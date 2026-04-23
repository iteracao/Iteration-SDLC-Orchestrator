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
                    PurposeSummary: "Build the read-only documentation context from evidence.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    AllowedToolActions: ["find_available_files", "get_file"],
                    SavedMarkdownArtifactFileName: DocumentationContextArtifactFileName,
                    InjectSavedMarkdownIntoNextPhase: true),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 3",
                    Prompt: BuildFinalOutputPrompt(request),
                    RequiresSavedOutput: true,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Decide the final documentation state and apply only approved writes.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireWorkflowInput: false,
                    RequireCompletionValidation: true,
                    AllowedToolActions: ["write_file"],
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
        sb.AppendLine("Follow the current phase prompt exactly.");
        sb.AppendLine("Use only the tools allowed by the current phase.");
        sb.AppendLine("Do not invent drift, file evidence, or write actions.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildBootstrapPrompt(SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 1 of 3 for setup-documentation.");
        sb.AppendLine("Return Markdown only.");
        sb.AppendLine("Output exactly:");
        sb.AppendLine("# Contract");
        sb.AppendLine("## Managed Documents");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"- {path}");
        }
        sb.AppendLine("## Authority Order");
        sb.AppendLine("1. Source code");
        sb.AppendLine("2. Valid stable docs");
        sb.AppendLine("3. Local repository docs");
        return sb.ToString().TrimEnd();
    }

    private static string BuildDocumentationContextPrompt(SolutionDocumentationSetupRequest request)
    {
        return """
This is Prompt 2 of 3 for setup-documentation.

Rules:
- Read-only phase.
- Call `find_available_files` first.
- Use only file paths returned by `find_available_files`.
- Use `get_file` only to read evidence.
- Do not call `write_file`.
- Drift means one of:
  - contradiction with source
  - missing required content
  - outdated relative to current structure
- Do not classify unrelated files as drift.
- Do not invent exclusions.
- Do not add irrelevant questions.

Return Markdown only using exactly this structure:
# Documentation Context
## Stable Docs Found
## Repo Docs Reviewed
## Source Files Reviewed
## Drift Signals
## Open Questions
""";
    }

    private static string BuildFinalOutputPrompt(SolutionDocumentationSetupRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 3 of 3 for setup-documentation.");
        sb.AppendLine("Use only the evidence from Prompt 2.");
        sb.AppendLine("Allowed write targets:");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"- {path}");
        }
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Determine one mode before any write: ALIGNED, UPDATE, or BOOTSTRAP.");
        sb.AppendLine("- ALIGNED: do not call `write_file`.");
        sb.AppendLine("- UPDATE: write only necessary stable docs.");
        sb.AppendLine("- BOOTSTRAP: write only missing or invalid stable docs.");
        sb.AppendLine("- Never write placeholder content.");
        sb.AppendLine("- Never overwrite valid docs.");
        sb.AppendLine("- Every action in `## Actions` must use one of: `CREATE <path>`, `UPDATE <path>`, `KEEP <path>`, `NO WRITE`.");
        sb.AppendLine("- If mode is UPDATE or BOOTSTRAP, perform only the necessary `write_file` calls, then return the final Markdown.");
        sb.AppendLine();
        sb.AppendLine("Return Markdown only using exactly this structure:");
        sb.AppendLine("# Decision");
        sb.AppendLine("Mode: ALIGNED | UPDATE | BOOTSTRAP");
        sb.AppendLine("## Actions");
        sb.AppendLine("## Reasoning");
        return sb.ToString().TrimEnd();
    }

    private static WorkflowReportPayload ParseAndNormalize(string raw, SolutionDocumentationSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Agent returned an empty final report.");
        }

        var sections = ParseSections(raw);
        var mode = NormalizeMode(ExtractMode(raw), request.ExistingStableDocumentPaths.Count);
        var reasoning = GetSingleValue(sections, "Reasoning");
        var actions = NormalizeStringList(GetList(sections, "Actions"));
        var normalizedActions = ParseDocumentActions(actions, request.StableDocumentTargets);

        var payload = new WorkflowReportPayload
        {
            Mode = mode,
            Summary = string.IsNullOrWhiteSpace(reasoning) ? $"Documentation setup completed for '{request.SolutionName}'." : reasoning.Trim(),
            StableDocsFound = NormalizeStringList(request.ExistingStableDocumentPaths),
            RepoDocsReviewed = [],
            SourceAreasReviewed = [],
            DriftFindings = string.IsNullOrWhiteSpace(reasoning) ? [] : [reasoning.Trim()],
            DocumentsCreated = normalizedActions.Created,
            DocumentsUpdated = normalizedActions.Updated,
            DocumentsUnchanged = normalizedActions.Unchanged,
            OpenQuestions = []
        };

        if (string.Equals(payload.Mode, "aligned", StringComparison.OrdinalIgnoreCase)
            && (payload.DocumentsCreated.Count > 0 || payload.DocumentsUpdated.Count > 0))
        {
            throw new InvalidOperationException("Aligned mode must not report created or updated documents.");
        }

        return payload;
    }

    private static string ExtractMode(string markdown)
    {
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
            {
                return line[5..].Trim();
            }
        }

        return string.Empty;
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

    private static DocumentActionParseResult ParseDocumentActions(IEnumerable<string> values, IReadOnlyList<string> stableTargets)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var unchanged = new List<string>();

        foreach (var value in NormalizeStringList(values))
        {
            if (TryParseDocumentAction(value, "CREATE", out var createdPath) && stableTargets.Contains(createdPath, StringComparer.OrdinalIgnoreCase))
            {
                created.Add(createdPath);
                continue;
            }

            if (TryParseDocumentAction(value, "UPDATE", out var updatedPath) && stableTargets.Contains(updatedPath, StringComparer.OrdinalIgnoreCase))
            {
                updated.Add(updatedPath);
                continue;
            }

            if (TryParseDocumentAction(value, "KEEP", out var unchangedPath) && stableTargets.Contains(unchangedPath, StringComparer.OrdinalIgnoreCase))
            {
                unchanged.Add(unchangedPath);
            }
        }

        return new DocumentActionParseResult(
            created.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            updated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            unchanged.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool TryParseDocumentAction(string value, string action, out string path)
    {
        path = string.Empty;
        if (!value.StartsWith(action + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = value[(action.Length + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(path);
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

    private sealed record DocumentActionParseResult(
        List<string> Created,
        List<string> Updated,
        List<string> Unchanged);
}
