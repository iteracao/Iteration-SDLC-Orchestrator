using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed record SetupDocumentationCommand(
    Guid TargetSolutionId,
    string RequestedBy);

public sealed record SetupDocumentationExecutionResult(Guid WorkflowRunId);

public sealed class SetupDocumentationHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionDocumentationSetupAgent _agent;
    private readonly IArtifactStore _artifacts;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowExecutionQueue _queue;

    public SetupDocumentationHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionDocumentationSetupAgent agent,
        IArtifactStore artifacts,
        IWorkflowRunLogStore logs,
        IWorkflowExecutionQueue queue)
    {
        _db = db;
        _config = config;
        _agent = agent;
        _artifacts = artifacts;
        _logs = logs;
        _queue = queue;
    }

    public async Task<SetupDocumentationExecutionResult> HandleAsync(SetupDocumentationCommand command, CancellationToken ct)
    {
        var target = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == command.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var workflow = await _config.GetWorkflowAsync("setup-documentation", ct);

        var run = new WorkflowRun(null, null, target.Id, workflow.Code, command.RequestedBy);
        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _logs.AppendLineAsync(run.Id, "Workflow run created and queued.", ct);
        await _queue.EnqueueAsync(run.Id, ct);
        return new SetupDocumentationExecutionResult(run.Id);
    }

    public async Task ExecuteAsync(Guid workflowRunId, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == workflowRunId, ct)
            ?? throw new InvalidOperationException("Workflow run not found.");

        if (!string.Equals(run.WorkflowCode, "setup-documentation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workflow run is not a setup-documentation run.");
        }

        var target = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == run.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var solution = await _db.Solutions.FirstOrDefaultAsync(x => x.Id == target.SolutionId, ct)
            ?? throw new InvalidOperationException("Solution not found for target.");

        var workflow = await _config.GetWorkflowAsync("setup-documentation", ct);
        var profile = await _config.GetProfileAsync(target.ProfileCode, ct);
        var agentDefinition = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var visibleFiles = await RepositoryPromptInputDiscovery.LoadVisibleRepositoryFilesAsync(target.RepositoryPath, ct);
        var stableDocumentTargets = StableDocumentationCatalog.GetCanonicalRelativePaths();
        var existingStableDocumentPaths = StableDocumentationCatalog.GetExistingRepositoryRelativePaths(target.RepositoryPath, target.Code)
            .Select(path => NormalizeStableDocumentRelativePath(path, target.RepositoryPath, target.Code))
            .ToArray();
        var stableDocumentContextFiles = StableDocumentationCatalog.GetExistingRepositoryRelativePaths(target.RepositoryPath, target.Code);
        var localRepositoryDocumentationFiles = RepositoryPromptInputDiscovery.GetLocalRepositoryDocumentationFiles(visibleFiles);
        var repositorySourceFiles = RepositoryPromptInputDiscovery.GetRepositorySourceContextFiles(visibleFiles);
        var allowedContextFiles = stableDocumentContextFiles
            .Concat(localRepositoryDocumentationFiles)
            .Concat(repositorySourceFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var request = new SolutionDocumentationSetupRequest(
            run.Id,
            target.Id,
            workflow.Code,
            workflow.Name,
            workflow.Purpose,
            solution.Name,
            solution.Description,
            target.Code,
            BuildProfileSummary(profile),
            BuildProfileRuleFiles(profile),
            stableDocumentTargets,
            existingStableDocumentPaths,
            localRepositoryDocumentationFiles,
            repositorySourceFiles,
            allowedContextFiles,
            target.RepositoryPath);

        var inputJson = JsonSerializer.Serialize(request, JsonOptions);
        var taskRun = new AgentTaskRun(run.Id, agentDefinition.Code, inputJson);

        _db.AgentTaskRuns.Add(taskRun);

        await _logs.AppendLineAsync(run.Id, "Background workflow execution started.", ct);

        run.Start("documentation-setup");
        taskRun.Start();
        await _db.SaveChangesAsync(ct);

        try
        {
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow started.", ct);
            await _artifacts.SaveTextAsync(run.Id, "setup-documentation.input.json", inputJson, ct);

            var result = await _agent.RunAsync(request, agentDefinition, target, ct);

            var writeOutcome = ValidateDocumentationStateAfterAgentWrites(
                target.RepositoryPath,
                target.Code,
                stableDocumentTargets,
                existingStableDocumentPaths,
                result);

            var outputJson = JsonSerializer.Serialize(new
            {
                result.Mode,
                result.Summary,
                result.StableDocsFound,
                result.RepoDocsReviewed,
                result.SourceAreasReviewed,
                result.DriftFindings,
                result.OpenQuestions,
                DocumentsCreated = writeOutcome.Created,
                DocumentsUpdated = writeOutcome.Updated,
                DocumentsUnchanged = writeOutcome.Unchanged
            }, JsonOptions);

            await _artifacts.SaveTextAsync(run.Id, "setup-documentation.output.json", outputJson, ct);

            var repositoryStateMarkdown = await _artifacts.ReadTextAsync(run.Id, "repository-state.md", ct) ?? string.Empty;
            var finalDecisionMarkdown = NormalizeMarkdown(result.RawReportMarkdown) + Environment.NewLine;
            var visibleReportMarkdown = BuildVisibleSetupDocumentationReport(repositoryStateMarkdown, finalDecisionMarkdown);
            await _artifacts.SaveTextAsync(run.Id, "setup-documentation-report.md", visibleReportMarkdown, ct);
            await _artifacts.SaveTextAsync(run.Id, "workflow-report.md", visibleReportMarkdown, ct);

            taskRun.Succeed(outputJson);
            run.Complete("setup-documentation-completed");

            await _db.SaveChangesAsync(ct);
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow completed successfully.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow execution was cancelled by the background execution token.", CancellationToken.None);
            await _logs.AppendKeyValuesAsync(run.Id, "Cancellation", new Dictionary<string, string?>
            {
                ["FailureCode"] = WorkflowFailureCatalog.WorkflowExecutionCancelled,
                ["Message"] = "Documentation setup workflow execution cancelled by the background execution token."
            }, CancellationToken.None);
            taskRun.Fail(WorkflowFailureCatalog.Format(WorkflowFailureCatalog.WorkflowExecutionCancelled, "Documentation setup workflow execution cancelled by the background execution token."));
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var failure = WorkflowFailureCatalog.Classify(ex);
            await _artifacts.SaveTextAsync(run.Id, "workflow-exception.txt", ex.ToString(), CancellationToken.None);
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow failed.", CancellationToken.None);
            await _logs.AppendKeyValuesAsync(run.Id, "Error", new Dictionary<string, string?>
            {
                ["FailureCode"] = failure.Code,
                ["Type"] = ex.GetType().Name,
                ["Message"] = failure.Message
            }, CancellationToken.None);
            await _logs.AppendBlockAsync(run.Id, "Exception", ex.ToString(), CancellationToken.None);
            taskRun.Fail(WorkflowFailureCatalog.Format(failure.Code, failure.Message));
            run.Fail(run.CurrentStage, WorkflowFailureCatalog.Format(failure.Code, failure.Message));
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static DocumentationWriteOutcome ValidateDocumentationStateAfterAgentWrites(
        string repositoryPath,
        string solutionCode,
        IReadOnlyList<string> stableDocumentTargets,
        IReadOnlyList<string> existingStableDocumentPaths,
        SolutionDocumentationSetupResult result)
    {
        var knowledgeRoot = StableDocumentationCatalog.BuildKnowledgeRoot(repositoryPath, solutionCode);
        Directory.CreateDirectory(knowledgeRoot);

        var existingSet = existingStableDocumentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = result.DocumentsCreated.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = result.DocumentsUpdated.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var unchanged = result.DocumentsUnchanged.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        if (string.Equals(result.Mode, "bootstrap", StringComparison.OrdinalIgnoreCase)
            && !stableDocumentTargets.All(path => created.Contains(path, StringComparer.OrdinalIgnoreCase)
                || updated.Contains(path, StringComparer.OrdinalIgnoreCase)
                || unchanged.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Bootstrap mode must report the full canonical stable documentation set.");
        }

        if (string.Equals(result.Mode, "bootstrap", StringComparison.OrdinalIgnoreCase)
            && !stableDocumentTargets.All(path => created.Contains(path, StringComparer.OrdinalIgnoreCase)
                || updated.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Bootstrap mode must physically create or update every canonical stable documentation file; KEEP is not valid for placeholder-only bootstrap targets.");
        }

        var missingStableTargets = stableDocumentTargets.Where(path => !existingSet.Contains(path)).ToArray();
        if (!string.Equals(result.Mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && missingStableTargets.Any(path => !created.Contains(path, StringComparer.OrdinalIgnoreCase) && !updated.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Missing canonical stable documentation files must be created or updated during bootstrap or update mode.");
        }

        if (string.Equals(result.Mode, "aligned", StringComparison.OrdinalIgnoreCase) && (created.Length > 0 || updated.Length > 0))
        {
            throw new InvalidOperationException("Aligned mode must not rewrite stable documentation.");
        }

        foreach (var relativePath in created.Concat(updated).Concat(unchanged).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!stableDocumentTargets.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Reported documentation path '{relativePath}' is not part of the canonical stable document set.");
            }

            var fullPath = Path.Combine(knowledgeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Expected stable documentation file '{relativePath}' was not written to disk.");
            }
        }

        return new DocumentationWriteOutcome(created, updated, unchanged);
    }

    private static string BuildVisibleSetupDocumentationReport(string repositoryStateMarkdown, string finalDecisionMarkdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Setup Documentation Report");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(repositoryStateMarkdown))
        {
            sb.AppendLine("## Repository State");
            sb.AppendLine();
            sb.AppendLine(repositoryStateMarkdown.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## Final Decision");
        sb.AppendLine();
        sb.AppendLine(finalDecisionMarkdown.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildProfileSummary(ProfileDefinition profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Profile: {profile.Code}");
        builder.AppendLine(profile.Name);
        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            builder.AppendLine(profile.Description.Trim());
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<WorkflowFileReference> BuildProfileRuleFiles(ProfileDefinition profile)
       => profile.Rules
        .Select(rule => new WorkflowFileReference(
            rule.Path,
            "rule",
            string.Empty,
            "profile-rule"))
        .ToArray();

    private static string NormalizeStableDocumentRelativePath(string path, string repositoryPath, string solutionCode)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim();
        var knowledgeRoot = StableDocumentationCatalog.BuildKnowledgeRoot(repositoryPath, solutionCode).Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(path))
        {
            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            if (fullPath.StartsWith(knowledgeRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath[(knowledgeRoot.Length + 1)..];
            }
        }

        var marker = $"AI/solutions/{solutionCode}/";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return normalized;
        }

        return normalized[(index + marker.Length)..];
    }

    private static string NormalizeMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd().Replace("\n", Environment.NewLine);
    }

    private sealed record DocumentationWriteOutcome(
        IReadOnlyList<string> Created,
        IReadOnlyList<string> Updated,
        IReadOnlyList<string> Unchanged);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
