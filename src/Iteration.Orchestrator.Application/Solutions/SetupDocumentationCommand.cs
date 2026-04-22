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

            var writeOutcome = await ApplyDocumentationChangesAsync(
                target.RepositoryPath,
                target.Code,
                stableDocumentTargets,
                existingStableDocumentPaths,
                result,
                ct);

            var reportMarkdown = BuildReportMarkdown(result, writeOutcome);
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
                DocumentsUnchanged = writeOutcome.Unchanged,
                Documents = result.Documents
            }, JsonOptions);

            await _artifacts.SaveTextAsync(run.Id, "setup-documentation.output.json", outputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "setup-documentation-report.md", reportMarkdown, ct);

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

    private static async Task<DocumentationWriteOutcome> ApplyDocumentationChangesAsync(
        string repositoryPath,
        string solutionCode,
        IReadOnlyList<string> stableDocumentTargets,
        IReadOnlyList<string> existingStableDocumentPaths,
        SolutionDocumentationSetupResult result,
        CancellationToken ct)
    {
        var knowledgeRoot = StableDocumentationCatalog.BuildKnowledgeRoot(repositoryPath, solutionCode);
        Directory.CreateDirectory(knowledgeRoot);

        var existingSet = existingStableDocumentPaths
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var draftMap = result.Documents
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

        if (string.Equals(result.Mode, "bootstrap", StringComparison.OrdinalIgnoreCase)
            && !stableDocumentTargets.All(path => draftMap.ContainsKey(path)))
        {
            throw new InvalidOperationException("Bootstrap mode must provide content for the full canonical stable documentation set.");
        }

        var missingStableTargets = stableDocumentTargets
            .Where(path => !existingSet.Contains(path))
            .ToArray();

        if (!string.Equals(result.Mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && missingStableTargets.Any(path =>
                !draftMap.TryGetValue(path, out var draft)
                || string.Equals(draft.Action, "unchanged", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Missing canonical stable documentation files must be created during bootstrap or update mode.");
        }

        if (string.Equals(result.Mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && result.Documents.Any(x => !string.Equals(x.Action, "unchanged", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Aligned mode must not rewrite stable documentation.");
        }

        var created = new List<string>();
        var updated = new List<string>();
        var unchanged = new List<string>();

        foreach (var relativePath in stableDocumentTargets)
        {
            var fullPath = Path.Combine(knowledgeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            if (!draftMap.TryGetValue(relativePath, out var draft)
                || string.Equals(draft.Action, "unchanged", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(fullPath))
                {
                    unchanged.Add(relativePath);
                }

                continue;
            }

            var normalizedContent = NormalizeMarkdown(draft.Content);
            var fileExists = File.Exists(fullPath);
            if (fileExists)
            {
                var currentContent = NormalizeMarkdown(await File.ReadAllTextAsync(fullPath, ct));
                if (string.Equals(currentContent, normalizedContent, StringComparison.Ordinal))
                {
                    unchanged.Add(relativePath);
                    continue;
                }
            }

            await File.WriteAllTextAsync(fullPath, normalizedContent + Environment.NewLine, ct);

            if (fileExists || existingSet.Contains(relativePath))
            {
                updated.Add(relativePath);
            }
            else
            {
                created.Add(relativePath);
            }
        }

        foreach (var relativePath in stableDocumentTargets)
        {
            if (!created.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
                && !updated.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
                && !unchanged.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(knowledgeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                unchanged.Add(relativePath);
            }
        }

        return new DocumentationWriteOutcome(
            created.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            updated.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            unchanged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string BuildReportMarkdown(
        SolutionDocumentationSetupResult result,
        DocumentationWriteOutcome writeOutcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Setup Documentation Report");
        sb.AppendLine();
        sb.AppendLine($"- mode: {result.Mode}");
        sb.AppendLine();
        AppendListSection(sb, "Stable Docs Found", result.StableDocsFound);
        AppendListSection(sb, "Repo Docs Reviewed", result.RepoDocsReviewed);
        AppendListSection(sb, "Source Areas Reviewed", result.SourceAreasReviewed);
        AppendListSection(sb, "Drift Findings", result.DriftFindings);
        AppendListSection(sb, "Documents Created", writeOutcome.Created);
        AppendListSection(sb, "Documents Updated", writeOutcome.Updated);
        AppendListSection(sb, "Documents Unchanged", writeOutcome.Unchanged);
        AppendListSection(sb, "Open Questions", result.OpenQuestions);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendListSection(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();

        if (items.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var item in items)
            {
                sb.AppendLine($"- {item}");
            }
        }

        sb.AppendLine();
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
