using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed record SetupDocumentationCommand(
    Guid TargetSolutionId,
    string RequestedBy);

public sealed record SetupDocumentationExecutionResult(
    Guid WorkflowRunId,
    string Mode,
    IReadOnlyList<string> StableDocsFound,
    IReadOnlyList<string> DriftFindings,
    IReadOnlyList<string> DocumentsCreated,
    IReadOnlyList<string> DocumentsUpdated,
    IReadOnlyList<string> DocumentsUnchanged,
    IReadOnlyList<string> OpenQuestions);

public sealed class SetupDocumentationHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionDocumentationSetupAgent _agent;
    private readonly IArtifactStore _artifacts;
    private readonly IWorkflowRunLogStore _logs;

    public SetupDocumentationHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionDocumentationSetupAgent agent,
        IArtifactStore artifacts,
        IWorkflowRunLogStore logs)
    {
        _db = db;
        _config = config;
        _agent = agent;
        _artifacts = artifacts;
        _logs = logs;
    }

    public async Task<SetupDocumentationExecutionResult> HandleAsync(SetupDocumentationCommand command, CancellationToken ct)
    {
        var target = await _db.SolutionTargets.FirstOrDefaultAsync(x => x.Id == command.TargetSolutionId, ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var solution = await _db.Solutions.FirstOrDefaultAsync(x => x.Id == target.SolutionId, ct)
            ?? throw new InvalidOperationException("Solution not found for target.");

        var workflow = await _config.GetWorkflowAsync("setup-documentation", ct);
        var profile = await _config.GetProfileAsync(target.ProfileCode, ct);
        var agentDefinition = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var visibleFiles = await RepositoryPromptInputDiscovery.LoadVisibleRepositoryFilesAsync(target.RepositoryPath, ct);
        var stableDocumentTargets = StableDocumentationCatalog.GetCanonicalRelativePaths();
        var existingStableDocumentPaths = StableDocumentationCatalog.GetExistingRepositoryRelativePaths(target.RepositoryPath, target.Code)
            .Select(path => ToWorkspaceRelativePath(path, target.Code))
            .ToArray();
        var stableDocumentContextFiles = StableDocumentationCatalog.GetExistingRepositoryRelativePaths(target.RepositoryPath, target.Code);
        var localRepositoryDocumentationFiles = RepositoryPromptInputDiscovery.GetLocalRepositoryDocumentationFiles(visibleFiles);
        var repositorySourceFiles = RepositoryPromptInputDiscovery.GetRepositorySourceContextFiles(visibleFiles);
        var allowedContextFiles = stableDocumentContextFiles
            .Concat(localRepositoryDocumentationFiles)
            .Concat(repositorySourceFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var run = new WorkflowRun(null, null, target.Id, workflow.Code, command.RequestedBy);
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

        _db.WorkflowRuns.Add(run);
        _db.AgentTaskRuns.Add(taskRun);

        run.Start("documentation-setup");
        taskRun.Start();

        await _db.SaveChangesAsync(ct);
        await _logs.AppendLineAsync(run.Id, "Documentation setup workflow started.", ct);

        try
        {
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
            await _artifacts.SaveTextAsync(run.Id, "setup-documentation-report.md", NormalizeMarkdown(result.RawReportMarkdown) + Environment.NewLine, ct);

            taskRun.Succeed(outputJson);
            run.Complete("setup-documentation-completed");
            run.Validate("setup-documentation-validated");

            await _db.SaveChangesAsync(ct);
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow completed successfully.", ct);

            return new SetupDocumentationExecutionResult(
                run.Id,
                result.Mode,
                result.StableDocsFound,
                result.DriftFindings,
                writeOutcome.Created,
                writeOutcome.Updated,
                writeOutcome.Unchanged,
                result.OpenQuestions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow was cancelled.", CancellationToken.None);
            taskRun.Fail("Documentation setup workflow cancelled.");
            run.Fail(run.CurrentStage, "Documentation setup workflow cancelled.");
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _artifacts.SaveTextAsync(run.Id, "workflow-exception.txt", ex.ToString(), CancellationToken.None);
            await _logs.AppendLineAsync(run.Id, "Documentation setup workflow failed.", CancellationToken.None);
            await _logs.AppendBlockAsync(run.Id, "Exception", ex.ToString(), CancellationToken.None);
            taskRun.Fail(ex.Message);
            run.Fail(run.CurrentStage, ex.Message);
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
                throw new InvalidOperationException($"Reported documentation path '{relativePath}' is not part of the canonical stable documentation set.");
            }

            var fullPath = Path.Combine(knowledgeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Reported documentation file '{relativePath}' does not exist after the agent write step.");
            }
        }

        return new DocumentationWriteOutcome(created, updated, unchanged);
    }

    private static string ToWorkspaceRelativePath(string repositoryRelativePath, string solutionCode)
    {
        var prefix = $"AI/solutions/{solutionCode}/";
        return repositoryRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? repositoryRelativePath[prefix.Length..]
            : repositoryRelativePath;
    }

    private static IReadOnlyList<WorkflowFileReference> BuildProfileRuleFiles(ProfileDefinition profile)
        => profile.Rules
            .Select(rule => new WorkflowFileReference(rule.Path, "markdown", "Profile rule", "profile"))
            .ToArray();

    private static string BuildProfileSummary(ProfileDefinition profile)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(profile.Name))
        {
            parts.Add(profile.Name.Trim());
        }

        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            parts.Add(profile.Description.Trim());
        }

        if (profile.Rules.Count > 0)
        {
            parts.Add("Rules included:\n" + string.Join("\n", profile.Rules.Select(rule => $"- {rule.Path}")));
        }

        return string.Join("\n\n", parts);
    }

    private static string NormalizeMarkdown(string value)
        => value.Replace("\r\n", "\n").Trim();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record DocumentationWriteOutcome(
        IReadOnlyList<string> Created,
        IReadOnlyList<string> Updated,
        IReadOnlyList<string> Unchanged);
}
