using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionDocumentationSetupAgent : ISolutionDocumentationSetupAgent
{
    private const string DocumentationContextArtifactFileName = "documentation-context.md";

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
            var phases = new[]
            {
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 1",
                    Prompt: BuildBootstrapPrompt(request),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Establish documentation setup boundaries and authority order.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive),
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
                    PurposeSummary: "Produce the final documentation setup decision and document drafts as JSON.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireWorkflowInput: false,
                    RequireCompletionValidation: true)
            };

            var rawText = await FileAwareAgentRunner.RunMultiStepAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                BuildInstructions(agentDefinition),
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
                maxModelResponseSeconds: _maxModelResponseSeconds);

            var payload = ParseAndNormalize(rawText, request);
            var normalizedJson = JsonSerializer.Serialize(payload, JsonOptions);

            await _logs.AppendLineAsync(request.WorkflowRunId, "Documentation setup agent response parsed successfully.", ct);

            return new SolutionDocumentationSetupResult(
                payload.Mode,
                payload.Summary,
                payload.StableDocsFound,
                payload.RepoDocsReviewed,
                payload.SourceAreasReviewed,
                payload.DriftFindings,
                payload.Documents,
                payload.OpenQuestions,
                normalizedJson);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, "Documentation setup agent execution failed.", CancellationToken.None);
            await _logs.AppendBlockAsync(request.WorkflowRunId, "Exception", ex.ToString(), CancellationToken.None);
            throw;
        }
    }

    private static string BuildInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("Execution mode:");
        sb.AppendLine("- This workflow runs as a three-prompt sequence.");
        sb.AppendLine("- Prompt 1 establishes rules and boundaries.");
        sb.AppendLine($"- Prompt 2 must review the full allowed context and produce Markdown saved as {DocumentationContextArtifactFileName}.");
        sb.AppendLine("- Prompt 3 must return the final JSON result only.");
        sb.AppendLine("- Do not call get_workflow_input or save_workflow_output.");
        sb.AppendLine("- When using a tool, return exactly one JSON object with an 'action' property.");
        sb.AppendLine("- Allowed tool actions are find_available_files, get_next_file_batch, and get_file.");
        sb.AppendLine("- Always call find_available_files first.");
        sb.AppendLine("- In Prompt 2, repeatedly call get_next_file_batch until all allowed context has been reviewed.");
        sb.AppendLine("- Use get_file only with exact full physical paths returned by find_available_files.");
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
        sb.AppendLine("Use the saved documentation context from Prompt 2 as your baseline, then produce the final setup-documentation decision.");
        sb.AppendLine();
        sb.AppendLine("GOAL");
        sb.AppendLine("- Decide whether the mode is bootstrap, update, or aligned.");
        sb.AppendLine("- Create document drafts only for files that must be created or updated.");
        sb.AppendLine("- If the mode is aligned, do not propose rewrites.");
        sb.AppendLine();
        sb.AppendLine("DRIFT RULES");
        sb.AppendLine("- Compare stable docs against local repository docs and source code.");
        sb.AppendLine("- Detect missing documents, outdated sections, workflow mismatch, architecture/module mismatch, and new modules not documented.");
        sb.AppendLine("- Do not use excluded areas as evidence.");
        sb.AppendLine();
        sb.AppendLine("DOCUMENT TARGETS");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        sb.AppendLine("OUTPUT CONTRACT");
        sb.AppendLine("- Return JSON only.");
        sb.AppendLine("- The top-level object must contain:");
        sb.AppendLine("  - mode");
        sb.AppendLine("  - summary");
        sb.AppendLine("  - stableDocsFound");
        sb.AppendLine("  - repoDocsReviewed");
        sb.AppendLine("  - sourceAreasReviewed");
        sb.AppendLine("  - driftFindings");
        sb.AppendLine("  - documents");
        sb.AppendLine("  - openQuestions");
        sb.AppendLine("- `mode` must be one of: bootstrap, update, aligned.");
        sb.AppendLine("- `documents` must be an array of objects with: path, action, content.");
        sb.AppendLine("- `path` must be one of the stable document targets listed above.");
        sb.AppendLine("- `action` must be create, update, or unchanged.");
        sb.AppendLine("- `content` is required for create or update and must be omitted or empty for unchanged.");
        sb.AppendLine("- Stable docs must stay concise, structured, long-lived, and grounded in source code.");
        sb.AppendLine("- Do not include workflow logs, analysis narratives, or transient run details in document content.");
        return sb.ToString().TrimEnd();
    }

    private static WorkflowOutputPayload ParseAndNormalize(string raw, SolutionDocumentationSetupRequest request)
    {
        var cleaned = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<WorkflowOutputPayload>(cleaned, JsonOptions)
            ?? throw new InvalidOperationException($"Agent returned an empty or invalid JSON payload. Raw response: {raw}");

        var stableTargets = request.StableDocumentTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        parsed.Mode = NormalizeMode(parsed.Mode, request.ExistingStableDocumentPaths.Count);
        parsed.Summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? $"Documentation setup completed for '{request.SolutionName}'."
            : parsed.Summary.Trim();
        parsed.StableDocsFound = NormalizeStringList(parsed.StableDocsFound);
        parsed.RepoDocsReviewed = NormalizeStringList(parsed.RepoDocsReviewed);
        parsed.SourceAreasReviewed = NormalizeStringList(parsed.SourceAreasReviewed);
        parsed.DriftFindings = NormalizeStringList(parsed.DriftFindings);
        parsed.OpenQuestions = NormalizeStringList(parsed.OpenQuestions);
        parsed.Documents = NormalizeDocuments(parsed.Documents, stableTargets, parsed.Mode);
        return parsed;
    }

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

    private static List<DocumentationFileDraft> NormalizeDocuments(
        IEnumerable<DocumentationFileDraft>? documents,
        IReadOnlySet<string> stableTargets,
        string mode)
    {
        var normalized = documents?
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new DocumentationFileDraft(
                x.Path.Trim(),
                NormalizeDocumentAction(x.Action),
                x.Content.Trim()))
            .Where(x => stableTargets.Contains(x.Path))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList() ?? [];

        foreach (var document in normalized)
        {
            if ((string.Equals(document.Action, "create", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(document.Action, "update", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(document.Content))
            {
                throw new InvalidOperationException($"Documentation draft '{document.Path}' is missing content for action '{document.Action}'.");
            }
        }

        if (string.Equals(mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && normalized.Any(x => !string.Equals(x.Action, "unchanged", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Aligned mode must not contain create or update document drafts.");
        }

        return normalized;
    }

    private static string NormalizeDocumentAction(string? action)
        => action?.Trim().ToLowerInvariant() switch
        {
            "create" => "create",
            "update" => "update",
            "unchanged" => "unchanged",
            _ => "update"
        };

    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Agent returned an empty response.");
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Agent response did not contain a valid JSON object. Raw response: {raw}");
        }

        return raw[start..(end + 1)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class WorkflowOutputPayload
    {
        public string Mode { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> StableDocsFound { get; set; } = [];
        public List<string> RepoDocsReviewed { get; set; } = [];
        public List<string> SourceAreasReviewed { get; set; } = [];
        public List<string> DriftFindings { get; set; } = [];
        public List<DocumentationFileDraft> Documents { get; set; } = [];
        public List<string> OpenQuestions { get; set; } = [];
    }

}
