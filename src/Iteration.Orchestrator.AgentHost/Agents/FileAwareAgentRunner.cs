using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    private const int MaxToolCallsPerPhase = 16;
    private const int MaxFileCharacters = 8000;
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
            PurposeSummary: "Run the workflow in a single prompt/tool loop.");

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

        await logs.AppendSectionAsync(workflowRunId, logTitle, ct);
        await logs.AppendBlockAsync(workflowRunId, $"Prompt: {logTitle}", prompt, ct);
        await logs.AppendLineAsync(workflowRunId, $"Model '{model}' using per-response timeout of {responseTimeoutSeconds} seconds.", ct);

        var rawText = await RunModelWithTimeoutAsync(agent, prompt, responseTimeoutSeconds, ct);

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
        var allowedPathSet = allowedPaths
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var responseTimeoutSeconds = maxModelResponseSeconds;
        var requiredFrameworkPathSet = (requiredFrameworkPaths ?? Array.Empty<string>())
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredSolutionPathSet = (requiredSolutionPaths ?? Array.Empty<string>())
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var transcript = new StringBuilder();
        var state = new AgentExecutionState();

        await logs.AppendLineAsync(workflowRunId, $"Model '{model}' using per-response timeout of {responseTimeoutSeconds} seconds.", ct);

        for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            await logs.AppendSectionAsync(workflowRunId, $"Agent phase {phaseIndex + 1}: {phase.Name}", ct);
            await logs.AppendBlockAsync(workflowRunId, $"Phase prompt: {phase.Name}", phase.Prompt, ct);

            var phaseResult = await ExecutePhaseAsync(
                agent,
                phase,
                phaseIndex,
                phases.Count,
                repositoryRoot,
                allowedPathSet,
                workflowRunId,
                logs,
                payloadStore,
                discoveryTools,
                transcript,
                state,
                ct,
                requiredFrameworkPathSet,
                requiredSolutionPathSet,
                requireRepositoryEvidence,
                requireRepositoryDiscovery,
                responseTimeoutSeconds);

            if (phase.RequiresSavedOutput)
            {
                return phaseResult;
            }

            transcript.AppendLine($"PHASE COMPLETED: {phase.Name}");
            transcript.AppendLine("--- PHASE SUMMARY START ---");
            transcript.AppendLine(phaseResult.Trim());
            transcript.AppendLine("--- PHASE SUMMARY END ---");
            transcript.AppendLine();
        }

        throw new InvalidOperationException("Multi-step agent execution finished without saving a final workflow output.");
    }

    private static async Task<string> ExecutePhaseAsync(
        AIAgent agent,
        AgentPhaseDefinition phase,
        int phaseIndex,
        int totalPhases,
        string repositoryRoot,
        IReadOnlySet<string> allowedPathSet,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        RepositoryDiscoveryTools? discoveryTools,
        StringBuilder transcript,
        AgentExecutionState state,
        CancellationToken ct,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool requireRepositoryDiscovery,
        int maxModelResponseSeconds)
    {
        var currentPrompt = BuildPhasePrompt(phase, phaseIndex, totalPhases, transcript.ToString());

        for (var i = 0; i < MaxToolCallsPerPhase; i++)
        {
            var rawText = await RunModelWithTimeoutAsync(agent, currentPrompt, maxModelResponseSeconds, ct);
            await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} - agent response #{i + 1}", rawText, ct);

            if (!TryParseToolRequest(rawText, out var toolRequest))
            {
                if (phase.RequiresSavedOutput)
                {
                    throw new InvalidOperationException($"Final phase '{phase.Name}' ended without save_workflow_output.");
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
                    await logs.AppendBlockAsync(workflowRunId, $"Workflow input payload ({phase.Name})", inputPayload.InputPayloadJson, ct);

                    AppendToolInteraction(transcript, rawText, $"TOOL RESULT FOR get_workflow_input('{workflowRunId}'):",
                    [
                        "--- WORKFLOW INPUT START ---",
                        inputPayload.InputPayloadJson,
                        "--- WORKFLOW INPUT END ---"
                    ]);

                    currentPrompt = BuildPhasePrompt(phase, phaseIndex, totalPhases, transcript.ToString());
                    continue;
                }
                case "read_file":
                case "get_file":
                {
                    if (string.IsNullOrWhiteSpace(toolRequest.Path))
                    {
                        throw new InvalidOperationException($"Agent requested {normalizedAction} without a valid 'path'.");
                    }

                    var normalizedPath = NormalizePath(toolRequest.Path);
                    if (!allowedPathSet.Contains(normalizedPath))
                    {
                        throw new InvalidOperationException($"Agent requested file outside allowed scope: {normalizedPath}");
                    }

                    var fileContent = ReadFile(repositoryRoot, normalizedPath);
                    state.ReadPaths.Add(normalizedPath);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): {normalizedAction}('{normalizedPath}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, $"File content: {normalizedPath}", fileContent, ct);

                    AppendToolInteraction(transcript, rawText, $"TOOL RESULT FOR {normalizedAction}('{normalizedPath}'):",
                    [
                        "--- FILE CONTENT START ---",
                        fileContent,
                        "--- FILE CONTENT END ---"
                    ]);

                    currentPrompt = BuildPhasePrompt(phase, phaseIndex, totalPhases, transcript.ToString());
                    continue;
                }
                case "list_repo_tree":
                case "list_repository_tree":
                {
                    if (!phase.AllowRepositoryDiscovery || discoveryTools?.ListRepositoryTreeAsync is null)
                    {
                        throw new InvalidOperationException($"Agent requested {normalizedAction}, but repository discovery is not enabled for phase '{phase.Name}'.");
                    }

                    var scope = NormalizeOptionalPath(toolRequest.Path);
                    var entries = await discoveryTools.ListRepositoryTreeAsync(scope, ct);
                    state.RepositoryDiscoveryUsed = true;
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): {normalizedAction}('{scope ?? "."}').", ct);

                    var treeResult = FormatRepositoryTree(entries, scope);
                    await logs.AppendBlockAsync(workflowRunId, $"Repository tree ({phase.Name})", treeResult, ct);
                    AppendToolInteraction(transcript, rawText, $"TOOL RESULT FOR {normalizedAction}('{scope ?? "."}'):", [treeResult]);

                    currentPrompt = BuildPhasePrompt(phase, phaseIndex, totalPhases, transcript.ToString());
                    continue;
                }
                case "search_repo":
                case "search_files":
                {
                    if (!phase.AllowRepositoryDiscovery || discoveryTools?.SearchRepositoryAsync is null)
                    {
                        throw new InvalidOperationException($"Agent requested {normalizedAction}, but repository discovery is not enabled for phase '{phase.Name}'.");
                    }

                    if (string.IsNullOrWhiteSpace(toolRequest.Query))
                    {
                        throw new InvalidOperationException($"Agent requested {normalizedAction} without a valid 'query'.");
                    }

                    var scope = NormalizeOptionalPath(toolRequest.Path);
                    var hits = await discoveryTools.SearchRepositoryAsync(toolRequest.Query, scope, ct);
                    state.RepositoryDiscoveryUsed = true;
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): {normalizedAction}(query='{toolRequest.Query}', path='{scope ?? "."}').", ct);

                    var searchResult = FormatSearchHits(toolRequest.Query, hits, scope);
                    await logs.AppendBlockAsync(workflowRunId, $"Repository search ({phase.Name})", searchResult, ct);
                    AppendToolInteraction(transcript, rawText, $"TOOL RESULT FOR {normalizedAction}(query='{toolRequest.Query}', path='{scope ?? "."}'):", [searchResult]);

                    currentPrompt = BuildPhasePrompt(phase, phaseIndex, totalPhases, transcript.ToString());
                    continue;
                }
                case "save_workflow_output":
                {
                    if (!phase.RequiresSavedOutput)
                    {
                        throw new InvalidOperationException($"Agent attempted to save workflow output during non-final phase '{phase.Name}'.");
                    }

                    if (toolRequest.Output is null || toolRequest.Output.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    {
                        throw new InvalidOperationException("Agent requested save_workflow_output without an 'output' object.");
                    }

                    await LogIgnoredWorkflowRunIdAsync(toolRequest.WorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);

                    EnsureRequiredContextLoaded(
                        state.WorkflowInputLoaded,
                        state.ReadPaths,
                        requiredFrameworkPathSet,
                        requiredSolutionPathSet,
                        requireRepositoryEvidence,
                        state.RepositoryDiscoveryUsed,
                        requireRepositoryDiscovery);

                    ValidateWorkflowOutputPayload(toolRequest.Output.Value);

                    var outputJson = JsonSerializer.Serialize(toolRequest.Output.Value, JsonOptions);
                    await payloadStore.SaveOutputAsync(workflowRunId, outputJson, ct);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): save_workflow_output('{workflowRunId}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, "Saved workflow output payload", outputJson, ct);
                    return outputJson;
                }
                default:
                    throw new InvalidOperationException($"Unsupported tool action requested by agent: {toolRequest.ResolvedAction}");
            }
        }

        throw new InvalidOperationException($"Agent exceeded the maximum of {MaxToolCallsPerPhase} tool calls during phase '{phase.Name}'.");
    }

    private static async Task LogIgnoredWorkflowRunIdAsync(
        string? requestedWorkflowRunId,
        Guid activeWorkflowRunId,
        string phaseName,
        string action,
        IWorkflowRunLogStore logs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedWorkflowRunId))
        {
            return;
        }

        if (!Guid.TryParse(requestedWorkflowRunId, out var parsedWorkflowRunId))
        {
            await logs.AppendLineAsync(
                activeWorkflowRunId,
                $"Tool call ({phaseName}): ignored invalid workflowRunId '{requestedWorkflowRunId}' on {action}; using active workflowRunId '{activeWorkflowRunId}'.",
                ct);
            return;
        }

        if (parsedWorkflowRunId != activeWorkflowRunId)
        {
            await logs.AppendLineAsync(
                activeWorkflowRunId,
                $"Tool call ({phaseName}): ignored agent workflowRunId '{parsedWorkflowRunId}' on {action}; using active workflowRunId '{activeWorkflowRunId}'.",
                ct);
        }
    }

    private static void EnsureRequiredContextLoaded(
        bool workflowInputLoaded,
        IReadOnlySet<string> readPaths,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool repositoryDiscoveryUsed,
        bool requireRepositoryDiscovery)
    {
        if (!workflowInputLoaded)
        {
            throw new InvalidOperationException("Agent attempted to save output before loading workflow input.");
        }

        var missingFrameworkFiles = requiredFrameworkPathSet
            .Where(path => !readPaths.Contains(path))
            .ToList();
        if (missingFrameworkFiles.Count > 0)
        {
            throw new InvalidOperationException(
                $"Agent attempted to save output before loading required framework context. Missing: {string.Join(", ", missingFrameworkFiles)}");
        }

        var missingSolutionFiles = requiredSolutionPathSet
            .Where(path => !readPaths.Contains(path))
            .ToList();
        if (missingSolutionFiles.Count > 0)
        {
            throw new InvalidOperationException(
                $"Agent attempted to save output before loading required solution context. Missing: {string.Join(", ", missingSolutionFiles)}");
        }

        if (requireRepositoryDiscovery && !repositoryDiscoveryUsed)
        {
            throw new InvalidOperationException("Agent attempted to save output before using repository discovery tools.");
        }

        if (requireRepositoryEvidence)
        {
            var repositoryEvidenceRead = readPaths.Any(path =>
                !requiredFrameworkPathSet.Contains(path) &&
                !requiredSolutionPathSet.Contains(path));

            if (!repositoryEvidenceRead)
            {
                throw new InvalidOperationException("Agent attempted to save output before reading repository evidence files.");
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
        string prompt,
        int maxModelResponseSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxModelResponseSeconds));

        try
        {
            var rawResponse = await agent.RunAsync(prompt, cancellationToken: timeoutCts.Token);
            return rawResponse.Text ?? string.Empty;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Model response exceeded {maxModelResponseSeconds} seconds.", ex);
        }
    }

    private static string? NormalizeOptionalPath(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? null : NormalizePath(relativePath);

    private static string ReadFile(string repositoryRoot, string relativePath)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid path requested: {relativePath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Requested file does not exist: {relativePath}");
        }

        var text = File.ReadAllText(fullPath);
        if (text.Length <= MaxFileCharacters)
        {
            return text;
        }

        return text[..MaxFileCharacters] + $"\n\n[TRUNCATED BY SERVER: file content exceeded {MaxFileCharacters} characters]";
    }

    private static string BuildPhasePrompt(AgentPhaseDefinition phase, int phaseIndex, int totalPhases, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PHASE {phaseIndex + 1} OF {totalPhases}: {phase.Name}");
        sb.AppendLine();
        sb.AppendLine(phase.PurposeSummary.Trim());
        sb.AppendLine();
        sb.AppendLine(phase.Prompt.TrimEnd());

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            sb.AppendLine();
            sb.AppendLine("EXECUTION HISTORY:");
            sb.AppendLine(transcript.TrimEnd());
        }

        sb.AppendLine();
        sb.AppendLine("PHASE RULES:");
        sb.AppendLine("- Return either a single JSON tool call object or a concise plain-text phase summary.");
        sb.AppendLine("- Do not include markdown fences.");
        sb.AppendLine("- Keep phase summaries concrete and evidence-based.");
        sb.AppendLine("- Preserve any useful findings for later phases.");

        if (phase.AllowRepositoryDiscovery)
        {
            sb.AppendLine("- You may use discovery tools list_repo_tree and search_repo to explore before reading files.");
        }

        if (phase.RequiresSavedOutput)
        {
            sb.AppendLine("- This is the final phase: you MUST finish by calling save_workflow_output.");
            sb.AppendLine("- In the final phase, do not call get_workflow_input, read_file, get_file, list_repo_tree, or search_repo.");
            sb.AppendLine("- Return exactly one JSON object with properties action, workflowRunId, and output.");
            sb.AppendLine("- Use property name 'action', not 'tool'.");
        }
        else
        {
            sb.AppendLine("- Do not call save_workflow_output in this phase.");
        }

        return sb.ToString();
    }

    private static void AppendToolInteraction(StringBuilder transcript, string rawRequest, string resultHeader, IEnumerable<string> resultLines)
    {
        transcript.AppendLine("AGENT TOOL REQUEST:");
        transcript.AppendLine(rawRequest.Trim());
        transcript.AppendLine();
        transcript.AppendLine(resultHeader);
        foreach (var line in resultLines)
        {
            transcript.AppendLine(line);
        }

        transcript.AppendLine();
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal sealed record AgentPhaseDefinition(
        string Name,
        string Prompt,
        bool RequiresSavedOutput,
        bool AllowRepositoryDiscovery,
        string PurposeSummary);

    internal sealed record RepositoryDiscoveryTools(
        Func<string?, CancellationToken, Task<IReadOnlyList<RepositoryEntry>>> ListRepositoryTreeAsync,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<FileSearchHit>>> SearchRepositoryAsync);

    private sealed class AgentExecutionState
    {
        public bool WorkflowInputLoaded { get; set; }
        public bool RepositoryDiscoveryUsed { get; set; }
        public HashSet<string> ReadPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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
