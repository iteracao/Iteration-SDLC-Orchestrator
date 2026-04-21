using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
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
    private readonly IWorkflowExecutionQueue _queue;
    private readonly IWorkflowRunLogStore _logs;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public StartAnalyzeSolutionRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionAnalystAgent agent,
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

    public async Task<Guid> HandleAsync(StartAnalyzeSolutionRunCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FindAsync([command.RequirementId], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        if (!WorkflowLifecycleCatalog.CanStartWorkflow("analyze-request", requirement.Status))
        {
            throw new InvalidOperationException("Requirement must be in a valid status before analysis can start.");
        }

        await _workflowLifecycle.EnsureNoBlockingRunAsync(requirement.Id, "analyze-request", null, ct);

        var workflow = await _config.GetWorkflowAsync("analyze-request", ct);

        var run = new WorkflowRun(requirement.Id, null, solution.Id, workflow.Code, command.RequestedBy);
        requirement.AdvanceLifecycle(run.Id, WorkflowLifecycleCatalog.GetWorkflowRequirementState("analyze-request")!);

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

        if (!string.Equals(run.WorkflowCode, "analyze-request", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workflow run is not an analyze-request run.");
        }

        if (run.RequirementId is null)
        {
            throw new InvalidOperationException("Workflow run is not linked to a requirement.");
        }

        var requirement = await _db.Requirements.FindAsync([run.RequirementId.Value], ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var solution = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == requirement.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var workflow = await _config.GetWorkflowAsync("analyze-request", ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        await _logs.AppendLineAsync(run.Id, "Background workflow execution started.", ct);

        run.Start("request-analysis");
        await _db.SaveChangesAsync(ct);

        var repositoryFiles = await RepositoryPromptInputDiscovery.LoadVisibleRepositoryFilesAsync(solution.RepositoryPath, ct);
        var repositoryDocumentationFiles = RepositoryPromptInputDiscovery.GetFrameworkDocumentationFiles(solution.RepositoryPath);
        var searchQuery = $"{requirement.Title} {requirement.Description}".Trim();
        var hits = await _bridge.SearchFilesAsync(solution, searchQuery, null, ct);
        var repositoryEvidenceFiles = hits
            .Take(5)
            .Select(hit => new WorkflowFileReference(
                hit.RelativePath,
                InferFileKind(hit.RelativePath),
                "Repository evidence hit",
                "repository-search"))
            .ToArray();

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        var request = new SolutionAnalysisRequest(
            run.Id,
            solution.Id,
            workflow.Code,
            workflow.Name,
            WorkflowInputTextNormalizer.NormalizeMultiline(workflow.Purpose),
            WorkflowInputTextNormalizer.NormalizeSingleLine(requirement.Title),
            WorkflowInputTextNormalizer.NormalizeMultiline(requirement.Description),
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

        var taskRun = new AgentTaskRun(run.Id, agentDef.Code, inputJson);
        taskRun.Start();
        _db.AgentTaskRuns.Add(taskRun);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _logs.AppendSectionAsync(run.Id, "Model call", ct);
            await _logs.AppendLineAsync(run.Id, "Calling workflow agent.", ct);
            var result = await _agent.AnalyzeAsync(request, agentDef, solution, ct);

            taskRun.Succeed(result.ReportMarkdown);

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
                result.ReportMarkdown);

            _db.AnalysisReports.Add(report);
            PersistRequirementAnalysis(result, requirement, run.Id);

            run.Complete("analysis-completed-awaiting-validation");

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "analysis-report.md", result.ReportMarkdown, ct);
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
            run.Fail("request-analysis", ex.Message);
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
            var fullPath = Path.Combine(
                solution.RepositoryPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

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

    private static void PersistRequirementAnalysis(
        SolutionAnalysisResult result,
        Requirement primaryRequirement,
        Guid workflowRunId)
    {
        primaryRequirement.UpdateFromAnalysis(
            workflowRunId,
            primaryRequirement.Title,
            primaryRequirement.Description,
            primaryRequirement.RequirementType,
            primaryRequirement.Source,
            primaryRequirement.Status,
            primaryRequirement.Priority,
            primaryRequirement.AcceptanceCriteriaJson,
            primaryRequirement.ConstraintsJson,
            DateTime.UtcNow);
    }
}
