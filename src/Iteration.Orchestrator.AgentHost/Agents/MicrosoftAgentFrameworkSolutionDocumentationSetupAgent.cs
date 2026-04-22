using System.Text;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionDocumentationSetupAgent : ISolutionDocumentationSetupAgent
{
    private const string BoundariesArtifactFileName = "01-boundaries.md";
    private const string DocumentationContextArtifactFileName = "documentation-context.md";
    private const string FinalDecisionArtifactFileName = "03-decision.md";

    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly IArtifactStore _artifacts;
    private readonly int _maxModelResponseSeconds;

    public MicrosoftAgentFrameworkSolutionDocumentationSetupAgent(
        string endpoint,
        string model,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore artifacts,
        int maxModelResponseSeconds)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
        _payloadStore = payloadStore;
        _artifacts = artifacts;
        _maxModelResponseSeconds = maxModelResponseSeconds;
    }

    public async Task<SolutionDocumentationSetupResult> RunAsync(
        SolutionDocumentationSetupRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agentDefinition);
        ArgumentNullException.ThrowIfNull(target);

        if (request.AllowedContextFiles.Count == 0)
        {
            throw new InvalidOperationException("Documentation setup requires at least one allowed context file.");
        }

        await _logs.AppendLineAsync(request.WorkflowRunId, "Documentation setup workflow prepared for prompt-driven execution.", ct);

        try
        {
            var writableFiles = request.StableDocumentTargets.ToDictionary(
                path => path,
                path => Path.Combine(StableDocumentationCatalog.BuildKnowledgeRoot(request.RepositoryPath, request.SolutionCode), path.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

            var phases = new[]
            {
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 1",
                    Prompt: BuildBootstrapPrompt(request),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Establish documentation setup boundaries and authority order.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    AllowToolCalls: false,
                    SavedMarkdownArtifactFileName: BoundariesArtifactFileName),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 2",
                    Prompt: BuildDocumentationContextPrompt(request),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Review the full allowed stable-doc, repo-doc, and source context.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    SavedMarkdownArtifactFileName: DocumentationContextArtifactFileName,
                    InjectSavedMarkdownIntoNextPhase: true,
                    RequireAllAvailableFilesRead: true),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 3",
                    Prompt: BuildFinalOutputPrompt(request),
                    RequiresSavedOutput: true,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Decide the final documentation state, write approved stable docs, and return the final Markdown report.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireWorkflowInput: false,
                    RequireCompletionValidation: true,
                    SavedMarkdownArtifactFileName: FinalDecisionArtifactFileName)
            };

            var rawMarkdown = await FileAwareAgentRunner.RunMultiStepAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                BuildInstructions(agentDefinition, request),
                phases,
                request.RepositoryPath,
                request.AllowedContextFiles,
                request.WorkflowRunId,
                _logs,
                _payloadStore,
                _artifacts,
                ct,
                requireRepositoryEvidence: false,
                requireRepositoryDiscovery: false,
                discoveryTools: null,
                maxModelResponseSeconds: _maxModelResponseSeconds,
                writableFiles: writableFiles);

            var payload = ParseAndNormalize(rawMarkdown, request);
            await _logs.AppendLineAsync(request.WorkflowRunId, "Documentation setup agent final report parsed successfully.", ct);

            return new SolutionDocumentationSetupResult(
                payload.Mode,
                payload.Summary,
                payload.StableDocsFound,
                payload.RepoDocsReviewed,
                payload.SourceAreasReviewed,
                payload.DriftFindings,
                payload.DocumentsCreated,
                payload.DocumentsUpdated,
                payload.DocumentsUnchanged,
                payload.OpenQuestions,
                rawMarkdown);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, "Documentation setup agent execution failed.", CancellationToken.None);
            await _logs.AppendBlockAsync(request.WorkflowRunId, "Exception", ex.ToString(), CancellationToken.None);
            throw;
        }
    }

    private static string BuildInstructions(AgentDefinition agentDefinition, SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("Execution mode:");
        sb.AppendLine("- This workflow runs as a three-prompt sequence.");
        sb.AppendLine("- Prompt 1 establishes rules and boundaries.");
        sb.AppendLine($"- Prompt 2 must review the full allowed context and produce Markdown saved as {DocumentationContextArtifactFileName}.");
        sb.AppendLine("- Prompt 3 must decide the final documentation state, use write_file for approved doc changes, and return the final Markdown report only.");
        sb.AppendLine("- Do not call get_workflow_input or save_workflow_output.");
        sb.AppendLine("- When using a tool, return exactly one JSON object with an 'action' property.");
        sb.AppendLine("- Allowed read tool actions are find_available_files, get_next_file_batch, and get_file.");
        sb.AppendLine("- Allowed write tool action is write_file.");
        sb.AppendLine("- Always call find_available_files first before targeted get_file calls.");
        sb.AppendLine("- In Prompt 2, repeatedly call get_next_file_batch until all allowed context has been reviewed.");
        sb.AppendLine("- Use get_file only with exact full physical paths returned by find_available_files.");
        sb.AppendLine("- Use write_file only for approved stable documentation files listed below.");
        sb.AppendLine("Approved stable documentation targets:");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"- {path}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildBootstrapPrompt(SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 1 of 3 for setup-documentation.");
        sb.AppendLine();
        sb.AppendLine("Purpose:");
        sb.AppendLine("Establish the documentation setup workflow boundaries before loading repository context.");
        sb.AppendLine();
        sb.AppendLine("Rules to preserve:");
        sb.AppendLine("- Manage only stable solution documentation.");
        sb.AppendLine("- Stable document targets are:");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"  - {path}");
        }
        sb.AppendLine("- Exclude analysis, history, AI framework content, artifacts, and runs from drift detection.");
        sb.AppendLine("- Authority order is source code, then still-valid stable docs, then local repository docs.");
        sb.AppendLine("- Do not rewrite documentation when mode should be aligned.");
        sb.AppendLine();
        sb.AppendLine("Return a very short Markdown note with:");
        sb.AppendLine("# Boundaries");
        sb.AppendLine("## Workflow Intent");
        sb.AppendLine("## Boundaries To Preserve");
        return sb.ToString().TrimEnd();
    }

    private static string BuildDocumentationContextPrompt(SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 2 of 3 for setup-documentation.");
        sb.AppendLine("Purpose: build the full documentation context before deciding whether to bootstrap, update, or stay aligned.");
        sb.AppendLine();
        sb.AppendLine("ALLOWED CONTEXT");
        sb.AppendLine("- Existing stable documentation files for this solution target.");
        sb.AppendLine("- Local repository documentation files outside AI/history/analysis/artifacts/runs.");
        sb.AppendLine("- Source context files from the already visible repository set.");
        sb.AppendLine();
        sb.AppendLine("REQUIRED TOOL FLOW");
        sb.AppendLine("- First call `find_available_files`.");
        sb.AppendLine("- Then repeatedly call `get_next_file_batch` until the full allowed context has been reviewed.");
        sb.AppendLine("- Use `get_file` only for targeted follow-up reads when a specific file needs closer confirmation.");
        sb.AppendLine();
        sb.AppendLine("Return Markdown using exactly this structure:");
        sb.AppendLine("# Documentation Context");
        sb.AppendLine("## Stable Docs Found");
        sb.AppendLine("## Local Repo Docs Reviewed");
        sb.AppendLine("## Source Areas Reviewed");
        sb.AppendLine("## Drift Signals");
        sb.AppendLine("## Excluded Areas");
        sb.AppendLine("## Open Questions");
        sb.AppendLine();
        sb.AppendLine("Keep the context concise but grounded in the files you actually reviewed.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalOutputPrompt(SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 3 of 3 for setup-documentation.");
        sb.AppendLine("Use the saved documentation context from Prompt 2 as your baseline.");
        sb.AppendLine();
        sb.AppendLine("GOAL");
        sb.AppendLine("- Decide whether the mode is bootstrap, update, or aligned.");
        sb.AppendLine("- Use write_file to create or update only approved stable documentation files.");
        sb.AppendLine("- If the mode is aligned, do not call write_file.");
        sb.AppendLine();
        sb.AppendLine("WRITE RULES");
        sb.AppendLine("- You may write ONLY these approved stable document paths:");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"  - {path}");
        }
        sb.AppendLine("- Never write any other path.");
        sb.AppendLine("- Stable docs must stay concise, structured, long-lived, and grounded in source code.");
        sb.AppendLine("- Do not include workflow logs, analysis narratives, or transient run details in document content.");
        sb.AppendLine();
        sb.AppendLine("FINAL REPORT FORMAT");
        sb.AppendLine("Return Markdown only using exactly these sections:");
        sb.AppendLine("# Setup Documentation Result");
        sb.AppendLine("## Mode");
        sb.AppendLine("## Summary");
        sb.AppendLine("## Stable Docs Found");
        sb.AppendLine("## Repo Docs Reviewed");
        sb.AppendLine("## Source Areas Reviewed");
        sb.AppendLine("## Drift Findings");
        sb.AppendLine("## Documents Created");
        sb.AppendLine("## Documents Updated");
        sb.AppendLine("## Documents Unchanged");
        sb.AppendLine("## Open Questions");
        sb.AppendLine();
        sb.AppendLine("Use bullet lists for list sections. Put exactly one of: bootstrap, update, aligned under ## Mode.");
        return sb.ToString().TrimEnd();
    }

    private static WorkflowReportPayload ParseAndNormalize(string raw, SolutionDocumentationSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Agent returned an empty final report.");
        }

        var sections = ParseSections(raw);
        var mode = NormalizeMode(GetSingleValue(sections, "Mode"), request.ExistingStableDocumentPaths.Count);
        var summary = GetSingleValue(sections, "Summary");

        var payload = new WorkflowReportPayload
        {
            Mode = mode,
            Summary = string.IsNullOrWhiteSpace(summary) ? $"Documentation setup completed for '{request.SolutionName}'." : summary.Trim(),
            StableDocsFound = NormalizeStringList(GetList(sections, "Stable Docs Found")),
            RepoDocsReviewed = NormalizeStringList(GetList(sections, "Repo Docs Reviewed")),
            SourceAreasReviewed = NormalizeStringList(GetList(sections, "Source Areas Reviewed")),
            DriftFindings = NormalizeStringList(GetList(sections, "Drift Findings")),
            DocumentsCreated = NormalizeDocumentPaths(GetList(sections, "Documents Created"), request.StableDocumentTargets),
            DocumentsUpdated = NormalizeDocumentPaths(GetList(sections, "Documents Updated"), request.StableDocumentTargets),
            DocumentsUnchanged = NormalizeDocumentPaths(GetList(sections, "Documents Unchanged"), request.StableDocumentTargets),
            OpenQuestions = NormalizeStringList(GetList(sections, "Open Questions"))
        };

        if (string.Equals(payload.Mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && (payload.DocumentsCreated.Count > 0 || payload.DocumentsUpdated.Count > 0))
        {
            throw new InvalidOperationException("Aligned mode must not report created or updated documents.");
        }

        return payload;
    }

    private static Dictionary<string, List<string>> ParseSections(string markdown)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = line[3..].Trim();
                sections[current] = new List<string>();
                continue;
            }

            if (current is null || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            sections[current].Add(line.Trim());
        }

        return sections;
    }

    private static string GetSingleValue(IReadOnlyDictionary<string, List<string>> sections, string name)
        => sections.TryGetValue(name, out var lines)
            ? string.Join(" ", lines.Select(x => x.TrimStart('-', ' ').Trim()).Where(x => !string.IsNullOrWhiteSpace(x))).Trim()
            : string.Empty;

    private static IReadOnlyList<string> GetList(IReadOnlyDictionary<string, List<string>> sections, string name)
        => sections.TryGetValue(name, out var lines)
            ? lines.Select(x => x.TrimStart('-', ' ').Trim()).Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "none", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private static string NormalizeMode(string? mode, int existingStableDocCount)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "bootstrap" => "bootstrap",
            "update" => "update",
            "aligned" => "aligned",
            _ => existingStableDocCount == 0 ? "bootstrap" : "update"
        };

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
        => values?
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static List<string> NormalizeDocumentPaths(IEnumerable<string>? values, IReadOnlyList<string> stableTargets)
    {
        var stableSet = stableTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NormalizeStringList(values).Where(stableSet.Contains).ToList();
    }

    private sealed class WorkflowReportPayload
    {
        public string Mode { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> StableDocsFound { get; set; } = [];
        public List<string> RepoDocsReviewed { get; set; } = [];
        public List<string> SourceAreasReviewed { get; set; } = [];
        public List<string> DriftFindings { get; set; } = [];
        public List<string> DocumentsCreated { get; set; } = [];
        public List<string> DocumentsUpdated { get; set; } = [];
        public List<string> DocumentsUnchanged { get; set; } = [];
        public List<string> OpenQuestions { get; set; } = [];
    }
}
