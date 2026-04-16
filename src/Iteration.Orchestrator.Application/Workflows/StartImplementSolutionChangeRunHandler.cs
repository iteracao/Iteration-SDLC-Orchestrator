using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Reports;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class StartImplementSolutionChangeRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionBridge _bridge;
    private readonly ISolutionImplementationAgent _agent;
    private readonly IArtifactStore _artifacts;

    public StartImplementSolutionChangeRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionImplementationAgent agent,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _bridge = bridge;
        _agent = agent;
        _artifacts = artifacts;
    }

    public async Task<Guid> HandleAsync(StartImplementSolutionChangeRunCommand command, CancellationToken ct)
    {
        var backlogItem = await _db.BacklogItems.FindAsync([command.BacklogItemId], ct)
            ?? throw new InvalidOperationException("Backlog item not found.");

        if (!backlogItem.RequirementId.HasValue)
        {
            throw new InvalidOperationException("Backlog item is not linked to a requirement.");
        }

        if (!backlogItem.PlanWorkflowRunId.HasValue)
        {
            throw new InvalidOperationException("Backlog item is not linked to a planning workflow run.");
        }

        if (!backlogItem.CanStartImplementationAttempt())
        {
            throw new InvalidOperationException("Backlog item is not eligible for a new implementation attempt.");
        }

        var requirement = await _db.Requirements.FindAsync([backlogItem.RequirementId.Value], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.SolutionId == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution setup not found.");

        var blockingItemsExist = await _db.BacklogItems
            .Where(x => x.RequirementId == requirement.Id)
            .Where(x => x.PlanWorkflowRunId == backlogItem.PlanWorkflowRunId)
            .Where(x => x.PlanningOrder < backlogItem.PlanningOrder)
            .Where(x => x.Status != BacklogItemStatus.Validated && x.Status != BacklogItemStatus.Canceled)
            .AnyAsync(ct);

        if (blockingItemsExist)
        {
            throw new InvalidOperationException("A previous backlog item must be validated before this implementation can start.");
        }

        var planReport = await _db.PlanReports.FirstOrDefaultAsync(x => x.WorkflowRunId == backlogItem.PlanWorkflowRunId.Value, ct)
            ?? throw new InvalidOperationException("Plan report not found for backlog item.");

        var workflow = await _config.GetWorkflowAsync("implement-solution-change", ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var run = new WorkflowRun(requirement.Id, backlogItem.Id, solution.SolutionId, workflow.Code, command.RequestedBy);
        run.Start("implementation");
        requirement.MarkImplementing(run.Id);

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var snapshot = await _bridge.GetSolutionSnapshotAsync(solution, ct);
        var searchQuery = $"{requirement.Title} {backlogItem.Title} {backlogItem.Description}".Trim();
        var hits = await _bridge.SearchFilesAsync(solution, searchQuery, ct);

        var sampleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits.Take(8))
        {
            sampleFiles[hit.RelativePath] = await _bridge.ReadFileAsync(solution, hit.RelativePath, ct);
        }

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        var request = new SolutionImplementationRequest(
            run.Id,
            solution.SolutionId,
            requirement.Id,
            backlogItem.Id,
            backlogItem.PlanWorkflowRunId.Value,
            workflow.Code,
            workflow.Name,
            workflow.Purpose,
            requirement.Title,
            requirement.Description,
            backlogItem.Title,
            backlogItem.Description,
            backlogItem.PlanningOrder,
            planReport.Summary,
            planReport.Status,
            planReport.GeneratedBacklogItemsJson,
            planReport.GeneratedOpenQuestionsJson,
            planReport.GeneratedDecisionsJson,
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
            var result = await _agent.ImplementAsync(request, agentDef, ct);

            taskRun.Succeed(result.RawJson);

            var report = new ImplementationReport(
                run.Id,
                requirement.Id,
                backlogItem.Id,
                result.Summary,
                result.Status,
                result.ImplementedChangesJson,
                result.FilesTouchedJson,
                result.TestsExecutedJson,
                result.GeneratedRequirementsJson,
                result.GeneratedOpenQuestionsJson,
                result.GeneratedDecisionsJson,
                result.DocumentationUpdatesJson,
                result.KnowledgeUpdatesJson,
                result.RecommendedNextWorkflowCodesJson,
                result.RawJson);

            _db.ImplementationReports.Add(report);
            PersistGeneratedRequirements(result, requirement.TargetSolutionId, requirement.Id, backlogItem.Id, run.Id);
            PersistGeneratedOpenQuestions(result, requirement.TargetSolutionId, requirement.Id, backlogItem.Id, run.Id);
            PersistGeneratedDecisions(result, requirement.TargetSolutionId, requirement.Id, backlogItem.Id, run.Id);

            backlogItem.MarkAwaitingValidation();
            requirement.MarkAwaitingImplementationValidation(run.Id);
            run.Succeed("implementation-completed-awaiting-validation");

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "implementation-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "implementation-result.json", result.RawJson, ct);
            return run.Id;
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("implementation", ex.Message);
            backlogItem.MarkImplementationError();
            requirement.MarkImplementationFailed(run.Id);
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<IReadOnlyList<TextDocumentInput>> LoadSolutionKnowledgeDocumentsAsync(
        Domain.Solutions.SolutionTarget solution,
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
            $"AI/solutions/{solution.Code}/design/latest-design.md",
            $"AI/solutions/{solution.Code}/delivery/latest-plan.md"
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

    private void PersistGeneratedRequirements(
        SolutionImplementationResult result,
        Guid targetSolutionId,
        Guid requirementId,
        Guid backlogItemId,
        Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedRequirementDto>(result.GeneratedRequirementsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Generated requirement from implementation");
            var description = FirstNonEmpty(item.Description, title);
            _db.Requirements.Add(new Requirement(
                targetSolutionId,
                backlogItemId,
                workflowRunId,
                requirementId,
                title,
                description,
                FirstNonEmpty(item.RequirementType, "functional"),
                FirstNonEmpty(item.Source, "implementation"),
                FirstNonEmpty(item.Status, "submitted"),
                FirstNonEmpty(item.Priority, "medium"),
                item.AcceptanceCriteriaJson ?? "[]",
                item.ConstraintsJson ?? "[]",
                DateTime.UtcNow,
                null));
        }
    }

    private void PersistGeneratedOpenQuestions(
        SolutionImplementationResult result,
        Guid targetSolutionId,
        Guid requirementId,
        Guid backlogItemId,
        Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedOpenQuestionDto>(result.GeneratedOpenQuestionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Open question from implementation");
            var description = FirstNonEmpty(item.Description, title);
            _db.OpenQuestions.Add(new OpenQuestion(
                targetSolutionId,
                requirementId,
                workflowRunId,
                backlogItemId,
                title,
                description,
                item.Category,
                FirstNonEmpty(item.Status, "open"),
                item.ResolutionNotes,
                ParseDateOrDefault(item.RaisedAtUtc, DateTime.UtcNow),
                ParseNullableDate(item.ResolvedAtUtc)));
        }
    }

    private void PersistGeneratedDecisions(
        SolutionImplementationResult result,
        Guid targetSolutionId,
        Guid requirementId,
        Guid backlogItemId,
        Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedDecisionDto>(result.GeneratedDecisionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Decision from implementation");
            var summary = FirstNonEmpty(item.Summary, title);
            _db.Decisions.Add(new Decision(
                targetSolutionId,
                requirementId,
                workflowRunId,
                backlogItemId,
                title,
                summary,
                FirstNonEmpty(item.DecisionType, "implementation"),
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

    private static string SerializeStringList(IEnumerable<string>? values)
        => JsonSerializer.Serialize(values?.Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() ?? []);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : fallback;

    private static DateTime? ParseNullableDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : null;

    private sealed class GeneratedRequirementDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? RequirementType { get; set; }
        public string? Source { get; set; }
        public string? Status { get; set; }
        public string? Priority { get; set; }
        public string? AcceptanceCriteriaJson { get; set; }
        public string? ConstraintsJson { get; set; }
    }

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
