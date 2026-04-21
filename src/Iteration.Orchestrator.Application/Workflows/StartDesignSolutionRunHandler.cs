using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Decisions;
using Iteration.Orchestrator.Domain.Questions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Iteration.Orchestrator.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class StartDesignSolutionRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionBridge _bridge;
    private readonly ISolutionDesignerAgent _agent;
    private readonly IArtifactStore _artifacts;
    private readonly IWorkflowExecutionQueue _queue;
    private readonly IWorkflowRunLogStore _logs;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public StartDesignSolutionRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionDesignerAgent agent,
        IArtifactStore artifacts,
        IWorkflowExecutionQueue queue,
        IWorkflowRunLogStore logs,
        WorkflowLifecycleService workflowLifecycle)
    {
        _db = db;
        _config = config;
        _bridge = bridge;
        _agent = agent;
        _artifacts = artifacts;
        _queue = queue;
        _logs = logs;
        _workflowLifecycle = workflowLifecycle;
    }

    public async Task<Guid> HandleAsync(StartDesignSolutionRunCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FindAsync([command.RequirementId], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        if (!WorkflowLifecycleCatalog.CanStartWorkflow("design-solution-change", requirement.Status))
        {
            throw new InvalidOperationException("Requirement must be in a valid status before design can start.");
        }

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        await _workflowLifecycle.EnsureNoBlockingRunAsync(requirement.Id, "design-solution-change", null, ct);

        var workflow = await _config.GetWorkflowAsync("design-solution-change", ct);

        var run = new WorkflowRun(requirement.Id, null, solution.Id, workflow.Code, command.RequestedBy);
        requirement.AdvanceLifecycle(run.Id, WorkflowLifecycleCatalog.GetWorkflowRequirementState("design-solution-change")!);

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _logs.AppendLineAsync(run.Id, "Workflow run created and queued.", ct);
        await _queue.EnqueueAsync(run.Id, ct);
        return run.Id;
    }

    public async Task ExecuteAsync(Guid workflowRunId, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == workflowRunId, ct)
            ?? throw new InvalidOperationException("Workflow run not found.");

        if (run.RequirementId is null)
        {
            throw new InvalidOperationException("Workflow run is not linked to a requirement.");
        }

        var requirement = await _db.Requirements.FindAsync([run.RequirementId.Value], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var analysisRun = await _workflowLifecycle.GetLatestValidatedRunAsync(requirement.Id, "analyze-request", ct);

        var analysisReport = await _db.AnalysisReports
            .Where(x => x.WorkflowRunId == analysisRun.Id)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (analysisReport is null)
        {
            throw new InvalidOperationException("Analysis report not found for requirement.");
        }

        var workflow = await _config.GetWorkflowAsync("design-solution-change", ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        await _logs.AppendLineAsync(run.Id, "Background workflow execution started.", ct);

        run.Start("solution-design");
        await _db.SaveChangesAsync(ct);

        var repositoryFiles = await RepositoryPromptInputDiscovery.LoadRepositoryFilesAsync(solution, ct);
        var repositoryDocumentationFiles = RepositoryPromptInputDiscovery.GetFrameworkDocumentationFiles(solution.RepositoryPath);
        var searchQuery = $"{requirement.Title} {requirement.Description}".Trim();
        var hits = await _bridge.SearchFilesAsync(solution, searchQuery, null, ct);

        var repositoryEvidenceFiles = new List<WorkflowFileReference>();
        foreach (var hit in hits.Take(5))
        {
            repositoryEvidenceFiles.Add(new WorkflowFileReference(hit.RelativePath, InferFileKind(hit.RelativePath), "Repository evidence hit", "repository-search"));
        }

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        await _logs.AppendSectionAsync(run.Id, "Input summary", ct);
        await _logs.AppendKeyValuesAsync(run.Id, "Input summary", new Dictionary<string, string?>
        {
            ["Requirement"] = WorkflowInputTextNormalizer.NormalizeSingleLine(requirement.Title),
            ["Target"] = solution.Code,
            ["Framework docs available"] = repositoryDocumentationFiles.Count.ToString(),
            ["Solution docs available"] = solutionKnowledgeDocuments.Count.ToString(),
            ["Repository files available"] = repositoryFiles.Count.ToString(),
            ["Search hits"] = hits.Count.ToString()
        }, ct);

        var request = new SolutionDesignRequest(
            run.Id,
            solution.Id,
            requirement.Id,
            analysisReport.WorkflowRunId,
            workflow.Code,
            workflow.Name,
            WorkflowInputTextNormalizer.NormalizeMultiline(workflow.Purpose),
            WorkflowInputTextNormalizer.NormalizeSingleLine(requirement.Title),
            WorkflowInputTextNormalizer.NormalizeMultiline(requirement.Description),
            analysisReport.Summary,
            analysisReport.Status,
            analysisReport.ArtifactsJson,
            analysisReport.GeneratedOpenQuestionsJson,
            analysisReport.GeneratedDecisionsJson,
            BuildProfileSummary(profile),
            BuildProfileRuleFiles(profile),
            solutionKnowledgeDocuments,
            repositoryEvidenceFiles,
            workflow.ProducedArtifacts,
            workflow.KnowledgeUpdates,
            workflow.ExecutionRules,
            workflow.NextWorkflows,
            repositoryFiles,
            repositoryDocumentationFiles,
            solution.RepositoryPath);

        var inputJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true });
        await _logs.AppendLineAsync(run.Id, "Workflow request prepared.", ct);
        await _artifacts.SaveTextAsync(run.Id, "workflow-input.json", inputJson, ct);
        var taskRun = new AgentTaskRun(run.Id, agentDef.Code, inputJson);
        taskRun.Start();
        _db.AgentTaskRuns.Add(taskRun);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _logs.AppendSectionAsync(run.Id, "Model call", ct);
            await _logs.AppendLineAsync(run.Id, "Calling workflow agent.", ct);
            var result = await _agent.DesignAsync(request, agentDef, ct);

            taskRun.Succeed(result.RawJson);

            var report = new Domain.Reports.DesignReport(
                run.Id,
                requirement.Id,
                result.Summary,
                result.Status,
                result.ArtifactsJson,
                result.GeneratedOpenQuestionsJson,
                result.GeneratedDecisionsJson,
                result.DocumentationUpdatesJson,
                result.KnowledgeUpdatesJson,
                result.RecommendedNextWorkflowCodesJson,
                result.RawJson);

            _db.DesignReports.Add(report);
            PersistGeneratedOpenQuestions(result, requirement.TargetSolutionId, requirement.Id, run.Id);
            PersistGeneratedDecisions(result, requirement.TargetSolutionId, requirement.Id, run.Id);

            run.Complete("solution-design-completed-awaiting-validation");

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "design-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "design-report.json", result.RawJson, ct);
            await _logs.AppendSectionAsync(run.Id, "Result", ct);
            await _logs.AppendLineAsync(run.Id, "Workflow completed successfully.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _logs.AppendSectionAsync(run.Id, "Cancelled", CancellationToken.None);
            await _logs.AppendLineAsync(run.Id, "Workflow execution cancelled while the agent was running.", CancellationToken.None);
            taskRun.Fail("Workflow execution cancelled.");
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _logs.AppendSectionAsync(run.Id, "Error", CancellationToken.None);
            await _logs.AppendLineAsync(run.Id, "Workflow execution failed.", CancellationToken.None);
            await _logs.AppendKeyValuesAsync(run.Id, "Error", new Dictionary<string, string?>
            {
                ["Type"] = ex.GetType().Name,
                ["Message"] = ex.Message
            }, CancellationToken.None);
            await _artifacts.SaveTextAsync(run.Id, "workflow-exception.txt", ex.ToString(), CancellationToken.None);
            taskRun.Fail(ex.Message);
            run.Fail("solution-design", ex.Message);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private Task<IReadOnlyList<WorkflowFileReference>> LoadSolutionKnowledgeDocumentsAsync(
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

        var docs = new List<WorkflowFileReference>();
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(solution.RepositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            docs.Add(new WorkflowFileReference(relativePath, "markdown", "Solution knowledge document", "solution-knowledge"));
        }

        return Task.FromResult<IReadOnlyList<WorkflowFileReference>>(docs);
    }

    private static IReadOnlyList<WorkflowFileReference> BuildProfileRuleFiles(ProfileDefinition profile)
    {
        return profile.Rules
            .Select(rule => new WorkflowFileReference(rule.Path, "markdown", "Profile rule", "profile"))
            .ToArray();
    }

    private static string InferFileKind(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".md" => "markdown",
            ".json" => "json",
            ".cs" => "source",
            ".csproj" => "project",
            ".sln" => "solution",
            ".yml" or ".yaml" => "yaml",
            _ => "file"
        };
    }

    private static string BuildProfileSummary(ProfileDefinition profile)
    {
        var parts = new List<string>();

        var name = WorkflowInputTextNormalizer.NormalizeSingleLine(profile.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name);
        }

        var description = WorkflowInputTextNormalizer.NormalizeMultiline(profile.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description);
        }

        if (profile.Rules.Count > 0)
        {
            var rules = string.Join("\n", profile.Rules.Select(rule => $"- {rule.Path}"));
            parts.Add($"Rules included:\n{rules}");
        }

        return string.Join("\n\n", parts);
    }

    private void PersistGeneratedOpenQuestions(
        SolutionDesignResult result,
        Guid targetSolutionId,
        Guid requirementId,
        Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedOpenQuestionDto>(result.GeneratedOpenQuestionsJson);
        foreach (var item in items)
        {
            var title = FirstNonEmpty(item.Title, "Open question from design");
            var description = FirstNonEmpty(item.Description, title);
            var createdAtUtc = ParseDateOrDefault(item.RaisedAtUtc, DateTime.UtcNow);
            var resolvedAtUtc = ParseNullableDate(item.ResolvedAtUtc);

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
        SolutionDesignResult result,
        Guid targetSolutionId,
        Guid requirementId,
        Guid workflowRunId)
    {
        var items = DeserializeList<GeneratedDecisionDto>(result.GeneratedDecisionsJson);
        foreach (var item in items)
        {
            _db.Decisions.Add(new Decision(
                targetSolutionId,
                requirementId,
                workflowRunId,
                null,
                FirstNonEmpty(item.Title, "Decision from design"),
                FirstNonEmpty(item.Summary, item.Title ?? "Decision from design"),
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
