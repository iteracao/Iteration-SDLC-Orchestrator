using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Common;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class StartPlanImplementationRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionBridge _bridge;
    private readonly ISolutionPlannerAgent _agent;
    private readonly IArtifactStore _artifacts;

    public StartPlanImplementationRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionPlannerAgent agent,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _bridge = bridge;
        _agent = agent;
        _artifacts = artifacts;
    }

    public async Task<Guid> HandleAsync(StartPlanImplementationRunCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FindAsync([command.RequirementId], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        if (!string.Equals(requirement.Status, "designed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requirement must be in 'designed' status before planning can start.");
        }

        var solutionExists = await _db.Solutions.AnyAsync(x => x.Id == requirement.TargetSolutionId, ct);
        if (!solutionExists)
        {
            throw new InvalidOperationException("Target solution not found.");
        }

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.SolutionId == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution setup not found.");

        var designReport = await _db.DesignReports
            .Where(x => x.WorkflowRunId == requirement.WorkflowRunId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (designReport is null)
        {
            throw new InvalidOperationException("Design report not found for requirement.");
        }

        var workflow = await _config.GetWorkflowAsync("plan-implementation", ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var run = new WorkflowRun(requirement.Id, null, solution.SolutionId, workflow.Code, command.RequestedBy);
        run.Start("implementation-planning");
        requirement.MarkUnderPlanning(run.Id);

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var snapshot = await _bridge.GetSolutionSnapshotAsync(solution, ct);
        var searchQuery = $"{requirement.Title} {requirement.Description}".Trim();
        var hits = await _bridge.SearchFilesAsync(solution, searchQuery, ct);

        var sampleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits.Take(5))
        {
            sampleFiles[hit.RelativePath] = await _bridge.ReadFileAsync(solution, hit.RelativePath, ct);
        }

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        var request = new SolutionPlanRequest(
            run.Id,
            solution.SolutionId,
            requirement.Id,
            designReport.WorkflowRunId,
            workflow.Code,
            workflow.Name,
            workflow.Purpose,
            requirement.Title,
            requirement.Description,
            designReport.Summary,
            designReport.Status,
            designReport.ArtifactsJson,
            designReport.GeneratedOpenQuestionsJson,
            designReport.GeneratedDecisionsJson,
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
            var result = await _agent.PlanAsync(request, agentDef, ct);

            taskRun.Succeed(result.RawJson);

            var report = new Domain.Reports.PlanReport(
                run.Id,
                requirement.Id,
                result.Summary,
                result.Status,
                result.ArtifactsJson,
                result.GeneratedBacklogItemsJson,
                result.GeneratedOpenQuestionsJson,
                result.GeneratedDecisionsJson,
                result.DocumentationUpdatesJson,
                result.KnowledgeUpdatesJson,
                result.RecommendedNextWorkflowCodesJson,
                result.RawJson);

            _db.PlanReports.Add(report);
            PersistGeneratedBacklogItems(result, requirement.TargetSolutionId, requirement.Id, run.Id);
            PersistGeneratedOpenQuestions(result, requirement.TargetSolutionId, requirement.Id, run.Id);
            PersistGeneratedDecisions(result, requirement.TargetSolutionId, requirement.Id, run.Id);

            run.Succeed("implementation-planning-completed");
            requirement.MarkPlanned(run.Id);

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "plan-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "implementation-plan.json", result.RawJson, ct);
            return run.Id;
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("implementation-planning", ex.Message);
            requirement.MarkPlanningFailed(run.Id);
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
            $"AI/solutions/{solution.Code}/analysis/latest-analysis.md",
            $"AI/solutions/{solution.Code}/design/latest-design.md"
        };

        var docs = new List<TextDocumentInput>();
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(solution.RepositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    private void PersistGeneratedBacklogItems(SolutionPlanResult result, Guid targetSolutionId, Guid requirementId, Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedBacklogItemDto>(result.GeneratedBacklogItemsJson);
        var fallbackOrder = 1;
        foreach (var item in items.OrderBy(x => x.Order ?? int.MaxValue))
        {
            _db.BacklogItems.Add(new BacklogItem(
                targetSolutionId,
                requirementId,
                workflowRunId,
                item.Order ?? fallbackOrder,
                FirstNonEmpty(item.Title, $"Planned implementation slice {fallbackOrder}"),
                FirstNonEmpty(item.Description, item.Title, "Planned implementation work item."),
                FirstNonEmpty(item.WorkflowCode, "implement-solution-change"),
                ParsePriority(item.Priority)));
            fallbackOrder++;
        }
    }

    private void PersistGeneratedOpenQuestions(SolutionPlanResult result, Guid targetSolutionId, Guid requirementId, Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedOpenQuestionDto>(result.GeneratedOpenQuestionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Open question from planning");
            var description = FirstNonEmpty(item.Description, title);
            _db.OpenQuestions.Add(new OpenQuestion(
              targetSolutionId,
              requirementId,
              workflowRunId,
              null,
              title,
              description,
              item.Category,
              FirstNonEmpty(item.Status, "open"),
              item.ResolutionNotes,
              ParseDateOrDefault(item.RaisedAtUtc, DateTime.UtcNow),
              ParseNullableDate(item.ResolvedAtUtc)));
       }
    }

    private void PersistGeneratedDecisions(SolutionPlanResult result, Guid targetSolutionId, Guid requirementId, Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedDecisionDto>(result.GeneratedDecisionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Decision from planning");
            var summary = FirstNonEmpty(item.Summary, title);
            _db.Decisions.Add(new Decision(
                 targetSolutionId,
                 requirementId,
                 workflowRunId,
                 null,
                 title,
                 summary,
                 FirstNonEmpty(item.DecisionType, "planning"),
                 FirstNonEmpty(item.Status, "proposed"),
                 item.Rationale,
                 SerializeStringList(item.Consequences),
                 SerializeStringList(item.AlternativesConsidered),
                 ParseDateOrDefault(item.DecidedAtUtc, DateTime.UtcNow)));
        }
    }

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
        catch { return []; }
    }

    private static PriorityLevel ParsePriority(string? value)
        => Enum.TryParse<PriorityLevel>(value, true, out var parsed) ? parsed : PriorityLevel.Medium;

    private static string SerializeStringList(IEnumerable<string>? values)
        => JsonSerializer.Serialize(values?.Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() ?? []);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : fallback;

    private static DateTime? ParseNullableDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : null;

    private sealed class GeneratedBacklogItemDto
    {
        public int? Order { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? WorkflowCode { get; set; }
        public string? Priority { get; set; }
    }

    private sealed class GeneratedOpenQuestionDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Status { get; set; }
        public string? ResolutionNotes { get; set; }
        public string? RaisedBy { get; set; }
        public string? AssignedTo { get; set; }
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
        public string? DecidedBy { get; set; }
        public string? DecidedAtUtc { get; set; }
    }
}
