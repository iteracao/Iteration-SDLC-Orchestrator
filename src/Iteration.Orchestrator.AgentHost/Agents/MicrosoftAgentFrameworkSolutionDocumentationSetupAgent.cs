using System.Text;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionDocumentationSetupAgent : ISolutionDocumentationSetupAgent
{
    private const string BoundariesArtifactFileName = "01-boundaries.md";
    private const string RepositoryStateArtifactFileName = "repository-state.md";
    private const string FinalDecisionArtifactFileName = "03-decision.md";
    private const string AgentPromptPath = "AI/framework/agents/solution-documenter/prompt.md";
    private const string AgentRulesPath = "AI/framework/agents/solution-documenter/rules.md";
    private const string AgentWorkflowPath = "AI/framework/agents/solution-documenter/workflows/setup-documentation.md";
    private const string WorkflowDoctrinePath = "AI/framework/workflows/setup-documentation/workflow.md";
    private const string SdlcRulePath = "AI/framework/rules/sdlc/setup-documentation.md";
    private const string DotNetWebEnterpriseDocumentationRulePath = "AI/framework/profiles/dotnet-web-enterprise/rules/documentation.md";

    private readonly IAgentConversationFactory _conversationFactory;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly IArtifactStore _artifacts;
    private readonly IConfigCatalog _config;
    private readonly int _maxModelResponseSeconds;

    public MicrosoftAgentFrameworkSolutionDocumentationSetupAgent(
        IAgentConversationFactory conversationFactory,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore artifacts,
        IConfigCatalog config,
        int maxModelResponseSeconds)
    {
        _conversationFactory = conversationFactory;
        _logs = logs;
        _payloadStore = payloadStore;
        _artifacts = artifacts;
        _config = config;
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
            var frameworkContextDocuments = await LoadRequiredFrameworkContextAsync(
                target.ProfileCode,
                agentDefinition,
                request.ProfileSummary,
                request.WorkflowRunId,
                ct);

            var knowledgeRoot = StableDocumentationCatalog.BuildKnowledgeRoot(request.RepositoryPath, request.SolutionCode);
            var managedDocumentMap = request.StableDocumentTargets.ToDictionary(
                logicalPath => logicalPath,
                logicalPath => Path.Combine(knowledgeRoot, logicalPath.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

            var writableFiles = managedDocumentMap.Values.ToDictionary(
                physicalPath => physicalPath,
                physicalPath => physicalPath,
                StringComparer.OrdinalIgnoreCase);

            var writeTargets = request.StableDocumentTargets
                .Select(logicalPath => (LogicalPath: logicalPath, PhysicalPath: managedDocumentMap[logicalPath]))
                .ToList();

            var phases = new List<FileAwareAgentRunner.AgentPhaseDefinition>
            {
                new(
                    Name: "Prompt 1",
                    Prompt: BuildBootstrapPrompt(request, writeTargets),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Establish documentation setup contract, authority order, and approved physical write targets.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    AllowToolCalls: false,
                    SavedMarkdownArtifactFileName: BoundariesArtifactFileName),
                new(
                    Name: "Prompt 2A",
                    Prompt: BuildRepositoryAcquisitionPrompt(),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Load the full allowed repository evidence set by consuming every batch from get_next_file_batch.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    ResponseMode: FileAwareAgentRunner.AgentPhaseResponseMode.ToolCallsOnly,
                    RequireCompletionValidation: false,
                    AllowedToolActions: ["get_next_file_batch"],
                    RequireAllAvailableFilesRead: true,
                    AutoCompleteWhenAllAvailableFilesRead: true),
                new(
                    Name: "Prompt 2B",
                    Prompt: BuildDocumentationContextPrompt(),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Build the repository-state Markdown using the full repository evidence already loaded.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    AllowToolCalls: false,
                    SavedMarkdownArtifactFileName: RepositoryStateArtifactFileName,
                    InjectSavedMarkdownIntoNextPhase: true,
                    RequireAllAvailableFilesRead: true),
                new(
                    Name: "Prompt 3",
                    Prompt: BuildDecisionPrompt(writeTargets),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Decide the documentation mode and per-document actions only.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    AllowToolCalls: false,
                    SavedMarkdownArtifactFileName: FinalDecisionArtifactFileName,
                    InjectSavedMarkdownIntoNextPhase: true)
            };

            phases.AddRange(writeTargets.Select((targetInfo, index) =>
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: $"Prompt {index + 4}",
                    Prompt: BuildSingleWritePrompt(index + 1, targetInfo.LogicalPath, targetInfo.PhysicalPath),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: $"Write only {targetInfo.LogicalPath} when required by the decision artifact.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: false,
                    AllowedToolActions: ["write_file"])));

            phases.Add(
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: $"Prompt {writeTargets.Count + 4}",
                    Prompt: BuildFinalSummaryPrompt(),
                    RequiresSavedOutput: true,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Return the final setup-documentation result after all required writes are complete.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    AllowToolCalls: false));

            var rawMarkdown = await FileAwareAgentRunner.RunMultiStepAsync(
                _conversationFactory,
                agentDefinition.Name,
                BuildInstructions(frameworkContextDocuments, request.ProfileSummary),
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

    private async Task<IReadOnlyList<TextDocumentInput>> LoadRequiredFrameworkContextAsync(
        string profileCode,
        AgentDefinition agentDefinition,
        string profileSummary,
        Guid workflowRunId,
        CancellationToken ct)
    {
        var documents = new List<TextDocumentInput>();

        await _logs.AppendSectionAsync(workflowRunId, "Framework Context", ct);
        await _logs.AppendLineAsync(workflowRunId, "Loading setup-documentation framework context for Prompt 1.", ct);

        if (string.IsNullOrWhiteSpace(agentDefinition.PromptText))
        {
            await _logs.AppendLineAsync(workflowRunId, $"Framework context: NOT FOUND {AgentPromptPath} | chars=0", CancellationToken.None);
            throw new InvalidOperationException($"Required setup-documentation framework context is empty: {AgentPromptPath}.");
        }

        documents.Add(new TextDocumentInput(AgentPromptPath, agentDefinition.PromptText));
        await _logs.AppendLineAsync(workflowRunId, $"Framework context: loaded {AgentPromptPath} | chars={agentDefinition.PromptText.Length}", ct);

        foreach (var path in GetRequiredFrameworkContextPaths(profileCode))
        {
            var relativePath = ToFrameworkRelativePath(path);
            try
            {
                var content = await _config.ReadFrameworkTextAsync(relativePath, ct);
                if (string.IsNullOrWhiteSpace(content))
                {
                    await _logs.AppendLineAsync(workflowRunId, $"Framework context: NOT FOUND {path} | chars=0", CancellationToken.None);
                    throw new InvalidOperationException($"Required setup-documentation framework context is empty: {path}.");
                }

                documents.Add(new TextDocumentInput(path, content));
                await _logs.AppendLineAsync(workflowRunId, $"Framework context: loaded {path} | chars={content.Length}", ct);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                await _logs.AppendLineAsync(workflowRunId, $"Framework context: NOT FOUND {path} | error={ex.GetType().Name}", CancellationToken.None);
                throw new InvalidOperationException($"Required setup-documentation framework context could not be loaded: {path}.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(profileSummary))
        {
            await _logs.AppendLineAsync(workflowRunId, "Framework context: active profile summary registered for setup-documentation.", ct);
        }

        return documents;
    }

    private static IReadOnlyList<string> GetRequiredFrameworkContextPaths(string profileCode)
        => string.Equals(profileCode, "dotnet-web-enterprise", StringComparison.OrdinalIgnoreCase)
            ? [AgentRulesPath, AgentWorkflowPath, WorkflowDoctrinePath, SdlcRulePath, DotNetWebEnterpriseDocumentationRulePath]
            : throw new InvalidOperationException($"Setup-documentation framework context does not yet support profile '{profileCode}'.");

    private static string ToFrameworkRelativePath(string repositoryRelativePath)
    {
        const string frameworkPrefix = "AI/framework/";
        return repositoryRelativePath.StartsWith(frameworkPrefix, StringComparison.OrdinalIgnoreCase)
            ? repositoryRelativePath[frameworkPrefix.Length..]
            : repositoryRelativePath;
    }

    private static string BuildInstructions(IReadOnlyList<TextDocumentInput> frameworkContextDocuments, string profileSummary)
    {
        var sb = new StringBuilder();
        for (var index = 0; index < frameworkContextDocuments.Count; index++)
        {
            var document = frameworkContextDocuments[index];
            sb.AppendLine($"FRAMEWORK CONTEXT FILE: {document.Path}");
            sb.AppendLine(document.Content.Trim());
            if (index < frameworkContextDocuments.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(profileSummary))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("ACTIVE PROFILE");
            sb.AppendLine(profileSummary.Trim());
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("EXECUTION REQUIREMENTS");
        sb.AppendLine("- Load the framework doctrine above before interpreting the phase prompt.");
        sb.AppendLine("- Follow the current phase prompt exactly.");
        sb.AppendLine("- Use only direct evidence. Do not invent drift, repository state, questions, or write actions.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildBootstrapPrompt(SolutionDocumentationSetupRequest request, IReadOnlyList<(string LogicalPath, string PhysicalPath)> writeTargets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Goal: define the documentation contract only.");
        sb.AppendLine("Return Markdown only.");
        sb.AppendLine("Output exactly:");
        sb.AppendLine("# Contract");
        sb.AppendLine("## Managed Documents");
        foreach (var path in request.StableDocumentTargets)
        {
            sb.AppendLine($"- {path}");
        }
        sb.AppendLine("## Managed Document Physical Paths");
        foreach (var target in writeTargets)
        {
            sb.AppendLine($"- {target.LogicalPath} -> {target.PhysicalPath}");
        }
        sb.AppendLine("## Authority Order");
        sb.AppendLine("1. Source code");
        sb.AppendLine("2. Valid stable docs");
        sb.AppendLine("3. Local repository docs");
        sb.AppendLine("## Path Rules");
        sb.AppendLine("- All write_file calls must use the full physical path exactly as listed above.");
        sb.AppendLine("- Logical paths are for decision and reporting only.");
        sb.AppendLine("- Never guess, shorten, or transform a path.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildRepositoryAcquisitionPrompt()
    {
        return """
Goal:
Load the full allowed repository evidence set using `get_next_file_batch` only.

Use native tool calling when available.
If native tools are unavailable, return exactly one JSON object and nothing else:
{"tool":"get_next_file_batch","args":{}}

After each tool result:
- if `HAS MORE: yes`, call `get_next_file_batch` exactly once again
- if `HAS MORE: no`, stop this phase

Rules:
- no prose
- no Markdown
- no multiple tool calls
- do not call again before receiving the previous tool result
- do not skip batches
""";
    }

    private static string BuildDocumentationContextPrompt()
    {
        return """
Goal:
Build the repository-state Markdown using ONLY the repository evidence loaded in Prompt 2A.

Rules:
- This is a synthesis phase.
- Tool calls are forbidden in this phase.
- Use only the evidence already loaded.
- Do not invent, assume, or infer missing information.
- If something is not present in the evidence, state it as unknown.
- Do not suggest new documents.
- Do not expand the managed document list.

Return Markdown only using exactly this structure:
# Repository State
## Solution Overview
## Business / Domain Summary
## Solution Stack
## Architecture / Structure
## Current Stable Documentation State
## Managed Document Assessment
## Relevant Files Reviewed
## Best Practice Gaps / Risks
## Open Questions
""";
    }

    private static string BuildDecisionPrompt(IReadOnlyList<(string LogicalPath, string PhysicalPath)> writeTargets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Goal: decide the documentation mode and per-document actions only. Do not write files in this step.");
        sb.AppendLine("Use the repository-state Markdown from Prompt 2B as the baseline understanding of the current solution.");
        sb.AppendLine();
        sb.AppendLine("Managed document mapping:");
        foreach (var target in writeTargets)
        {
            sb.AppendLine($"- {target.LogicalPath} -> {target.PhysicalPath}");
        }
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Determine one mode only: ALIGNED, UPDATE, or BOOTSTRAP.");
        sb.AppendLine("- This is a decision step only. Tool calls are forbidden.");
        sb.AppendLine("- Base the decision only on repository-state evidence and the current stable document state.");
        sb.AppendLine("- `## Actions` must include exactly one entry for each managed document.");
        sb.AppendLine("- Every action must use one of: CREATE <logical path>, UPDATE <logical path>, KEEP <logical path>, NO WRITE.");
        sb.AppendLine("- Do not suggest new documents or extra targets.");
        sb.AppendLine();
        sb.AppendLine("Return Markdown only using exactly this structure:");
        sb.AppendLine("# Decision");
        sb.AppendLine("Mode: ALIGNED | UPDATE | BOOTSTRAP");
        sb.AppendLine("## Actions");
        sb.AppendLine("## Reasoning");
        return sb.ToString().TrimEnd();
    }

    private static string BuildSingleWritePrompt(int stepNumber, string logicalPath, string physicalPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal: handle only `{logicalPath}` in this step.");
        sb.AppendLine();
        sb.AppendLine("Current write target:");
        sb.AppendLine($"- Logical path: {logicalPath}");
        sb.AppendLine($"- Physical path: {physicalPath}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- This step has a single objective: handle only the current target.");
        sb.AppendLine("- Read the decision artifact from Prompt 3 and act only for this logical path.");
        sb.AppendLine("- If the decision says KEEP or NO WRITE for this target, do not call any tool.");
        sb.AppendLine("- If the decision says CREATE or UPDATE for this target, call `write_file` exactly once.");
        sb.AppendLine("- Any `write_file` call must use the exact full physical path shown above.");
        sb.AppendLine("- Do not write any other file.");
        sb.AppendLine("- Never mix tool calls and Markdown in the same response.");
        sb.AppendLine();
        sb.AppendLine("If writing is required, use native tool calling when available.");
        sb.AppendLine("If native tools are unavailable, return exactly one JSON object in this shape:");
        sb.AppendLine($@"{{""tool"":""write_file"",""args"":{{""path"":""{physicalPath}"",""content"":""<markdown content>""}}}}");
        sb.AppendLine();
        sb.AppendLine("If no write is required, return Markdown only using exactly this structure:");
        sb.AppendLine("# Write Step Result");
        sb.AppendLine($"Action: NO WRITE {logicalPath}");
        sb.AppendLine("## Reasoning");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalSummaryPrompt()
    {
        return """
Goal:
Return the final workflow result only after all prior write steps are complete.

Rules:
- Tool calls are forbidden in this step.
- Summarize the final mode and outcomes based on the decision artifact and any completed write steps.
- Use logical managed-document paths in the final report.
- Do not claim a write happened unless it already succeeded in a prior step.

Return Markdown only using exactly this structure:
# Decision
Mode: ALIGNED | UPDATE | BOOTSTRAP
## Actions
## Reasoning
""";
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
