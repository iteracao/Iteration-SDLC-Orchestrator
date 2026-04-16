using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class StartAnalyzeSolutionRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionBridge _bridge;
    private readonly ISolutionAnalystAgent _agent;
    private readonly IArtifactStore _artifacts;

    public StartAnalyzeSolutionRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionAnalystAgent agent,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _bridge = bridge;
        _agent = agent;
        _artifacts = artifacts;
    }

    public async Task<Guid> HandleAsync(StartAnalyzeSolutionRunCommand command, CancellationToken ct)
    {
        var backlog = await _db.BacklogItems.FindAsync([command.BacklogItemId], ct)
            ?? throw new InvalidOperationException("Backlog item not found.");

        var solution = await _db.SolutionTargets.FindAsync([backlog.TargetSolutionId], ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var workflow = await _config.GetWorkflowAsync(backlog.WorkflowCode, ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var run = new WorkflowRun(backlog.Id, solution.Id, backlog.WorkflowCode, command.RequestedBy);
        run.Start("request-analysis");
        backlog.MarkInAnalysis();

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var snapshot = await _bridge.GetSolutionSnapshotAsync(solution, ct);
        var hits = await _bridge.SearchFilesAsync(solution, backlog.Title, ct);

        var sampleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits.Take(5))
        {
            sampleFiles[hit.RelativePath] = await _bridge.ReadFileAsync(solution, hit.RelativePath, ct);
        }

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        var request = new SolutionAnalysisRequest(
            run.Id,
            solution.Id,
            workflow.Code,
            workflow.Name,
            workflow.Purpose,
            backlog.Title,
            backlog.Description,
            BuildProfileSummary(profile),
            profile.Rules,
            solutionKnowledgeDocuments,
            workflow.ProducedArtifacts,
            workflow.KnowledgeUpdates,
            workflow.ExecutionRules,
            workflow.NextWorkflows,
            snapshot,
            hits,
            sampleFiles);

        var inputJson = JsonSerializer.Serialize(request);
        var taskRun = new AgentTaskRun(run.Id, agentDef.Code, inputJson);
        taskRun.Start();
        _db.AgentTaskRuns.Add(taskRun);
        await _db.SaveChangesAsync(ct);

        try
        {
            var result = await _agent.AnalyzeAsync(request, agentDef, ct);

            taskRun.Succeed(result.RawJson);

            var report = new Domain.Reports.AnalysisReport(
                run.Id,
                result.Summary,
                result.Status,
                result.ArtifactsJson,
                result.GeneratedRequirementsJson,
                result.GeneratedOpenQuestionsJson,
                result.GeneratedDecisionsJson,
                result.DocumentationUpdatesJson,
                result.KnowledgeUpdatesJson,
                result.RecommendedNextWorkflowCodesJson,
                result.RawJson);

            _db.AnalysisReports.Add(report);
            PersistGeneratedOpenQuestions(result, solution.Id, run.Id, backlog.Id);
            PersistGeneratedDecisions(result, solution.Id, run.Id, backlog.Id);

            run.Succeed("analysis-completed");
            backlog.MarkAnalysisCompleted();

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "analysis-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "analysis-report.json", result.RawJson, ct);
            return run.Id;
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("request-analysis", ex.Message);
            backlog.MarkFailed();
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<IReadOnlyList<TextDocumentInput>> LoadSolutionKnowledgeDocumentsAsync(
        SolutionTarget solution,
        CancellationToken ct)
    {
        var relativePaths = new[]
        {
            $"AI/solutions/{solution.Code}/context/overview.md",
            $"AI/solutions/{solution.Code}/business/business-rules.md",
            $"AI/solutions/{solution.Code}/business/workflows.md",
            $"AI/solutions/{solution.Code}/architecture/architecture-overview.md",
            $"AI/solutions/{solution.Code}/architecture/module-map.md",
            $"AI/solutions/{solution.Code}/history/decisions.md",
            $"AI/solutions/{solution.Code}/history/open-questions.md",
            $"AI/solutions/{solution.Code}/history/known-gaps.md",
            $"AI/solutions/{solution.Code}/analysis/latest-analysis.md"
        };

        var docs = new List<TextDocumentInput>();

        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(
                solution.RepositoryPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                var content = await _bridge.ReadFileAsync(solution, relativePath, ct);
                docs.Add(new TextDocumentInput(relativePath, content));
            }
            catch
            {
                // ignore individual file failures so analysis can continue
            }
        }

        return docs;
    }

    private static string BuildProfileSummary(ProfileDefinition profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine(profile.Name);
        sb.AppendLine(profile.Description);

        if (profile.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Rules included:");
            foreach (var rule in profile.Rules)
            {
                sb.AppendLine($"- {rule.Path}");
            }
        }

        return sb.ToString().Trim();
    }

    private void PersistGeneratedOpenQuestions(
        SolutionAnalysisResult result,
        Guid targetSolutionId,
        Guid workflowRunId,
        Guid backlogItemId)
    {
        var items = DeserializeList<GeneratedOpenQuestionDto>(result.GeneratedOpenQuestionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Open question from analysis");
            var description = FirstNonEmpty(item.Description, title);
            var createdAtUtc = ParseDateOrDefault(item.RaisedAtUtc, DateTime.UtcNow);
            var resolvedAtUtc = ParseNullableDate(item.ResolvedAtUtc);

            _db.OpenQuestions.Add(new OpenQuestion(
                targetSolutionId,
                workflowRunId,
                backlogItemId,
                title,
                description,
                item.Category,
                item.Status ?? "open",
                item.ResolutionNotes,
                createdAtUtc,
                resolvedAtUtc));
        }
    }

    private void PersistGeneratedDecisions(
        SolutionAnalysisResult result,
        Guid targetSolutionId,
        Guid workflowRunId,
        Guid backlogItemId)
    {
        var items = DeserializeList<GeneratedDecisionDto>(result.GeneratedDecisionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Analysis decision");
            var summary = FirstNonEmpty(item.Summary, title);
            var createdAtUtc = ParseDateOrDefault(item.DecidedAtUtc, DateTime.UtcNow);

            _db.Decisions.Add(new Decision(
                targetSolutionId,
                workflowRunId,
                backlogItemId,
                title,
                summary,
                item.DecisionType ?? "technical",
                item.Status ?? "proposed",
                item.Rationale,
                SerializeStringList(item.Consequences),
                SerializeStringList(item.AlternativesConsidered),
                createdAtUtc));
        }
    }

    private static List<T> DeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
    }

    private static string SerializeStringList(List<string>? values)
    {
        var normalized = values?
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList() ?? [];

        return JsonSerializer.Serialize(normalized);
    }

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback)
        => DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : fallback;

    private static DateTime? ParseNullableDate(string? value)
        => DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GeneratedOpenQuestionDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Status { get; set; }
        public string? ResolutionNotes { get; set; }
        public string? RaisedAtUtc { get; set; }
        public string? ResolvedAtUtc { get; set; }
    }

    private sealed class GeneratedDecisionDto
    {
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? DecisionType { get; set; }
        public string? Status { get; set; }
        public string? Rationale { get; set; }
        public List<string>? Consequences { get; set; }
        public List<string>? AlternativesConsidered { get; set; }
        public string? DecidedAtUtc { get; set; }
    }
}
