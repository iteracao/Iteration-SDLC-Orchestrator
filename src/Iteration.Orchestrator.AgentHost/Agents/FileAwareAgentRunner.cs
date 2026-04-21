using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    private const int MaxToolCallsPerPhase = 16;
    private const int MaxTreeEntries = 120;
    private const int MaxSearchHits = 12;

    public static Task<string> RunAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        string initialPrompt,
        string repositoryRoot,
        IReadOnlyCollection<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        CancellationToken ct,
        IReadOnlyCollection<string>? requiredFrameworkPaths = null,
        IReadOnlyCollection<string>? requiredSolutionPaths = null,
        bool requireRepositoryEvidence = false,
        int maxModelResponseSeconds = 180)
    {
        var phase = new AgentPhaseDefinition(
            Name: "single-pass",
            Prompt: initialPrompt,
            RequiresSavedOutput: true,
            AllowRepositoryDiscovery: false,
            PurposeSummary: "Run the workflow in a single prompt/tool loop.",
            Mode: AgentPhaseMode.Interactive,
            RequireWorkflowInput: true,
            RequireCompletionValidation: true);

        return RunMultiStepAsync(
            endpoint,
            model,
            agentName,
            instructions,
            [phase],
            repositoryRoot,
            allowedPaths,
            workflowRunId,
            logs,
            payloadStore,
            ct,
            requiredFrameworkPaths,
            requiredSolutionPaths,
            requireRepositoryEvidence,
            requireRepositoryDiscovery: false,
            discoveryTools: null,
            maxModelResponseSeconds: maxModelResponseSeconds);
    }

    public static async Task<string> RunPromptAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        string prompt,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        string logTitle,
        CancellationToken ct,
        int maxModelResponseSeconds = 180)
    {
        var chatClient = new OllamaChatClient(new Uri(endpoint), modelId: model);
        AIAgent agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);
        var responseTimeoutSeconds = maxModelResponseSeconds;
        var session = await agent.CreateSessionAsync(ct);

        await logs.AppendSectionAsync(workflowRunId, logTitle, ct);
        await logs.AppendBlockAsync(workflowRunId, $"Prompt: {logTitle}", prompt, ct);
        await logs.AppendLineAsync(workflowRunId, $"Model '{model}' using per-response timeout of {responseTimeoutSeconds} seconds.", ct);

        var rawText = await RunModelWithTimeoutAsync(
            agent,
            [CreateUserMessage(prompt)],
            session,
            responseTimeoutSeconds,
            ct);

        await logs.AppendBlockAsync(workflowRunId, $"Response: {logTitle}", rawText, ct);
        return rawText;
    }

    public static async Task<string> RunMultiStepAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        IReadOnlyList<AgentPhaseDefinition> phases,
        string repositoryRoot,
        IReadOnlyCollection<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        CancellationToken ct,
        IReadOnlyCollection<string>? requiredFrameworkPaths = null,
        IReadOnlyCollection<string>? requiredSolutionPaths = null,
        bool requireRepositoryEvidence = false,
        bool requireRepositoryDiscovery = false,
        RepositoryDiscoveryTools? discoveryTools = null,
        int maxModelResponseSeconds = 180)
    {
        if (phases.Count == 0)
        {
            throw new InvalidOperationException("At least one agent phase is required.");
        }

        var chatClient = new OllamaChatClient(new Uri(endpoint), modelId: model);
        AIAgent agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);
        var session = await agent.CreateSessionAsync(ct);
        var responseTimeoutSeconds = maxModelResponseSeconds;
        var requiredFrameworkPathSet = (requiredFrameworkPaths ?? Array.Empty<string>())
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredSolutionPathSet = (requiredSolutionPaths ?? Array.Empty<string>())
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableFileIndex = BuildAvailableFileIndex(repositoryRoot, allowedPaths);

        var pendingMessages = new List<ChatMessage>();
        var state = new AgentExecutionState();

        await logs.AppendLineAsync(workflowRunId, $"Model '{model}' using per-response timeout of {responseTimeoutSeconds} seconds.", ct);

        for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            await logs.AppendSectionAsync(workflowRunId, $"Agent phase {phaseIndex + 1}: {phase.Name}", ct);
            await logs.AppendBlockAsync(workflowRunId, $"Phase prompt: {phase.Name}", phase.Prompt, ct);

            string phaseResult;
            if (phase.Mode == AgentPhaseMode.ContextOnly)
            {
                pendingMessages.Add(CreateUserMessage(phase.Prompt));
                await logs.AppendLineAsync(workflowRunId, $"Phase mode ({phase.Name}): ContextOnly. Prompt queued into agent conversation context with no model call.", ct);
                phaseResult = "Context loaded.";
            }
            else
            {
                phaseResult = await ExecutePhaseAsync(
                    agent,
                    session,
                    phase,
                    phaseIndex,
                    phases.Count,
                    repositoryRoot,
                    workflowRunId,
                    logs,
                    payloadStore,
                    discoveryTools,
                    availableFileIndex,
                    pendingMessages,
                    state,
                    ct,
                    requiredFrameworkPathSet,
                    requiredSolutionPathSet,
                    requireRepositoryEvidence,
                    requireRepositoryDiscovery,
                    responseTimeoutSeconds);
            }

            if (phase.RequiresSavedOutput)
            {
                return phaseResult;
            }
        }

        throw new InvalidOperationException("Multi-step agent execution finished without saving a final workflow output.");
    }

    private static async Task<string> ExecutePhaseAsync(
        AIAgent agent,
        AgentSession session,
        AgentPhaseDefinition phase,
        int phaseIndex,
        int totalPhases,
        string repositoryRoot,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        RepositoryDiscoveryTools? discoveryTools,
        AvailableFileIndex availableFileIndex,
        List<ChatMessage> pendingMessages,
        AgentExecutionState state,
        CancellationToken ct,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool requireRepositoryDiscovery,
        int maxModelResponseSeconds)
    {
        var currentMessages = BuildPhaseMessages(phase, phaseIndex, totalPhases, pendingMessages);
        pendingMessages.Clear();

        for (var i = 0; i < MaxToolCallsPerPhase; i++)
        {
            var rawText = await RunModelWithTimeoutAsync(agent, currentMessages, session, maxModelResponseSeconds, ct);
            await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} - agent response #{i + 1}", rawText, ct);

            if (!TryParseToolRequest(rawText, out var toolRequest))
            {
                if (phase.RequiresSavedOutput)
                {
                    throw new InvalidOperationException($"Final phase '{phase.Name}' ended without save_workflow_output.");
                }

                if (phase.RequireCompletionValidation)
                {
                    EnsureRequiredContextLoaded(
                        phase.RequireWorkflowInput,
                        state.WorkflowInputLoaded,
                        state.ReadPaths,
                        requiredFrameworkPathSet,
                        requiredSolutionPathSet,
                        requireRepositoryEvidence,
                        state.RepositoryDiscoveryUsed,
                        requireRepositoryDiscovery);
                }

                return rawText;
            }

            var normalizedAction = toolRequest!.ResolvedAction.Trim().ToLowerInvariant();
            if (phase.RequiresSavedOutput && normalizedAction != "save_workflow_output")
            {
                throw new InvalidOperationException($"Final phase '{phase.Name}' only allows save_workflow_output, but agent requested '{toolRequest.ResolvedAction}'.");
            }

            switch (normalizedAction)
            {
                case "get_workflow_input":
                {
                    await LogIgnoredWorkflowRunIdAsync(toolRequest.WorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
                    var inputPayload = await payloadStore.GetInputAsync(workflowRunId, ct);
                    state.WorkflowInputLoaded = true;
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): get_workflow_input('{workflowRunId}').", ct);
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): success. Characters read: {inputPayload.InputPayloadJson.Length}.", ct);

                    currentMessages =
                    [
                        CreateToolMessage($"WORKFLOW INPUT FOR {workflowRunId}\n{inputPayload.InputPayloadJson}")
                    ];
                    continue;
                }
                case "find_available_files":
                {
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): find_available_files().", ct);

                    var matchingPaths = FindAvailableFiles(availableFileIndex);
                    var fileListResult = FormatAvailableFiles(matchingPaths);
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): {matchingPaths.Count} file path(s) returned.", ct);
                    if (matchingPaths.Count > 0)
                    {
                        await logs.AppendBlockAsync(workflowRunId, $"Available files ({phase.Name})", fileListResult, ct);
                    }
                    currentMessages = [CreateToolMessage(fileListResult)];
                    continue;
                }
                case "read_file":
                case "get_file":
                {
                    if (string.IsNullOrWhiteSpace(toolRequest.Path))
                    {
                        throw new InvalidOperationException($"Agent requested {normalizedAction} without a valid 'path'.");
                    }

                    var requestedPath = toolRequest.Path.Trim();
                    var fileRead = TryReadFileByPhysicalPath(availableFileIndex, requestedPath);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): get_file('{requestedPath}').", ct);

                    if (fileRead is null)
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. File not available for this run.", ct);
                        currentMessages = [CreateToolMessage(string.Empty)];
                        continue;
                    }

                    state.ReadPaths.Add(fileRead.NormalizedPath);
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): success. Characters read: {fileRead.Content.Length}.", ct);

                    currentMessages =
                    [
                        CreateToolMessage(fileRead.Content)
                    ];
                    continue;
                }
                case "save_workflow_output":
                {
                    if (toolRequest.Output is not JsonElement output)
                    {
                        throw new InvalidOperationException("save_workflow_output requires an 'output' object.");
                    }

                    await LogIgnoredWorkflowRunIdAsync(toolRequest.WorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
                    ValidateWorkflowOutputPayload(output);
                    var outputJson = output.GetRawText();
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): save_workflow_output('{workflowRunId}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, $"Workflow output payload ({phase.Name})", outputJson, ct);
                    await payloadStore.SaveOutputAsync(workflowRunId, outputJson, ct);
                    return outputJson;
                }
                default:
                    throw new InvalidOperationException($"Unsupported tool action requested by agent: {toolRequest.ResolvedAction}");
            }
        }

        throw new InvalidOperationException($"Agent exceeded maximum of {MaxToolCallsPerPhase} tool interactions in phase '{phase.Name}'.");
    }

    private static IReadOnlyList<ChatMessage> BuildPhaseMessages(
        AgentPhaseDefinition phase,
        int phaseIndex,
        int totalPhases,
        IReadOnlyList<ChatMessage> pendingMessages)
    {
        var messages = new List<ChatMessage>(pendingMessages.Count + 1);
        messages.AddRange(pendingMessages);
        messages.Add(CreateUserMessage(BuildPhasePrompt(phase, phaseIndex, totalPhases)));
        return messages;
    }

    private static void EnsureRequiredContextLoaded(
        bool requireWorkflowInput,
        bool workflowInputLoaded,
        IReadOnlyCollection<string> readPaths,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool repositoryDiscoveryUsed,
        bool requireRepositoryDiscovery)
    {
        if (requireWorkflowInput && !workflowInputLoaded)
        {
            throw new InvalidOperationException("Agent attempted to complete the phase without loading workflow input.");
        }

        foreach (var requiredPath in requiredFrameworkPathSet)
        {
            if (!readPaths.Contains(requiredPath))
            {
                throw new InvalidOperationException($"Agent attempted to complete the phase before reading required framework path '{requiredPath}'.");
            }
        }

        foreach (var requiredPath in requiredSolutionPathSet)
        {
            if (!readPaths.Contains(requiredPath))
            {
                throw new InvalidOperationException($"Agent attempted to complete the phase before reading required solution path '{requiredPath}'.");
            }
        }

        if (requireRepositoryDiscovery && !repositoryDiscoveryUsed)
        {
            throw new InvalidOperationException("Agent attempted to complete the phase without using repository discovery.");
        }

        if (requireRepositoryEvidence)
        {
            var repositoryEvidenceRead = readPaths.Any(path =>
                !requiredFrameworkPathSet.Contains(path) &&
                !requiredSolutionPathSet.Contains(path));

            if (!repositoryEvidenceRead)
            {
                throw new InvalidOperationException("Agent attempted to complete the phase before reading repository evidence files.");
            }
        }
    }

    private static void ValidateWorkflowOutputPayload(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Workflow output must be a JSON object.");
        }

        if (!output.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(status.GetString()))
        {
            throw new InvalidOperationException("Workflow output is missing required field 'status'.");
        }

        if (!output.TryGetProperty("summary", out var summary) ||
            summary.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(summary.GetString()))
        {
            throw new InvalidOperationException("Workflow output is missing required field 'summary'.");
        }

        if (!output.TryGetProperty("artifacts", out var artifacts) ||
            artifacts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Workflow output is missing required field 'artifacts'.");
        }

        if (!output.TryGetProperty("recommendedNextWorkflowCodes", out var nextWorkflowCodes) ||
            nextWorkflowCodes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Workflow output is missing required field 'recommendedNextWorkflowCodes'.");
        }
    }

    private static bool TryParseToolRequest(string raw, out ToolRequest? request)
    {
        request = null;
        var json = ExtractJsonObjectOrNull(raw);
        if (json is null)
        {
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<ToolRequest>(json, JsonOptions);
            return request is not null && !string.IsNullOrWhiteSpace(request.ResolvedAction);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObjectOrNull(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string NormalizePath(string relativePath)
        => relativePath.Replace('\\', '/').Trim();

    private static async Task<string> RunModelWithTimeoutAsync(
        AIAgent agent,
        IReadOnlyList<ChatMessage> messages,
        AgentSession session,
        int maxModelResponseSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxModelResponseSeconds));

        try
        {
            var rawResponse = await agent.RunAsync(messages, session: session, cancellationToken: timeoutCts.Token);
            return rawResponse.Text ?? string.Empty;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Model response exceeded {maxModelResponseSeconds} seconds.", ex);
        }
    }

    private static string? NormalizeOptionalPath(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? null : NormalizePath(relativePath);

    private static AvailableFileIndex BuildAvailableFileIndex(string repositoryRoot, IReadOnlyCollection<string> allowedPaths)
    {
        var files = new List<string>();
        var normalizedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var allowedPath in allowedPaths)
        {
            if (string.IsNullOrWhiteSpace(allowedPath))
            {
                continue;
            }

            var fullPath = Path.IsPathRooted(allowedPath)
                ? Path.GetFullPath(allowedPath)
                : Path.GetFullPath(Path.Combine(repositoryRoot, allowedPath));

            var normalizedFullPath = NormalizePath(fullPath);
            if (normalizedMap.ContainsKey(normalizedFullPath))
            {
                continue;
            }

            files.Add(fullPath);
            normalizedMap[normalizedFullPath] = fullPath;
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new AvailableFileIndex(files, normalizedMap);
    }

    private static FileReadResult? TryReadFileByPhysicalPath(AvailableFileIndex availableFileIndex, string requestedPath)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        var normalizedFullPath = NormalizePath(fullPath);

        if (!availableFileIndex.NormalizedFullPaths.TryGetValue(normalizedFullPath, out var allowedFullPath))
        {
            return null;
        }

        if (!File.Exists(allowedFullPath))
        {
            return null;
        }

        return new FileReadResult(allowedFullPath, NormalizePath(allowedFullPath), File.ReadAllText(allowedFullPath));
    }

    private static List<string> FindAvailableFiles(AvailableFileIndex availableFileIndex)
    {
        return availableFileIndex.FullPaths
            .Take(MaxTreeEntries)
            .ToList();
    }

    private static string FormatAvailableFiles(IReadOnlyList<string> fullPaths)
    {
        if (fullPaths.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, fullPaths);
    }

    private static string BuildPhasePrompt(AgentPhaseDefinition phase, int phaseIndex, int totalPhases)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PHASE {phaseIndex + 1} OF {totalPhases}: {phase.Name}");
        sb.AppendLine();
        sb.AppendLine(phase.PurposeSummary.Trim());
        sb.AppendLine();
        sb.AppendLine(phase.Prompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("PHASE RULES:");
        sb.AppendLine("- Return either a single JSON tool call object or a concise plain-text phase summary.");
        sb.AppendLine("- Do not include markdown fences.");
        sb.AppendLine("- Keep phase summaries concrete and evidence-based.");
        sb.AppendLine("- Use find_available_files to obtain exact full physical paths for this run.");
        sb.AppendLine("- Use get_file only with an exact full physical path returned by find_available_files.");

        if (phase.RequiresSavedOutput)
        {
            sb.AppendLine("- This is the final phase: you MUST finish by calling save_workflow_output.");
            sb.AppendLine("- In the final phase, do not call get_workflow_input or get_file.");
            sb.AppendLine("- Return exactly one JSON object with properties action, workflowRunId, and output.");
            sb.AppendLine("- Use property name 'action', not 'tool'.");
        }
        else
        {
            sb.AppendLine("- Do not call save_workflow_output in this phase.");

            if (phase.RequireCompletionValidation)
            {
                sb.AppendLine("- Before you finish this phase, gather enough direct evidence to support your final plain-text response.");
            }
        }

        return sb.ToString();
    }

    private static ChatMessage CreateUserMessage(string content) => new(ChatRole.User, content);

    private static ChatMessage CreateToolMessage(string content) => new(ChatRole.Tool, content);

    private static string FormatRepositoryTree(IReadOnlyList<RepositoryEntry> entries, string? scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SCOPE: {scope ?? "."}");
        sb.AppendLine($"ENTRY COUNT: {entries.Count}");

        foreach (var entry in entries.Take(MaxTreeEntries))
        {
            sb.AppendLine($"- {(entry.IsDirectory ? "[dir]" : "[file]")} {entry.RelativePath}");
        }

        if (entries.Count > MaxTreeEntries)
        {
            sb.AppendLine($"... truncated to first {MaxTreeEntries} entries ...");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSearchHits(string query, IReadOnlyList<FileSearchHit> hits, string? scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"QUERY: {query}");
        sb.AppendLine($"SCOPE: {scope ?? "."}");
        sb.AppendLine($"HIT COUNT: {hits.Count}");

        foreach (var hit in hits.Take(MaxSearchHits))
        {
            sb.AppendLine($"- {hit.RelativePath}");
            sb.AppendLine($"  {hit.Snippet}");
        }

        if (hits.Count > MaxSearchHits)
        {
            sb.AppendLine($"... truncated to first {MaxSearchHits} hits ...");
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task LogIgnoredWorkflowRunIdAsync(
        string? requestedWorkflowRunId,
        Guid activeWorkflowRunId,
        string phaseName,
        string actionName,
        IWorkflowRunLogStore logs,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedWorkflowRunId) &&
            !string.Equals(requestedWorkflowRunId, activeWorkflowRunId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await logs.AppendLineAsync(
                activeWorkflowRunId,
                $"Ignored agent-supplied workflowRunId '{requestedWorkflowRunId}' for action '{actionName}' in phase '{phaseName}'. Using active workflow run '{activeWorkflowRunId}'.",
                ct);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal enum AgentPhaseMode
    {
        Interactive,
        ContextOnly
    }

    internal sealed record AgentPhaseDefinition(
        string Name,
        string Prompt,
        bool RequiresSavedOutput,
        bool AllowRepositoryDiscovery,
        string PurposeSummary,
        AgentPhaseMode Mode = AgentPhaseMode.Interactive,
        bool RequireWorkflowInput = false,
        bool RequireCompletionValidation = false);

    internal sealed record RepositoryDiscoveryTools(
        Func<string?, CancellationToken, Task<IReadOnlyList<RepositoryEntry>>> ListRepositoryTreeAsync,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<FileSearchHit>>> SearchRepositoryAsync);

    private sealed class AgentExecutionState
    {
        public bool WorkflowInputLoaded { get; set; }
        public bool RepositoryDiscoveryUsed { get; set; }
        public HashSet<string> ReadPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FileReadResult(string FullPath, string NormalizedPath, string Content);

    private sealed record AvailableFileIndex(
        IReadOnlyList<string> FullPaths,
        IReadOnlyDictionary<string, string> NormalizedFullPaths);

    private sealed class ToolRequest
    {
        public string? Action { get; set; }
        public string? Tool { get; set; }
        public string? WorkflowRunId { get; set; }
        public string? Path { get; set; }
        public string? Query { get; set; }
        public JsonElement? Output { get; set; }

        public string ResolvedAction => !string.IsNullOrWhiteSpace(Action)
            ? Action!
            : Tool ?? string.Empty;
    }
}
