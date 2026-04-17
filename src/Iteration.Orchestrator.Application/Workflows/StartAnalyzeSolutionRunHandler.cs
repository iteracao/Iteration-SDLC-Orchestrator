using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

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
        var requirement = await _db.Requirements.FindAsync([command.RequirementId], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var workflow = await _config.GetWorkflowAsync("analyze-request", ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var run = new WorkflowRun(requirement.Id, null, solution.Id, workflow.Code, command.RequestedBy);
        run.Start("request-analysis");
        requirement.MarkUnderAnalysis(run.Id);

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var snapshot = await _bridge.GetSolutionSnapshotAsync(solution, ct);
        var hits = await _bridge.SearchFilesAsync(solution, requirement.Title, ct);

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
            requirement.Title,
            requirement.Description,
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
            var requirementMap = PersistRequirements(result, requirement, run.Id);
            PersistGeneratedOpenQuestions(result, requirement.TargetSolutionId, run.Id, requirementMap);
            PersistGeneratedDecisions(result, requirement.TargetSolutionId, run.Id, requirementMap);

            run.Succeed("analysis-completed");
            requirement.MarkAnalyzed(run.Id);

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "analysis-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "analysis-report.json", result.RawJson, ct);
            return run.Id;
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("request-analysis", ex.Message);
            requirement.MarkAnalysisFailed(run.Id);
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

    private Dictionary<string, Guid> PersistRequirements(
        SolutionAnalysisResult result,
        Requirement primaryRequirement,
        Guid workflowRunId)
    {
        var requirementMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            [$"requirement:{primaryRequirement.Id}"] = primaryRequirement.Id,
            ["primary"] = primaryRequirement.Id
        };

        primaryRequirement.UpdateFromAnalysis(
            workflowRunId,
            primaryRequirement.Title,
            primaryRequirement.Description,
            primaryRequirement.RequirementType,
            primaryRequirement.Source,
            "analyzed",
            primaryRequirement.Priority,
            primaryRequirement.AcceptanceCriteriaJson,
            primaryRequirement.ConstraintsJson,
            DateTime.UtcNow);

        var generatedRequirements = DeserializeList<GeneratedRequirementDto>(result.GeneratedRequirementsJson);
        foreach (var item in generatedRequirements)
        {
            var parentRequirementId = ResolveRequirementReference(item.ParentRequirementId, requirementMap, primaryRequirement.Id);
            var requirement = new Requirement(
                primaryRequirement.TargetSolutionId,
                null,
                workflowRunId,
                parentRequirementId,
                FirstNonEmpty(item.Title, "Derived requirement from analysis"),
                FirstNonEmpty(item.Description, primaryRequirement.Description),
                item.RequirementType ?? "functional",
                item.Source ?? "analysis",
                item.Status ?? "submitted",
                item.Priority ?? primaryRequirement.Priority,
                SerializeStringList(item.AcceptanceCriteria),
                SerializeStringList(item.Constraints),
                ParseDateOrDefault(item.SubmittedAtUtc, DateTime.UtcNow),
                ParseNullableDate(item.LastUpdatedAtUtc));

            _db.Requirements.Add(requirement);

            if (!string.IsNullOrWhiteSpace(item.RequirementId))
            {
                requirementMap[item.RequirementId.Trim()] = requirement.Id;
            }
        }

        return requirementMap;
    }

    private void PersistGeneratedOpenQuestions(
        SolutionAnalysisResult result,
        Guid targetSolutionId,
        Guid workflowRunId,
        IReadOnlyDictionary<string, Guid> requirementMap)
    {
        var items = DeserializeList<GeneratedOpenQuestionDto>(result.GeneratedOpenQuestionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Open question from analysis");
            var description = FirstNonEmpty(item.Description, title);
            var createdAtUtc = ParseDateOrDefault(item.RaisedAtUtc, DateTime.UtcNow);
            var resolvedAtUtc = ParseNullableDate(item.ResolvedAtUtc);
            var requirementId = ResolveRequirementReference(item.RequirementId, requirementMap, null);

            _db.OpenQuestions.Add(new OpenQuestion(
                targetSolutionId,
                requirementId,
                workflowRunId,
                null,
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
        IReadOnlyDictionary<string, Guid> requirementMap)
    {
        var items = DeserializeList<GeneratedDecisionDto>(result.GeneratedDecisionsJson);
        foreach (var item in items)
        {
            var requirementId = ResolveRequirementReference(item.RequirementId, requirementMap, null);

            _db.Decisions.Add(new Decision(
                targetSolutionId,
                requirementId,
                workflowRunId,
                null,
                FirstNonEmpty(item.Title, "Decision from analysis"),
                FirstNonEmpty(item.Summary, item.Title ?? "Decision from analysis"),
                item.DecisionType ?? "technical",
                item.Status ?? "proposed",
                item.Rationale,
                SerializeStringList(item.Consequences),
                SerializeStringList(item.AlternativesConsidered),
                ParseDateOrDefault(item.DecidedAtUtc, DateTime.UtcNow)));
        }
    }

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback)
        => DateTime.TryParse(value, out var parsed) ? parsed : fallback;

    private static DateTime? ParseNullableDate(string? value)
        => DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static string SerializeStringList(IEnumerable<string>? values)
        => JsonSerializer.Serialize((values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());

    private static Guid? ResolveRequirementReference(
        string? reference,
        IReadOnlyDictionary<string, Guid> requirementMap,
        Guid? fallback)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return fallback;
        }

        if (Guid.TryParse(reference, out var directId))
        {
            return directId;
        }

        return requirementMap.TryGetValue(reference.Trim(), out var mapped) ? mapped : fallback;
    }

    private static List<T> DeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class GeneratedRequirementDto
    {
        public string? RequirementId { get; set; }
        public string? ParentRequirementId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? RequirementType { get; set; }
        public string? Source { get; set; }
        public string? Status { get; set; }
        public string? Priority { get; set; }
        public List<string>? AcceptanceCriteria { get; set; }
        public List<string>? Constraints { get; set; }
        public string? SubmittedAtUtc { get; set; }
        public string? LastUpdatedAtUtc { get; set; }
    }

    private sealed class GeneratedOpenQuestionDto
    {
        public string? RequirementId { get; set; }
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
        public string? RequirementId { get; set; }
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
