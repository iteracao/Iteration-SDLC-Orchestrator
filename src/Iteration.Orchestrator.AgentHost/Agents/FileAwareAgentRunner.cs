using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    private const int MaxToolCallsPerPhase = 16;
    private const int MaxListedFiles = 512;
    private const int TargetBatchCharacters = 90000;
    private const int MaxSearchHits = 12;
    private const int MaxToolObjectsPerResponse = 8;

    public static Task<string> RunAsync(
        IAgentConversationFactory conversationFactory,
        string agentName,
        string instructions,
        string initialPrompt,
        string repositoryRoot,
        IReadOnlyCollection<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore? artifacts,
        CancellationToken ct,
        IReadOnlyCollection<string>? requiredFrameworkPaths = null,
        IReadOnlyCollection<string>? requiredSolutionPaths = null,
        bool requireRepositoryEvidence = false,
        int maxModelResponseSeconds = 180,
        IReadOnlyDictionary<string, string>? writableFiles = null)
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
            conversationFactory,
            agentName,
            instructions,
            [phase],
            repositoryRoot,
            allowedPaths,
            workflowRunId,
            logs,
            payloadStore,
            artifacts,
            ct,
            requiredFrameworkPaths,
            requiredSolutionPaths,
            requireRepositoryEvidence,
            requireRepositoryDiscovery: false,
            discoveryTools: null,
            maxModelResponseSeconds: maxModelResponseSeconds,
            writableFiles: null);
    }

    public static async Task<string> RunPromptAsync(
        IAgentConversationFactory conversationFactory,
        string agentName,
        string instructions,
        string prompt,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        string logTitle,
        CancellationToken ct,
        int maxModelResponseSeconds = 180)
    {
        var conversation = conversationFactory.CreateConversation(agentName, instructions);
        var responseTimeoutSeconds = maxModelResponseSeconds;

        await logs.AppendSectionAsync(workflowRunId, logTitle, ct);
        await logs.AppendLineAsync(workflowRunId, $"Model base instructions registered for {logTitle} ({instructions.Length} chars).", ct);
        await logs.AppendLineAsync(
            workflowRunId,
            $"Provider '{conversationFactory.ProviderName}' selected model '{conversationFactory.SelectedModel}' using per-response timeout of {responseTimeoutSeconds} seconds. OpenAI config complete: {conversationFactory.IsOpenAiConfigurationComplete}.",
            ct);

        var promptMessages = new[] { CreateUserMessage(prompt) };
        await LogModelInputMessagesAsync(logs, workflowRunId, logTitle, 1, promptMessages, ct);

        var response = await RunModelWithTimeoutAsync(
            conversation,
            promptMessages,
            tools: null,
            responseTimeoutSeconds,
            ct);

        if (response.ToolCalls.Count > 0)
        {
            throw new InvalidOperationException("Prompt-only execution received unexpected tool calls.");
        }

        await logs.AppendBlockAsync(workflowRunId, $"Response: {logTitle}", response.Text, ct);
        return response.Text;
    }

    public static async Task<string> RunMultiStepAsync(
        IAgentConversationFactory conversationFactory,
        string agentName,
        string instructions,
        IReadOnlyList<AgentPhaseDefinition> phases,
        string repositoryRoot,
        IReadOnlyCollection<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore? artifacts,
        CancellationToken ct,
        IReadOnlyCollection<string>? requiredFrameworkPaths = null,
        IReadOnlyCollection<string>? requiredSolutionPaths = null,
        bool requireRepositoryEvidence = false,
        bool requireRepositoryDiscovery = false,
        RepositoryDiscoveryTools? discoveryTools = null,
        int maxModelResponseSeconds = 180,
        IReadOnlyDictionary<string, string>? writableFiles = null)
    {
        if (phases.Count == 0)
        {
            throw new InvalidOperationException("At least one agent phase is required.");
        }

        var conversation = conversationFactory.CreateConversation(agentName, instructions);
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

        await logs.AppendLineAsync(
            workflowRunId,
            $"Provider '{conversationFactory.ProviderName}' selected model '{conversationFactory.SelectedModel}' using per-response timeout of {responseTimeoutSeconds} seconds. OpenAI config complete: {conversationFactory.IsOpenAiConfigurationComplete}.",
            ct);
        await logs.AppendLineAsync(workflowRunId, $"Model base instructions registered ({instructions.Length} chars).", ct);

        for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            await logs.AppendSectionAsync(workflowRunId, $"Agent phase {phaseIndex + 1}: {phase.Name}", ct);
            if (phase.SummarizePromptInLogs)
            {
                await logs.AppendLineAsync(workflowRunId, BuildPhasePromptSummary(phase), ct);
            }
            else
            {
                await logs.AppendBlockAsync(workflowRunId, $"Phase prompt: {phase.Name}", phase.Prompt, ct);
            }

            string phaseResult;
            if (phase.Mode == AgentPhaseMode.ContextOnly)
            {
                pendingMessages.Add(CreateUserMessage(phase.Prompt));
                await logs.AppendLineAsync(workflowRunId, $"Phase mode ({phase.Name}): ContextOnly. Prompt queued into agent conversation context with no model call.", ct);
                await logs.AppendBlockAsync(workflowRunId, $"Model message QUEUED [{phase.Name}] role=user source=context-only-prompt format=text/plain", phase.Prompt, ct);
                phaseResult = "Context loaded.";
            }
            else
            {
                phaseResult = await ExecutePhaseAsync(
                    conversation,
                    phase,
                    phaseIndex,
                    phases.Count,
                    repositoryRoot,
                    workflowRunId,
                    logs,
                    payloadStore,
                    artifacts,
                    discoveryTools,
                    availableFileIndex,
                    pendingMessages,
                    state,
                    ct,
                    requiredFrameworkPathSet,
                    requiredSolutionPathSet,
                    requireRepositoryEvidence,
                    requireRepositoryDiscovery,
                    responseTimeoutSeconds,
                    writableFiles);
            }

            if (phase.RequiresSavedOutput)
            {
                return phaseResult;
            }
        }

        throw new InvalidOperationException("Multi-step agent execution finished without producing a final workflow output.");
    }

    private static async Task<string> ExecutePhaseAsync(
        IAgentConversation conversation,
        AgentPhaseDefinition phase,
        int phaseIndex,
        int totalPhases,
        string repositoryRoot,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore? artifacts,
        RepositoryDiscoveryTools? discoveryTools,
        AvailableFileIndex availableFileIndex,
        List<ChatMessage> pendingMessages,
        AgentExecutionState state,
        CancellationToken ct,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool requireRepositoryDiscovery,
        int maxModelResponseSeconds,
        IReadOnlyDictionary<string, string>? writableFiles)
    {
        var currentMessages = BuildPhaseMessages(phase, phaseIndex, totalPhases, pendingMessages, writableFiles);
        if (pendingMessages.Count > 0)
        {
            await logs.AppendLineAsync(workflowRunId, $"{phase.Name}: carrying {pendingMessages.Count} context message(s) from previous phase.", ct);
        }
        pendingMessages.Clear();

        var nextUnreadFileIndex = 0;
        var writtenPathsThisPhase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolRegistry = CreateToolRegistry(
            workflowRunId,
            phase.Name,
            logs,
            payloadStore,
            availableFileIndex,
            state,
            writableFiles,
            writtenPathsThisPhase,
            () => nextUnreadFileIndex,
            value => nextUnreadFileIndex = value);
        var toolExecutor = new AgentToolExecutor(
            toolRegistry,
            phase.AllowToolCalls
                ? phase.AllowedToolActions
                : Array.Empty<string>());
        var nativeToolDefinitions = phase.AllowToolCalls
            ? toolExecutor.GetAllowedDefinitions()
            : Array.Empty<AgentToolDefinition>();

        for (var i = 0; i < MaxToolCallsPerPhase; i++)
        {
            var summarizedPhasePrompt = phase.SummarizePromptInLogs
                ? BuildPhasePrompt(phase, phaseIndex, totalPhases, writableFiles)
                : null;
            var interaction = BeginInteractionLog(phase.Name, i + 1, currentMessages, summarizedPhasePrompt);
            var response = await RunModelWithTimeoutAsync(
                conversation,
                currentMessages,
                nativeToolDefinitions,
                maxModelResponseSeconds,
                ct);
            var nativeToolAnalysis = AnalyzeNativeToolResponse(response);

            if (nativeToolAnalysis.State == NativeToolResponseState.MixedToolAndText)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(response.Text));
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: mixed native tool call and final Markdown/text in the same assistant response.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (nativeToolAnalysis.State == NativeToolResponseState.MultipleToolCalls)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "OUTPUT [assistant -> tool]", BuildToolCallOutputLog(response.ToolCalls));
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: multiple native tool calls in one assistant response.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (nativeToolAnalysis.State == NativeToolResponseState.SingleToolCall)
            {
                var nativeToolCall = nativeToolAnalysis.SingleToolCall!;
                AppendInteractionSection(interaction, "OUTPUT [assistant -> tool]", BuildToolCallOutputLog(nativeToolCall));

                if (phase.ResponseMode == AgentPhaseResponseMode.MarkdownOnly)
                {
                    const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only the final Markdown output. Do not return JSON. Do not call any tools.";
                    AppendInteractionSection(interaction, "VALIDATION", "Rejected: native tool call returned in a Markdown-only phase.");
                    AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateUserMessage(retryMessage)];
                    continue;
                }

                if (!phase.AllowToolCalls)
                {
                    const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only a short plain Markdown response. Do not return JSON. Do not call any tools.";
                    AppendInteractionSection(interaction, "VALIDATION", $"Rejected: native tool call '{nativeToolCall.Name}' returned in a no-tool phase.");
                    AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateUserMessage(retryMessage)];
                    continue;
                }

                var nativeToolResult = await ExecuteToolCallAsync(
                    toolExecutor,
                    nativeToolCall,
                    interaction,
                    workflowRunId,
                    phase,
                    i + 1,
                    logs,
                    ct);

                if (!string.IsNullOrWhiteSpace(nativeToolResult.FinalPhaseOutput))
                {
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    return nativeToolResult.FinalPhaseOutput!;
                }

                currentMessages = BuildPostToolMessages(nativeToolCall, nativeToolResult.ModelPayload);

                if (HandlePostToolExecution(
                    nativeToolCall,
                    nativeToolResult,
                    phase,
                    workflowRunId,
                    availableFileIndex,
                    state,
                    interaction,
                    pendingMessages,
                    out var earlyPhaseResult))
                {
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    return earlyPhaseResult!;
                }

                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                continue;
            }

            var rawText = response.Text;
            var toolResponseState = AnalyzeToolResponse(rawText);

            if (toolResponseState.State == ToolResponseState.MixedToolAndText)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(rawText));
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: mixed tool call and final Markdown/text in the same assistant message.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.MultipleToolObjects)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(rawText));
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: multiple tool-call JSON objects in one assistant message.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.SingleToolObject && phase.ResponseMode == AgentPhaseResponseMode.MarkdownOnly)
            {
                const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only the final Markdown output. Do not return JSON. Do not call any tools.";
                AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(rawText));
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: tool call returned in a Markdown-only phase.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.NoToolObject)
            {
                AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(rawText));
                if (phase.ResponseMode == AgentPhaseResponseMode.ToolCallsOnly)
                {
                    var retryMessage = BuildInvalidResponseRetryMessage(phase);
                    AppendInteractionSection(interaction, "VALIDATION", "Rejected: this phase requires exactly one JSON tool-call object.");
                    AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateUserMessage(retryMessage)];
                    continue;
                }

                if (phase.AllowedToolActions?.Contains("write_file", StringComparer.OrdinalIgnoreCase) == true)
                {
                    var pendingWritePaths = ExtractDeclaredWritePaths(rawText);
                    var missingWritePaths = pendingWritePaths
                        .Where(path => !writtenPathsThisPhase.Contains(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (missingWritePaths.Length > 0)
                    {
                        var retryMessage = $"INVALID FINAL RESPONSE. Execute write_file first for these declared paths before returning final Markdown: {string.Join(", ", missingWritePaths)}.";
                        AppendInteractionSection(interaction, "VALIDATION", $"Rejected: final Markdown declared writes that were not executed: {string.Join(", ", missingWritePaths)}.");
                        AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateUserMessage(retryMessage)];
                        continue;
                    }
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
                        phase.RequireAllAvailableFilesRead,
                        availableFileIndex,
                        state.RepositoryDiscoveryUsed,
                        requireRepositoryDiscovery);
                }

                if (!string.IsNullOrWhiteSpace(phase.SavedMarkdownArtifactFileName))
                {
                    if (artifacts is null)
                    {
                        throw new InvalidOperationException($"Phase '{phase.Name}' requires artifact persistence, but no artifact store is configured.");
                    }

                    await artifacts.SaveTextAsync(workflowRunId, phase.SavedMarkdownArtifactFileName, rawText, ct);
                    AppendInteractionSection(interaction, "ARTIFACT", $"Saved '{phase.SavedMarkdownArtifactFileName}' ({rawText.Length} chars).");

                    if (phase.InjectSavedMarkdownIntoNextPhase)
                    {
                        var savedMarkdownContextMessage = BuildSavedMarkdownContextMessage(phase.SavedMarkdownArtifactFileName, rawText);
                        pendingMessages.Add(CreateUserMessage(savedMarkdownContextMessage));
                        AppendInteractionSection(interaction, "NEXT PHASE CONTEXT", $"Queued artifact '{phase.SavedMarkdownArtifactFileName}' as context ({rawText.Length} chars).");
                    }
                }

                if (phase.RequiresSavedOutput)
                {
                    AppendInteractionSection(interaction, "RESULT", "Accepted as final workflow output.");
                }
                else if (string.IsNullOrWhiteSpace(phase.SavedMarkdownArtifactFileName))
                {
                    AppendInteractionSection(interaction, "RESULT", "Markdown/text response accepted.");
                }

                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                return rawText;
            }

            var toolRequest = toolResponseState.SingleToolRequest!;
            var fallbackToolCall = BuildFallbackToolCall(toolRequest);
            AppendInteractionSection(interaction, "OUTPUT [assistant -> tool]", BuildToolCallOutputLog(fallbackToolCall));
            if (!phase.AllowToolCalls)
            {
                const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only a short plain Markdown response. Do not return JSON. Do not call any tools.";
                AppendInteractionSection(interaction, "VALIDATION", $"Rejected: tool call '{toolRequest.ResolvedAction}' returned in a no-tool phase.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            var fallbackToolResult = await ExecuteToolCallAsync(
                toolExecutor,
                fallbackToolCall,
                interaction,
                workflowRunId,
                phase,
                i + 1,
                logs,
                ct);

            if (!string.IsNullOrWhiteSpace(fallbackToolResult.FinalPhaseOutput))
            {
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                return fallbackToolResult.FinalPhaseOutput!;
            }

            currentMessages = BuildPostToolMessages(fallbackToolCall, fallbackToolResult.ModelPayload);

            if (HandlePostToolExecution(
                fallbackToolCall,
                fallbackToolResult,
                phase,
                workflowRunId,
                availableFileIndex,
                state,
                interaction,
                pendingMessages,
                out var fallbackEarlyPhaseResult))
            {
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                return fallbackEarlyPhaseResult!;
            }

            await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
            continue;
        }

        throw new InvalidOperationException($"Agent exceeded maximum of {MaxToolCallsPerPhase} tool interactions in phase '{phase.Name}'.");
    }


    private static string BuildInvalidResponseRetryMessage(AgentPhaseDefinition phase)
    {
        if (phase.ResponseMode == AgentPhaseResponseMode.ToolCallsOnly &&
            phase.AllowedToolActions is { Count: 1 } &&
            phase.AllowedToolActions.Contains("get_next_file_batch", StringComparer.OrdinalIgnoreCase))
        {
            return """
INVALID RESPONSE.

Return EXACTLY ONE JSON object and nothing else:
{"tool":"get_next_file_batch","args":{}}

Rules:
- one object only
- no additional text
- no multiple tool calls
""".Trim();
        }

        if (phase.ResponseMode == AgentPhaseResponseMode.ToolCallsOnly &&
            phase.AllowedToolActions is { Count: > 0 })
        {
            return $"INVALID RESPONSE. Return exactly one JSON tool-call object and nothing else. Allowed tool actions: {string.Join(", ", phase.AllowedToolActions)}.";
        }

        return "INVALID RESPONSE. Return either one valid JSON tool-call object or the expected final Markdown/text response for this phase, but do not mix both.";
    }

    private static AgentToolRegistry CreateToolRegistry(
        Guid workflowRunId,
        string phaseName,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        AvailableFileIndex availableFileIndex,
        AgentExecutionState state,
        IReadOnlyDictionary<string, string>? writableFiles,
        HashSet<string> writtenPathsThisPhase,
        Func<int> getNextUnreadFileIndex,
        Action<int> setNextUnreadFileIndex)
    {
        return new AgentToolRegistry(
        [
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "get_workflow_input",
                    "Returns the workflow input payload for the active workflow run.",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "workflowRunId": {
                          "type": "string"
                        }
                      },
                      "additionalProperties": false
                    }
                    """),
                async (args, cancellationToken) =>
                {
                    await LogIgnoredWorkflowRunIdAsync(
                        GetOptionalStringProperty(args, "workflowRunId"),
                        workflowRunId,
                        phaseName,
                        "get_workflow_input",
                        logs,
                        cancellationToken);

                    var inputPayload = await payloadStore.GetInputAsync(workflowRunId, cancellationToken);
                    state.WorkflowInputLoaded = true;
                    var modelPayload = $"WORKFLOW INPUT FOR {workflowRunId}\n{inputPayload.InputPayloadJson}";
                    return new AgentToolExecutionResult(
                        modelPayload,
                        $"get_workflow_input -> {inputPayload.InputPayloadJson.Length} chars.");
                }),
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "find_available_files",
                    "Returns the allowed repository files available to this workflow phase.",
                    """
                    {
                      "type": "object",
                      "properties": {},
                      "additionalProperties": false
                    }
                    """),
                (_, _) =>
                {
                    if (state.FilesAlreadyListed)
                    {
                        return Task.FromResult(new AgentToolExecutionResult(
                            state.AvailableFilesPayload,
                            "find_available_files -> cached file list returned."));
                    }

                    var matchingPaths = FindAvailableFiles(availableFileIndex);
                    var fileListResult = FormatAvailableFiles(matchingPaths);
                    state.FilesAlreadyListed = true;
                    state.AvailableFilesPayload = fileListResult;
                    state.RepositoryDiscoveryUsed = true;
                    return Task.FromResult(new AgentToolExecutionResult(
                        fileListResult,
                        $"find_available_files -> {matchingPaths.Count} file path(s) returned."));
                }),
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "get_next_file_batch",
                    "Returns the next batch of allowed repository evidence files for the current workflow phase.",
                    """
                    {
                      "type": "object",
                      "properties": {},
                      "additionalProperties": false
                    }
                    """),
                (_, _) =>
                {
                    var batch = BuildNextFileBatch(availableFileIndex, getNextUnreadFileIndex());
                    setNextUnreadFileIndex(batch.NextUnreadFileIndex);
                    foreach (var file in batch.Files)
                    {
                        state.ReadPaths.Add(file.NormalizedPath);
                    }

                    return Task.FromResult(new AgentToolExecutionResult(
                        batch.Payload,
                        BuildBatchResultLog(batch)));
                }),
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "get_file",
                    "Returns one allowed repository file by full physical path.",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "path": {
                          "type": "string"
                        }
                      },
                      "required": ["path"],
                      "additionalProperties": false
                    }
                    """),
                (args, _) =>
                {
                    if (!state.FilesAlreadyListed)
                    {
                        const string toolPayload = "ERROR: call find_available_files first.";
                        return Task.FromResult(new AgentToolExecutionResult(
                            toolPayload,
                            "get_file rejected: find_available_files has not been called yet."));
                    }

                    var requestedPath = GetOptionalStringProperty(args, "path");
                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        const string toolPayload = "ERROR: get_file requires a non-empty 'path' with an exact full physical path previously returned by find_available_files.";
                        return Task.FromResult(new AgentToolExecutionResult(
                            toolPayload,
                            "get_file rejected: missing path."));
                    }

                    requestedPath = requestedPath.Trim();
                    var fileRead = TryReadFileByPhysicalPath(availableFileIndex, requestedPath);
                    if (fileRead is null)
                    {
                        const string toolPayload = "ERROR: path not allowed. Use only an exact full physical path returned by find_available_files for this run.";
                        return Task.FromResult(new AgentToolExecutionResult(
                            toolPayload,
                            $"get_file rejected: path is not allowed or does not exist: {requestedPath}."));
                    }

                    state.ReadPaths.Add(fileRead.NormalizedPath);
                    var toolPayloadForFile = BuildFullFileBatchBlock(fileRead.FullPath, fileRead.NormalizedPath, fileRead.Content);
                    return Task.FromResult(new AgentToolExecutionResult(
                        toolPayloadForFile,
                        $"get_file -> {fileRead.FullPath} ({fileRead.Content.Length} chars). Complete file loaded."));
                }),
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "write_file",
                    "Writes Markdown content to an approved managed documentation file.",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "path": {
                          "type": "string",
                          "description": "Full approved physical path."
                        },
                        "content": {
                          "type": "string",
                          "description": "Complete Markdown file content."
                        }
                      },
                      "required": ["path", "content"],
                      "additionalProperties": false
                    }
                    """),
                async (args, cancellationToken) =>
                {
                    var requestedPath = GetOptionalStringProperty(args, "path");
                    var content = GetOptionalStringProperty(args, "content");

                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        var writePathErrorPayload = BuildWriteFilePathErrorMessage(writableFiles);
                        return new AgentToolExecutionResult(
                            writePathErrorPayload,
                            "write_file rejected: missing path.");
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        var writeContentErrorPayload = BuildWriteFileContentErrorMessage();
                        return new AgentToolExecutionResult(
                            writeContentErrorPayload,
                            "write_file rejected: empty content.");
                    }

                    requestedPath = requestedPath.Trim();
                    if (writableFiles is null || !writableFiles.TryGetValue(requestedPath, out var fullWritePath))
                    {
                        var writeNotApprovedPayload = BuildWriteFileNotApprovedErrorMessage(writableFiles);
                        return new AgentToolExecutionResult(
                            writeNotApprovedPayload,
                            $"write_file rejected: path is not approved: {requestedPath}.");
                    }

                    var folder = Path.GetDirectoryName(fullWritePath);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    var normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + Environment.NewLine;
                    await File.WriteAllTextAsync(fullWritePath, normalizedContent, cancellationToken);
                    writtenPathsThisPhase.Add(requestedPath);
                    return new AgentToolExecutionResult(
                        $"OK: wrote '{requestedPath}'.",
                        $"write_file -> wrote {normalizedContent.Length} chars to '{requestedPath}'.");
                }),
            new DelegateAgentTool(
                AgentToolDefinition.Create(
                    "save_workflow_output",
                    "Saves the final workflow output JSON payload for the active workflow run.",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "workflowRunId": {
                          "type": "string"
                        },
                        "output": {
                          "type": "object"
                        }
                      },
                      "required": ["output"],
                      "additionalProperties": false
                    }
                    """),
                async (args, cancellationToken) =>
                {
                    if (!TryGetProperty(args, "output", out var output) || output.ValueKind != JsonValueKind.Object)
                    {
                        return new AgentToolExecutionResult(
                            "ERROR: save_workflow_output requires an 'output' JSON object. Retry with a valid output payload.",
                            "save_workflow_output rejected: missing output object.");
                    }

                    await LogIgnoredWorkflowRunIdAsync(
                        GetOptionalStringProperty(args, "workflowRunId"),
                        workflowRunId,
                        phaseName,
                        "save_workflow_output",
                        logs,
                        cancellationToken);

                    try
                    {
                        ValidateWorkflowOutputPayload(output);
                        var outputJson = output.GetRawText();
                        await payloadStore.SaveOutputAsync(workflowRunId, outputJson, cancellationToken);
                        return new AgentToolExecutionResult(
                            outputJson,
                            $"save_workflow_output -> saved output for workflow run {workflowRunId}.",
                            finalPhaseOutput: outputJson);
                    }
                    catch (Exception ex)
                    {
                        return new AgentToolExecutionResult(
                            $"ERROR: save_workflow_output failed validation: {ex.Message} Retry with a valid output object.",
                            $"save_workflow_output failed validation: {ex.Message}");
                    }
                })
        ]);
    }

    private static async Task<AgentToolExecutionResult> ExecuteToolCallAsync(
        AgentToolExecutor toolExecutor,
        AgentToolCall toolCall,
        StringBuilder interaction,
        Guid workflowRunId,
        AgentPhaseDefinition phase,
        int interactionNumber,
        IWorkflowRunLogStore logs,
        CancellationToken ct)
    {
        try
        {
            var result = await toolExecutor.ExecuteAsync(toolCall, ct);
            AppendInteractionSection(
                interaction,
                "TOOL RESULT",
                string.IsNullOrWhiteSpace(result.LogSummary)
                    ? $"{AgentToolExecutor.NormalizeToolName(toolCall.Name)} executed."
                    : result.LogSummary);

            if (!string.IsNullOrWhiteSpace(result.FinalPhaseOutput))
            {
                AppendInteractionSection(interaction, "WORKFLOW OUTPUT", result.FinalPhaseOutput!);
            }

            return result;
        }
        catch (InvalidOperationException ex)
        {
            var toolPayload = $"ERROR: {ex.Message}";
            AppendInteractionSection(interaction, "VALIDATION", $"Rejected tool call '{toolCall.Name}': {ex.Message}");
            AppendInteractionSection(interaction, "TOOL RESULT", toolPayload);
            return new AgentToolExecutionResult(toolPayload, toolPayload);
        }
    }

    private static bool HandlePostToolExecution(
        AgentToolCall toolCall,
        AgentToolExecutionResult toolResult,
        AgentPhaseDefinition phase,
        Guid workflowRunId,
        AvailableFileIndex availableFileIndex,
        AgentExecutionState state,
        StringBuilder interaction,
        List<ChatMessage> pendingMessages,
        out string? earlyPhaseResult)
    {
        earlyPhaseResult = null;
        var normalizedToolName = AgentToolExecutor.NormalizeToolName(toolCall.Name);

        if (string.Equals(normalizedToolName, "find_available_files", StringComparison.OrdinalIgnoreCase) &&
            phase.AutoLoadAllAvailableFilesAfterFindAvailableFiles)
        {
            pendingMessages.Add(CreateToolMessage(toolResult.ModelPayload));
            var autoLoadResult = AutoLoadRepositoryEvidence(availableFileIndex, state);
            foreach (var payload in autoLoadResult.ToolPayloads)
            {
                pendingMessages.Add(CreateToolMessage(payload));
            }

            AppendInteractionSection(interaction, "RESULT", $"Repository evidence loaded automatically. Batches prepared: {autoLoadResult.BatchCount}; files reviewed: {autoLoadResult.FilesReviewed}.");
            earlyPhaseResult = "Repository evidence acquisition completed.";
            return true;
        }

        if (string.Equals(normalizedToolName, "get_next_file_batch", StringComparison.OrdinalIgnoreCase) &&
            phase.AutoCompleteWhenAllAvailableFilesRead &&
            phase.RequireAllAvailableFilesRead &&
            availableFileIndex.FullPaths.Count > 0 &&
            availableFileIndex.NormalizedFullPaths.Keys.All(path => state.ReadPaths.Contains(path)))
        {
            AppendInteractionSection(interaction, "RESULT", "Repository evidence acquisition completed after all allowed files were reviewed.");
            earlyPhaseResult = "Repository evidence acquisition completed.";
            return true;
        }

        return false;
    }

    private static NativeToolResponseAnalysis AnalyzeNativeToolResponse(AgentConversationResponse response)
    {
        var toolCalls = response.ToolCalls;
        if (toolCalls.Count == 0)
        {
            return new NativeToolResponseAnalysis(NativeToolResponseState.NoToolCall, null);
        }

        if (toolCalls.Count > 1)
        {
            return new NativeToolResponseAnalysis(NativeToolResponseState.MultipleToolCalls, null);
        }

        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            return new NativeToolResponseAnalysis(NativeToolResponseState.MixedToolAndText, null);
        }

        return new NativeToolResponseAnalysis(NativeToolResponseState.SingleToolCall, toolCalls[0]);
    }

    private static string BuildToolCallOutputLog(IReadOnlyList<AgentToolCall> toolCalls)
        => string.Join(Environment.NewLine, toolCalls.Select(BuildToolCallOutputLog));

    private static string BuildToolCallOutputLog(AgentToolCall toolCall)
    {
        var normalizedToolName = AgentToolExecutor.NormalizeToolName(toolCall.Name);
        return normalizedToolName switch
        {
            "get_next_file_batch" => "get_next_file_batch()",
            "find_available_files" => "find_available_files()",
            "get_workflow_input" => BuildToolCallWithSingleOptionalStringArgument(normalizedToolName, "workflowRunId", toolCall.Arguments),
            "get_file" => BuildToolCallWithSingleOptionalStringArgument(normalizedToolName, "path", toolCall.Arguments),
            "write_file" => BuildWriteFileToolCallLog(toolCall.Arguments),
            "save_workflow_output" => "save_workflow_output(output=<object>)",
            _ => $"{normalizedToolName}({CountObjectProperties(toolCall.Arguments)} arg(s))"
        };
    }

    private static string BuildToolCallWithSingleOptionalStringArgument(string toolName, string argumentName, JsonElement arguments)
    {
        var argumentValue = GetOptionalStringProperty(arguments, argumentName);
        return string.IsNullOrWhiteSpace(argumentValue)
            ? $"{toolName}()"
            : $"{toolName}({argumentName}=\"{EscapeForLog(argumentValue)}\")";
    }

    private static string BuildWriteFileToolCallLog(JsonElement arguments)
    {
        var path = GetOptionalStringProperty(arguments, "path") ?? "<missing>";
        var contentLength = GetOptionalStringProperty(arguments, "content")?.Length ?? 0;
        return $"write_file(path=\"{EscapeForLog(path)}\", contentLength={contentLength})";
    }

    private static AgentToolCall BuildFallbackToolCall(ToolRequest request)
        => new(request.ResolvedAction, BuildFallbackArguments(request));

    private static JsonElement BuildFallbackArguments(ToolRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (request.Args is { ValueKind: JsonValueKind.Object } args)
            {
                foreach (var property in args.EnumerateObject())
                {
                    if (IsNormalizedArgumentProperty(property.Name))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }

            WriteStringPropertyIfPresent(writer, "path", request.ResolvedPath);
            WriteStringPropertyIfPresent(writer, "query", request.ResolvedQuery);
            WriteStringPropertyIfPresent(writer, "content", request.ResolvedContent);
            WriteStringPropertyIfPresent(writer, "workflowRunId", request.ResolvedWorkflowRunId);
            if (request.ResolvedOutput is JsonElement output)
            {
                writer.WritePropertyName("output");
                output.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static bool IsNormalizedArgumentProperty(string propertyName)
        => string.Equals(propertyName, "path", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "filePath", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "query", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "content", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "workflowRunId", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "output", StringComparison.OrdinalIgnoreCase);

    private static void WriteStringPropertyIfPresent(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetOptionalStringProperty(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int CountObjectProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in element.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static StringBuilder BeginInteractionLog(
        string phaseName,
        int interactionNumber,
        IReadOnlyList<ChatMessage> inputMessages,
        string? summarizedPhasePrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{phaseName} - interaction {interactionNumber}");
        AppendInteractionSection(sb, "INPUT", BuildInputMessagesLog(inputMessages, summarizedPhasePrompt));
        return sb;
    }

    private static string BuildPhasePromptSummary(AgentPhaseDefinition phase)
        => $"Phase prompt registered for {phase.Name} ({phase.Prompt.Length} chars). Content omitted from normal workflow log.";

    private static void AppendInteractionSection(StringBuilder sb, string title, string content)
    {
        if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            sb.AppendLine();
        }

        sb.AppendLine(title);
        var cleaned = CleanLogText(content);
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            sb.AppendLine(cleaned);
        }
    }

    private static string BuildInputMessagesLog(IReadOnlyList<ChatMessage> messages, string? summarizedPhasePrompt)
    {
        if (messages.Count == 0)
        {
            return "No input messages.";
        }

        var sb = new StringBuilder();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var content = GetChatMessageText(message);
            var source = InferMessageSource(content);
            var summary = ShouldSummarizePhasePrompt(content, summarizedPhasePrompt)
                ? $"Phase prompt input ({content.Length} chars). Content omitted from normal workflow log."
                : SummarizeInputMessageForLog(content);
            sb.AppendLine($"Message {index + 1}/{messages.Count} [{message.Role}; {source}; {content.Length} chars]");
            sb.AppendLine(summary);
            if (index < messages.Count - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool ShouldSummarizePhasePrompt(string content, string? summarizedPhasePrompt)
        => !string.IsNullOrWhiteSpace(summarizedPhasePrompt) &&
           string.Equals(content.TrimEnd(), summarizedPhasePrompt, StringComparison.Ordinal);

    private static string SummarizeInputMessageForLog(string content)
    {
        if (AgentToolMessageProtocol.TryParseNativeToolResultMessage(content, out var nativeToolResult))
        {
            return BuildNativeToolResultLog(nativeToolResult);
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("BATCH INDEX:", StringComparison.OrdinalIgnoreCase))
        {
            return BuildBatchHeaderSummary(content);
        }

        if (trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFilePayloadMetadataLog(content);
        }

        if (trimmed.StartsWith("SAVED MARKDOWN CONTEXT FROM PREVIOUS PHASE:", StringComparison.OrdinalIgnoreCase))
        {
            return BuildArtifactContextMetadataLog(content);
        }

        if (trimmed.StartsWith("WORKFLOW INPUT FOR", StringComparison.OrdinalIgnoreCase))
        {
            return $"Workflow input payload queued ({content.Length} chars).";
        }

        return SanitizeModelMessageForLog(content);
    }

    private static string BuildBatchHeaderSummary(string payload)
    {
        var batch = ExtractHeaderValue(payload, "BATCH INDEX") ?? "?";
        var total = ExtractHeaderValue(payload, "TOTAL BATCHES") ?? "?";
        var hasMore = ExtractHeaderValue(payload, "HAS MORE") ?? "?";
        var fileCount = ExtractHeaderValue(payload, "FILES IN BATCH") ?? "?";
        return $"Tool result from previous interaction: batch {batch}/{total}; files={fileCount}; hasMore={hasMore}.";
    }

    private static string BuildBatchResultLog(FileBatchResult batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"get_next_file_batch -> batch {batch.BatchNumber}/{batch.TotalBatchesEstimate}; files={batch.Files.Count}; hasMore={(batch.HasMore ? "yes" : "no")}; payloadChars={batch.Payload.Length}.");
        var payloadBatch = ExtractHeaderValue(batch.Payload, "BATCH INDEX");
        if (int.TryParse(payloadBatch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var payloadBatchNumber) &&
            payloadBatchNumber != batch.BatchNumber)
        {
            sb.AppendLine($"WARNING: batch metadata mismatch; payload batch={payloadBatchNumber}, result batch={batch.BatchNumber}.");
        }

        if (batch.Files.Count > 0)
        {
            var preview = batch.Files.Take(8).Select(file => $"- {Path.GetFileName(file.FullPath)} | {file.Content.Length} chars | {file.NormalizedPath}");
            foreach (var line in preview)
            {
                sb.AppendLine(line);
            }

            if (batch.Files.Count > 8)
            {
                sb.AppendLine($"- ... {batch.Files.Count - 8} more file(s) omitted from log preview.");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string SummarizeFileListForLog(string fileListResult)
    {
        var lines = fileListResult.Replace("\r\n", "\n").Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return "No files returned.";
        }

        var preview = lines.Take(12).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"File list returned: {lines.Length} path(s).");
        foreach (var line in preview)
        {
            sb.AppendLine($"- {line}");
        }

        if (lines.Length > preview.Count)
        {
            sb.AppendLine($"- ... {lines.Length - preview.Count} more path(s) omitted from log preview.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractHeaderValue(string payload, string header)
    {
        foreach (var rawLine in payload.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(header.Length + 1)..].Trim();
        }

        return null;
    }

    private static string CleanLogText(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        normalized = Regex.Replace(normalized, @"[ \t]+$", string.Empty, RegexOptions.Multiline);
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized;
    }

    private static async Task LogModelInputMessagesAsync(
        IWorkflowRunLogStore logs,
        Guid workflowRunId,
        string phaseName,
        int attempt,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct)
    {
        await logs.AppendLineAsync(
            workflowRunId,
            $"Model input ({phaseName}) attempt {attempt}: {messages.Count} message(s) sent.",
            ct);

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var content = GetChatMessageText(message);
            var source = InferMessageSource(content);
            var format = InferMessageFormat(content);
            var safeContent = SanitizeModelMessageForLog(content);

            await logs.AppendBlockAsync(
                workflowRunId,
                $"Model message OUT [{phaseName}] attempt={attempt} index={index + 1}/{messages.Count} role={message.Role} source={source} format={format} chars={content.Length}",
                safeContent,
                ct);
        }
    }

    private static string GetChatMessageText(ChatMessage message)
        => message.Text ?? string.Empty;

    private static string InferMessageSource(string content)
    {
        if (AgentToolMessageProtocol.TryParseNativeToolResultMessage(content, out var nativeToolResult))
        {
            return $"tool-result:{AgentToolExecutor.NormalizeToolName(nativeToolResult.ToolName)}";
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("PHASE ", StringComparison.OrdinalIgnoreCase)) return "phase-prompt";
        if (trimmed.StartsWith("SAVED MARKDOWN CONTEXT FROM PREVIOUS PHASE:", StringComparison.OrdinalIgnoreCase)) return "artifact-context";
        if (trimmed.StartsWith("BATCH INDEX:", StringComparison.OrdinalIgnoreCase)) return "tool-result:get_next_file_batch";
        if (trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase)) return "tool-result:get_file";
        if (trimmed.StartsWith("WORKFLOW INPUT FOR", StringComparison.OrdinalIgnoreCase)) return "tool-result:get_workflow_input";
        if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return "tool-error";
        if (trimmed.StartsWith("INVALID RESPONSE", StringComparison.OrdinalIgnoreCase)) return "retry-instruction";
        if (trimmed.StartsWith("TOOLS ARE NOT ALLOWED", StringComparison.OrdinalIgnoreCase)) return "retry-instruction";
        if (trimmed.StartsWith("OK:", StringComparison.OrdinalIgnoreCase)) return "tool-result";
        return "message";
    }

    private static string InferMessageFormat(string content)
    {
        if (AgentToolMessageProtocol.TryParseNativeToolResultMessage(content, out _))
        {
            return "application/vnd.agent-tool-result";
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}")) return "application/json";
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return "text/markdown";
        return "text/plain";
    }

    private static string SanitizeModelMessageForLog(string content)
    {
        if (AgentToolMessageProtocol.TryParseNativeToolResultMessage(content, out var nativeToolResult))
        {
            return BuildNativeToolResultLog(nativeToolResult);
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("BATCH INDEX:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFilePayloadMetadataLog(content);
        }

        if (trimmed.StartsWith("SAVED MARKDOWN CONTEXT FROM PREVIOUS PHASE:", StringComparison.OrdinalIgnoreCase))
        {
            return BuildArtifactContextMetadataLog(content);
        }

        return content;
    }

    private static string BuildNativeToolResultLog(AgentToolMessageProtocol.NativeToolResultEnvelope nativeToolResult)
    {
        var payloadSummary = SanitizeModelMessageForLog(nativeToolResult.Payload);
        return $"Structured tool result for {AgentToolExecutor.NormalizeToolName(nativeToolResult.ToolName)} (callId={nativeToolResult.CallId}).\n{payloadSummary}";
    }

    private static string BuildArtifactContextMetadataLog(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        var firstLine = lines.Length > 0 ? lines[0].Trim() : "SAVED MARKDOWN CONTEXT FROM PREVIOUS PHASE: <unknown>";
        var artifactName = firstLine.Contains(':') ? firstLine[(firstLine.IndexOf(':') + 1)..].Trim() : "<unknown>";
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var artifactContentLength = separator >= 0 ? normalized[(separator + 2)..].Trim().Length : 0;
        return $"Artifact context queued into model message.\nArtifact: {artifactName}\nArtifact content chars: {artifactContentLength}\nFull message chars: {content.Length}\nContent omitted from log.";
    }

    private static string BuildToolRequestLog(string rawText, ToolRequest request)
    {
        var action = request.ResolvedAction;
        if (string.Equals(action, "write_file", StringComparison.OrdinalIgnoreCase))
        {
            return "{\n" +
                   $"  \"tool\": \"write_file\",\n" +
                   $"  \"path\": \"{EscapeForLog(request.ResolvedPath ?? "<null>")}\",\n" +
                   $"  \"contentLength\": {request.ResolvedContent?.Length ?? 0},\n" +
                   $"  \"rawRequestChars\": {rawText.Length},\n" +
                   "  \"content\": \"<omitted from log>\"\n" +
                   "}";
        }

        return rawText.Trim();
    }

    private static string EscapeForLog(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string BuildFilePayloadMetadataLog(string payload)
    {
        var normalized = payload.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder();

        if (normalized.StartsWith("BATCH INDEX:", StringComparison.OrdinalIgnoreCase))
        {
            var batch = ExtractHeaderValue(normalized, "BATCH INDEX") ?? "?";
            var total = ExtractHeaderValue(normalized, "TOTAL BATCHES") ?? "?";
            var hasMore = ExtractHeaderValue(normalized, "HAS MORE") ?? "?";
            var fileCount = ExtractHeaderValue(normalized, "FILES IN BATCH") ?? "?";
            sb.AppendLine($"BATCH {batch}/{total} | files={fileCount} | hasMore={hasMore} | payloadChars={payload.Length}");
        }

        string? file = null;
        string? normalizedPath = null;
        string? chars = null;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                AppendCurrentFile();
                file = line[5..].Trim();
                normalizedPath = null;
                chars = null;
                continue;
            }

            if (line.StartsWith("NORMALIZED PATH:", StringComparison.OrdinalIgnoreCase))
            {
                normalizedPath = line[16..].Trim();
                continue;
            }

            if (line.StartsWith("CHARACTERS:", StringComparison.OrdinalIgnoreCase))
            {
                chars = line[11..].Trim();
                continue;
            }

            if (line.Equals("--- END FILE ---", StringComparison.OrdinalIgnoreCase))
            {
                AppendCurrentFile();
            }
        }

        AppendCurrentFile();
        return CleanLogText(sb.ToString());

        void AppendCurrentFile()
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            sb.Append("FILE: ").Append(file);
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                sb.Append(" | normalized=").Append(normalizedPath);
            }

            if (!string.IsNullOrWhiteSpace(chars))
            {
                sb.Append(" | chars=").Append(chars);
            }

            sb.AppendLine(" | content=<omitted>");
            file = null;
            normalizedPath = null;
            chars = null;
        }
    }

    private static IReadOnlyList<ChatMessage> BuildPhaseMessages(
        AgentPhaseDefinition phase,
        int phaseIndex,
        int totalPhases,
        IReadOnlyList<ChatMessage> pendingMessages,
        IReadOnlyDictionary<string, string>? writableFiles)
    {
        var messages = new List<ChatMessage>(pendingMessages.Count + 1);
        messages.AddRange(pendingMessages);
        messages.Add(CreateUserMessage(BuildPhasePrompt(phase, phaseIndex, totalPhases, writableFiles)));
        return messages;
    }

    private static void EnsureRequiredContextLoaded(
        bool requireWorkflowInput,
        bool workflowInputLoaded,
        IReadOnlyCollection<string> readPaths,
        IReadOnlySet<string> requiredFrameworkPathSet,
        IReadOnlySet<string> requiredSolutionPathSet,
        bool requireRepositoryEvidence,
        bool requireAllAvailableFilesRead,
        AvailableFileIndex availableFileIndex,
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

        if (requireAllAvailableFilesRead)
        {
            var missingCount = availableFileIndex.NormalizedFullPaths.Keys.Count(path => !readPaths.Contains(path));
            if (missingCount > 0)
            {
                throw new InvalidOperationException($"Agent attempted to complete the phase before reviewing all required repository context files. Missing file count: {missingCount}.");
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

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
            {
                trimmed = trimmed[(firstNewLine + 1)..].Trim();
                if (trimmed.EndsWith("```", StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^3].Trim();
                }
            }
        }

        if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    private static IReadOnlyList<ChatMessage> BuildPostToolMessages(string toolPayload)
    {
        return [CreateToolMessage(toolPayload)];
    }

    private static IReadOnlyList<ChatMessage> BuildPostToolMessages(AgentToolCall toolCall, string toolPayload)
    {
        if (toolCall.IsNative && !string.IsNullOrWhiteSpace(toolCall.NativeCallId))
        {
            return
            [
                CreateToolMessage(AgentToolMessageProtocol.BuildNativeToolResultMessage(
                    toolCall.NativeCallId!,
                    AgentToolExecutor.NormalizeToolName(toolCall.Name),
                    toolPayload))
            ];
        }

        return BuildPostToolMessages(toolPayload);
    }

    private static ToolResponseAnalysis AnalyzeToolResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
        }

        var trimmed = TrimCodeFence(raw).Trim();
        if (trimmed.Length == 0)
        {
            return new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
        }

        var requests = new List<ToolRequest>();
        var index = 0;
        while (index < trimmed.Length)
        {
            while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
            if (index >= trimmed.Length) break;
            if (trimmed[index] != '{')
            {
                return requests.Count > 0
                    ? new ToolResponseAnalysis(ToolResponseState.MixedToolAndText, null)
                    : new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
            }

            if (!TryReadSingleJsonObject(trimmed, index, out var json, out var nextIndex))
            {
                return requests.Count > 0
                    ? new ToolResponseAnalysis(ToolResponseState.MixedToolAndText, null)
                    : new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
            }

            try
            {
                var request = JsonSerializer.Deserialize<ToolRequest>(json, JsonOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.ResolvedAction))
                {
                    return requests.Count > 0
                        ? new ToolResponseAnalysis(ToolResponseState.MixedToolAndText, null)
                        : new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
                }

                requests.Add(request);
                if (requests.Count > MaxToolObjectsPerResponse)
                {
                    return new ToolResponseAnalysis(ToolResponseState.MultipleToolObjects, null);
                }
            }
            catch (JsonException)
            {
                return requests.Count > 0
                    ? new ToolResponseAnalysis(ToolResponseState.MixedToolAndText, null)
                    : new ToolResponseAnalysis(ToolResponseState.NoToolObject, null);
            }

            index = nextIndex;
        }

        return requests.Count switch
        {
            0 => new ToolResponseAnalysis(ToolResponseState.NoToolObject, null),
            1 => new ToolResponseAnalysis(ToolResponseState.SingleToolObject, requests[0]),
            _ => new ToolResponseAnalysis(ToolResponseState.MultipleToolObjects, null)
        };
    }

    private static string TrimCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine >= 0)
            {
                trimmed = trimmed[(firstNewLine + 1)..].Trim();
                if (trimmed.EndsWith("```", StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^3].Trim();
                }
            }
        }

        return trimmed;
    }

    private static bool TryReadSingleJsonObject(string text, int startIndex, out string json, out int nextIndex)
    {
        json = string.Empty;
        nextIndex = startIndex;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[startIndex..]), isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            json = document.RootElement.GetRawText();
            nextIndex = startIndex + (int)reader.BytesConsumed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractDeclaredWritePaths(string markdown)
    {
        var paths = new List<string>();
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('-', ' ');
            if (line.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(line[7..].Trim());
                continue;
            }

            if (line.StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(line[7..].Trim());
            }
        }

        return paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static AutoLoadRepositoryEvidenceResult AutoLoadRepositoryEvidence(AvailableFileIndex availableFileIndex, AgentExecutionState state)
    {
        var payloads = new List<string>();
        var nextUnreadFileIndex = 0;
        var batchCount = 0;
        var reviewedThisLoad = 0;
        while (nextUnreadFileIndex < availableFileIndex.FullPaths.Count)
        {
            var batch = BuildNextFileBatch(availableFileIndex, nextUnreadFileIndex);
            if (batch.Files.Count == 0)
            {
                break;
            }

            batchCount++;
            nextUnreadFileIndex = batch.NextUnreadFileIndex;
            payloads.Add(batch.Payload);
            foreach (var file in batch.Files)
            {
                if (state.ReadPaths.Add(file.NormalizedPath))
                {
                    reviewedThisLoad++;
                }
            }
        }

        return new AutoLoadRepositoryEvidenceResult(payloads, batchCount, reviewedThisLoad);
    }

    private static string NormalizePath(string relativePath)
        => relativePath.Replace('\\', '/').Trim();

    private static async Task<AgentConversationResponse> RunModelWithTimeoutAsync(
        IAgentConversation conversation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        int maxModelResponseSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxModelResponseSeconds));

        try
        {
            return await conversation.RunAsync(messages, tools, timeoutCts.Token);
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
        => availableFileIndex.FullPaths.ToList();

    private static string FormatAvailableFiles(IReadOnlyList<string> fullPaths)
    {
        if (fullPaths.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, fullPaths);
    }

    private static FileBatchResult BuildNextFileBatch(AvailableFileIndex availableFileIndex, int nextUnreadFileIndex)
    {
        if (nextUnreadFileIndex < 0)
        {
            nextUnreadFileIndex = 0;
        }

        if (nextUnreadFileIndex >= availableFileIndex.FullPaths.Count)
        {
            var emptyPayload = "END OF FILE BATCHES";
            return new FileBatchResult([], emptyPayload, nextUnreadFileIndex, false, 0, 0);
        }

        var files = new List<FileReadResult>();
        var payload = new StringBuilder();
        var accumulatedCharacters = 0;
        var currentIndex = nextUnreadFileIndex;

        while (currentIndex < availableFileIndex.FullPaths.Count)
        {
            var fullPath = availableFileIndex.FullPaths[currentIndex];
            currentIndex++;

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var originalContent = File.ReadAllText(fullPath);
            var normalizedPath = NormalizePath(fullPath);
            var blockText = BuildFullFileBatchBlock(fullPath, normalizedPath, originalContent);

            if (files.Count > 0 && accumulatedCharacters + blockText.Length > TargetBatchCharacters)
            {
                currentIndex--;
                break;
            }

            files.Add(new FileReadResult(fullPath, normalizedPath, originalContent));
            if (payload.Length > 0)
            {
                payload.AppendLine();
                payload.AppendLine();
            }

            payload.Append(blockText);
            accumulatedCharacters += blockText.Length;
        }

        var totalBatchesEstimate = EstimateTotalBatchCount(availableFileIndex);
        var batchNumber = CalculateBatchNumber(availableFileIndex, nextUnreadFileIndex);
        var hasMore = currentIndex < availableFileIndex.FullPaths.Count;
        var payloadText = payload.Length == 0
            ? $"BATCH INDEX: {batchNumber}\nTOTAL BATCHES: {totalBatchesEstimate}\nHAS MORE: {(hasMore ? "yes" : "no")}\nFILES IN BATCH: 0\n\nEND OF FILE BATCHES"
            : BuildFileBatchPayload(batchNumber, totalBatchesEstimate, hasMore, files.Count, payload.ToString());

        return new FileBatchResult(
            files,
            payloadText,
            currentIndex,
            hasMore,
            batchNumber,
            totalBatchesEstimate);
    }


    private static IReadOnlyList<string> DescribeBatchFilesForLog(IReadOnlyList<FileReadResult> files)
    {
        if (files.Count == 0)
        {
            return ["No files returned in this batch."];
        }

        var lines = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var positionLabel = i == 0
                ? "First file"
                : i == files.Count - 1
                    ? "Last file"
                    : "File";

            lines.Add($"{positionLabel}: {file.FullPath} | chars={file.Content.Length}");
        }

        return lines;
    }

    private static string BuildFullFileBatchBlock(string fullPath, string normalizedPath, string content)
    {
        var block = new StringBuilder();
        block.AppendLine($"FILE: {fullPath}");
        block.AppendLine($"NORMALIZED PATH: {normalizedPath}");
        block.AppendLine("COMPLETE FILE: yes");
        block.AppendLine($"CHARACTERS: {content.Length}");
        block.AppendLine("CONTENT:");
        block.AppendLine(content);
        block.AppendLine("--- END FILE ---");
        return block.ToString().TrimEnd();
    }

    private static string BuildFileBatchPayload(int batchNumber, int totalBatches, bool hasMore, int fileCount, string fileBlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BATCH INDEX: {batchNumber}");
        sb.AppendLine($"TOTAL BATCHES: {totalBatches}");
        sb.AppendLine($"HAS MORE: {(hasMore ? "yes" : "no")}");
        sb.AppendLine($"FILES IN BATCH: {fileCount}");
        sb.AppendLine();
        sb.Append(fileBlocks);
        return sb.ToString().TrimEnd();
    }

    private static int EstimateTotalBatchCount(AvailableFileIndex availableFileIndex)
    {
        const int startIndex = 0;

        if (startIndex >= availableFileIndex.FullPaths.Count)
        {
            return 0;
        }

        var count = 0;
        var index = startIndex;
        while (index < availableFileIndex.FullPaths.Count)
        {
            var batch = BuildNextFileBatchForEstimation(availableFileIndex, index);
            if (batch.NextIndex <= index)
            {
                break;
            }

            count++;
            index = batch.NextIndex;
        }

        return Math.Max(1, count);
    }

    private static int CalculateBatchNumber(AvailableFileIndex availableFileIndex, int startIndex)
    {
        if (startIndex <= 0)
        {
            return 1;
        }

        var batchNumber = 1;
        var index = 0;
        while (index < startIndex)
        {
            var batch = BuildNextFileBatchForEstimation(availableFileIndex, index);
            if (batch.NextIndex <= index)
            {
                break;
            }

            if (batch.NextIndex > startIndex)
            {
                return batchNumber;
            }

            batchNumber++;
            index = batch.NextIndex;
        }

        return batchNumber;
    }

    private static BatchEstimateResult BuildNextFileBatchForEstimation(AvailableFileIndex availableFileIndex, int startIndex)
    {
        var accumulatedCharacters = 0;
        var index = startIndex;
        var fileCount = 0;

        while (index < availableFileIndex.FullPaths.Count)
        {
            var fullPath = availableFileIndex.FullPaths[index];
            index++;

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var content = File.ReadAllText(fullPath);
            var normalizedPath = NormalizePath(fullPath);
            var blockLength = BuildFullFileBatchBlock(fullPath, normalizedPath, content).Length;

            if (fileCount > 0 && accumulatedCharacters + blockLength > TargetBatchCharacters)
            {
                index--;
                break;
            }

            accumulatedCharacters += blockLength;
            fileCount++;
        }

        return new BatchEstimateResult(index, fileCount);
    }

    private static string BuildPhasePrompt(AgentPhaseDefinition phase, int phaseIndex, int totalPhases, IReadOnlyDictionary<string, string>? writableFiles)
    {
        return phase.Prompt.TrimEnd();
    }

    private static string BuildSavedMarkdownContextMessage(string fileName, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SAVED MARKDOWN CONTEXT FROM PREVIOUS PHASE: {fileName}");
        sb.AppendLine();
        sb.AppendLine(content.Trim());
        return sb.ToString().TrimEnd();
    }

    private static ChatMessage CreateUserMessage(string content) => new(ChatRole.User, content);

    private static ChatMessage CreateToolMessage(string content) => new(ChatRole.Tool, content);

    private static string FormatRepositoryTree(IReadOnlyList<RepositoryEntry> entries, string? scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SCOPE: {scope ?? "."}");
        sb.AppendLine($"ENTRY COUNT: {entries.Count}");

        foreach (var entry in entries.Take(MaxListedFiles))
        {
            sb.AppendLine($"- {(entry.IsDirectory ? "[dir]" : "[file]")} {entry.RelativePath}");
        }

        if (entries.Count > MaxListedFiles)
        {
            sb.AppendLine($"... truncated to first {MaxListedFiles} entries ...");
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

    internal enum AgentPhaseResponseMode
    {
        ToolCallsOrMarkdown,
        ToolCallsOnly,
        MarkdownOnly
    }

    internal sealed record AgentPhaseDefinition(
        string Name,
        string Prompt,
        bool RequiresSavedOutput,
        bool AllowRepositoryDiscovery,
        string PurposeSummary,
        AgentPhaseMode Mode = AgentPhaseMode.Interactive,
        AgentPhaseResponseMode ResponseMode = AgentPhaseResponseMode.ToolCallsOrMarkdown,
        bool RequireWorkflowInput = false,
        bool RequireCompletionValidation = false,
        bool AllowToolCalls = true,
        IReadOnlyList<string>? AllowedToolActions = null,
        string? SavedMarkdownArtifactFileName = null,
        bool InjectSavedMarkdownIntoNextPhase = false,
        bool RequireAllAvailableFilesRead = false,
        bool AutoCompleteWhenAllAvailableFilesRead = false,
        bool AutoLoadAllAvailableFilesAfterFindAvailableFiles = false,
        bool SummarizePromptInLogs = false);

    internal sealed record RepositoryDiscoveryTools(
        Func<string?, CancellationToken, Task<IReadOnlyList<RepositoryEntry>>> ListRepositoryTreeAsync,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<FileSearchHit>>> SearchRepositoryAsync);

    private sealed class AgentExecutionState
    {
        public bool WorkflowInputLoaded { get; set; }
        public bool RepositoryDiscoveryUsed { get; set; }
        public bool FilesAlreadyListed { get; set; }
        public string AvailableFilesPayload { get; set; } = string.Empty;
        public HashSet<string> ReadPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private enum ToolResponseState
    {
        NoToolObject,
        SingleToolObject,
        MultipleToolObjects,
        MixedToolAndText
    }

    private sealed record ToolResponseAnalysis(ToolResponseState State, ToolRequest? SingleToolRequest);

    private enum NativeToolResponseState
    {
        NoToolCall,
        SingleToolCall,
        MultipleToolCalls,
        MixedToolAndText
    }

    private sealed record NativeToolResponseAnalysis(NativeToolResponseState State, AgentToolCall? SingleToolCall);

    private sealed record AutoLoadRepositoryEvidenceResult(IReadOnlyList<string> ToolPayloads, int BatchCount, int FilesReviewed);

    private sealed record FileReadResult(string FullPath, string NormalizedPath, string Content);

    private sealed record FileBatchResult(
        IReadOnlyList<FileReadResult> Files,
        string Payload,
        int NextUnreadFileIndex,
        bool HasMore,
        int BatchNumber,
        int TotalBatchesEstimate);

    private sealed record AvailableFileIndex(
        IReadOnlyList<string> FullPaths,
        IReadOnlyDictionary<string, string> NormalizedFullPaths);

    private sealed record BatchEstimateResult(int NextIndex, int FileCount);


    private static string BuildWriteFilePathErrorMessage(IReadOnlyDictionary<string, string>? writableFiles)
    {
        var approvedPaths = writableFiles is null || writableFiles.Count == 0
            ? "<none>"
            : string.Join(Environment.NewLine + "- ", writableFiles.Keys);

        return "ERROR: write_file requires a non-empty 'path'. Return exactly one tool call using this shape: " +
               "{\"tool\":\"write_file\",\"args\":{\"path\":\"<approved full physical path>\",\"content\":\"<markdown content>\"}}. " +
               "Use one of these approved full physical paths exactly as listed:" + Environment.NewLine + "- " + approvedPaths;
    }

    private static string BuildWriteFileContentErrorMessage()
    {
        return "ERROR: write_file requires non-empty 'content'. Return exactly one tool call using this shape: " +
               "{\"tool\":\"write_file\",\"args\":{\"path\":\"<approved full physical path>\",\"content\":\"<markdown content>\"}}.";
    }

    private static string BuildWriteFileNotApprovedErrorMessage(IReadOnlyDictionary<string, string>? writableFiles)
    {
        var approvedPaths = writableFiles is null || writableFiles.Count == 0
            ? "<none>"
            : string.Join(Environment.NewLine + "- ", writableFiles.Keys);

        return "ERROR: path not approved for write_file. Return exactly one tool call using an approved full physical path exactly as listed in the current step. Approved paths:" + Environment.NewLine + "- " + approvedPaths;
    }

    private sealed class ToolRequest
    {
        public string? Action { get; set; }
        public string? Tool { get; set; }
        public string? WorkflowRunId { get; set; }
        public string? Path { get; set; }
        public string? Query { get; set; }
        public string? FilePath { get; set; }
        public string? Content { get; set; }
        public JsonElement? Output { get; set; }
        public JsonElement? Args { get; set; }

        public string ResolvedAction => !string.IsNullOrWhiteSpace(Action)
            ? Action!
            : Tool ?? string.Empty;

        public string? ResolvedPath => FirstNonEmpty(Path, FilePath, TryGetStringArg("path"), TryGetStringArg("filePath"));

        public string? ResolvedQuery => FirstNonEmpty(Query, TryGetStringArg("query"));

        public string? ResolvedContent => FirstNonEmpty(Content, TryGetStringArg("content"));

        public string? ResolvedWorkflowRunId => FirstNonEmpty(WorkflowRunId, TryGetStringArg("workflowRunId"));

        public JsonElement? ResolvedOutput => Output ?? TryGetArg("output");

        private string? TryGetStringArg(string propertyName)
        {
            var value = TryGetArg(propertyName);
            return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
        }

        private JsonElement? TryGetArg(string propertyName)
        {
            if (Args is not { ValueKind: JsonValueKind.Object } args)
            {
                return null;
            }

            return args.TryGetProperty(propertyName, out var value)
                ? value
                : null;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
