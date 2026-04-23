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
        await logs.AppendBlockAsync(workflowRunId, $"Model base instructions: {logTitle}", instructions, ct);
        await logs.AppendLineAsync(
            workflowRunId,
            $"Provider '{conversationFactory.ProviderName}' selected model '{conversationFactory.SelectedModel}' using per-response timeout of {responseTimeoutSeconds} seconds. OpenAI config complete: {conversationFactory.IsOpenAiConfigurationComplete}.",
            ct);

        var promptMessages = new[] { CreateUserMessage(prompt) };
        await LogModelInputMessagesAsync(logs, workflowRunId, logTitle, 1, promptMessages, ct);

        var rawText = await RunModelWithTimeoutAsync(
            conversation,
            promptMessages,
            responseTimeoutSeconds,
            ct);

        await logs.AppendBlockAsync(workflowRunId, $"Response: {logTitle}", rawText, ct);
        return rawText;
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
        await logs.AppendBlockAsync(workflowRunId, "Model message OUT [base] role=system source=base-instructions format=text/plain", instructions, ct);

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

        for (var i = 0; i < MaxToolCallsPerPhase; i++)
        {
            var interaction = BeginInteractionLog(phase.Name, i + 1, currentMessages);
            var rawText = await RunModelWithTimeoutAsync(conversation, currentMessages, maxModelResponseSeconds, ct);
            var toolResponseState = AnalyzeToolResponse(rawText);
            AppendInteractionSection(interaction, "OUTPUT [assistant]", SanitizeModelMessageForLog(rawText));

            if (toolResponseState.State == ToolResponseState.MixedToolAndText)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: mixed tool call and final Markdown/text in the same assistant message.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.MultipleToolObjects)
            {
                var retryMessage = BuildInvalidResponseRetryMessage(phase);
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: multiple tool-call JSON objects in one assistant message.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.SingleToolObject && phase.ResponseMode == AgentPhaseResponseMode.MarkdownOnly)
            {
                const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only the final Markdown output. Do not return JSON. Do not call any tools.";
                AppendInteractionSection(interaction, "VALIDATION", "Rejected: tool call returned in a Markdown-only phase.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.NoToolObject)
            {
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

            var toolRequest = toolResponseState.SingleToolRequest;
            var normalizedAction = toolRequest!.ResolvedAction.Trim().ToLowerInvariant();
            AppendInteractionSection(interaction, "TOOL REQUEST", BuildToolRequestLog(rawText, toolRequest));

            if (phase.AllowToolCalls && phase.AllowedToolActions is { Count: > 0 }
                && !phase.AllowedToolActions.Contains(normalizedAction, StringComparer.OrdinalIgnoreCase))
            {
                var disallowedActionPayload = $"ERROR: action '{toolRequest.ResolvedAction}' is not allowed in this phase. Use only: {string.Join(", ", phase.AllowedToolActions)}.";
                AppendInteractionSection(interaction, "VALIDATION", $"Rejected: tool action '{toolRequest.ResolvedAction}' is not allowed in this phase.");
                AppendInteractionSection(interaction, "NEXT INPUT [tool]", disallowedActionPayload);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateToolMessage(disallowedActionPayload)];
                continue;
            }

            if (!phase.AllowToolCalls)
            {
                const string retryMessage = "TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only a short plain Markdown response. Do not return JSON. Do not call any tools.";
                AppendInteractionSection(interaction, "VALIDATION", $"Rejected: tool call '{toolRequest.ResolvedAction}' returned in a no-tool phase.");
                AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                currentMessages = [CreateUserMessage(retryMessage)];
                continue;
            }

            switch (normalizedAction)
            {
                case "get_workflow_input":
                {
                    await LogIgnoredWorkflowRunIdAsync(toolRequest.ResolvedWorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
                    var inputPayload = await payloadStore.GetInputAsync(workflowRunId, ct);
                    state.WorkflowInputLoaded = true;
                    var toolPayload = $"WORKFLOW INPUT FOR {workflowRunId}\n{inputPayload.InputPayloadJson}";
                    AppendInteractionSection(interaction, "TOOL RESULT", $"get_workflow_input -> {inputPayload.InputPayloadJson.Length} chars.");
                    AppendInteractionSection(interaction, "NEXT INPUT [tool]", SanitizeModelMessageForLog(toolPayload));
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateToolMessage(toolPayload)];
                    continue;
                }
                case "find_available_files":
                {
                    if (state.FilesAlreadyListed)
                    {
                        AppendInteractionSection(interaction, "TOOL RESULT", "find_available_files -> cached file list returned.");
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(state.AvailableFilesPayload)];
                        continue;
                    }

                    var matchingPaths = FindAvailableFiles(availableFileIndex);
                    var fileListResult = FormatAvailableFiles(matchingPaths);
                    state.FilesAlreadyListed = true;
                    state.AvailableFilesPayload = fileListResult;
                    state.RepositoryDiscoveryUsed = true;
                    AppendInteractionSection(interaction, "TOOL RESULT", $"find_available_files -> {matchingPaths.Count} file path(s) returned.");

                    if (phase.AutoLoadAllAvailableFilesAfterFindAvailableFiles)
                    {
                        pendingMessages.Add(CreateToolMessage(fileListResult));
                        var autoLoadResult = AutoLoadRepositoryEvidence(availableFileIndex, state);
                        foreach (var payload in autoLoadResult.ToolPayloads)
                        {
                            pendingMessages.Add(CreateToolMessage(payload));
                        }

                        AppendInteractionSection(interaction, "RESULT", $"Repository evidence loaded automatically. Batches prepared: {autoLoadResult.BatchCount}; files reviewed: {autoLoadResult.FilesReviewed}.");
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        return "Repository evidence acquisition completed.";
                    }

                    AppendInteractionSection(interaction, "NEXT INPUT [tool]", SummarizeFileListForLog(fileListResult));
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = BuildPostToolMessages(fileListResult);
                    continue;
                }
                case "get_next_file_batch":
                {
                    var batch = BuildNextFileBatch(availableFileIndex, nextUnreadFileIndex);
                    nextUnreadFileIndex = batch.NextUnreadFileIndex;

                    foreach (var file in batch.Files)
                    {
                        state.ReadPaths.Add(file.NormalizedPath);
                    }

                    AppendInteractionSection(interaction, "TOOL RESULT", BuildBatchResultLog(batch));
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = BuildPostToolMessages(batch.Payload);

                    if (phase.AutoCompleteWhenAllAvailableFilesRead &&
                        phase.RequireAllAvailableFilesRead &&
                        availableFileIndex.FullPaths.Count > 0 &&
                        availableFileIndex.NormalizedFullPaths.Keys.All(path => state.ReadPaths.Contains(path)))
                    {
                        await logs.AppendLineAsync(
                            workflowRunId,
                            $"{phase.Name}: repository evidence acquisition completed after all allowed files were reviewed.",
                            ct);
                        return "Repository evidence acquisition completed.";
                    }

                    continue;
                }
                case "read_file":
                case "get_file":
                {
                    if (!state.FilesAlreadyListed)
                    {
                        const string toolPayload = "ERROR: call find_available_files first.";
                        AppendInteractionSection(interaction, "TOOL RESULT", "get_file rejected: find_available_files has not been called yet.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", toolPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(toolPayload)];
                        continue;
                    }

                    var requestedPath = toolRequest.ResolvedPath;
                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        var toolPayload = "ERROR: get_file requires a non-empty 'path' with an exact full physical path previously returned by find_available_files.";
                        AppendInteractionSection(interaction, "TOOL RESULT", "get_file rejected: missing path.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", toolPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(toolPayload)];
                        continue;
                    }

                    requestedPath = requestedPath.Trim();
                    var fileRead = TryReadFileByPhysicalPath(availableFileIndex, requestedPath);
                    if (fileRead is null)
                    {
                        var toolPayload = "ERROR: path not allowed. Use only an exact full physical path returned by find_available_files for this run.";
                        AppendInteractionSection(interaction, "TOOL RESULT", $"get_file rejected: path is not allowed or does not exist: {requestedPath}.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", toolPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(toolPayload)];
                        continue;
                    }

                    state.ReadPaths.Add(fileRead.NormalizedPath);
                    var toolPayloadForFile = BuildFullFileBatchBlock(fileRead.FullPath, fileRead.NormalizedPath, fileRead.Content);
                    AppendInteractionSection(interaction, "TOOL RESULT", $"get_file -> {fileRead.FullPath} ({fileRead.Content.Length} chars). Complete file loaded.");
                    AppendInteractionSection(interaction, "NEXT INPUT [tool]", BuildFilePayloadMetadataLog(toolPayloadForFile));
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = BuildPostToolMessages(toolPayloadForFile);
                    continue;
                }
                case "write_file":
                {
                    var requestedPath = toolRequest.ResolvedPath;
                    var content = toolRequest.ResolvedContent;

                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        var writePathErrorPayload = BuildWriteFilePathErrorMessage(writableFiles);
                        AppendInteractionSection(interaction, "TOOL RESULT", "write_file rejected: missing path.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", writePathErrorPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(writePathErrorPayload)];
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        var writeContentErrorPayload = BuildWriteFileContentErrorMessage();
                        AppendInteractionSection(interaction, "TOOL RESULT", "write_file rejected: empty content.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", writeContentErrorPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(writeContentErrorPayload)];
                        continue;
                    }

                    requestedPath = requestedPath.Trim();
                    if (writableFiles is null || !writableFiles.TryGetValue(requestedPath, out var fullWritePath))
                    {
                        var writeNotApprovedPayload = BuildWriteFileNotApprovedErrorMessage(writableFiles);
                        AppendInteractionSection(interaction, "TOOL RESULT", $"write_file rejected: path is not approved: {requestedPath}.");
                        AppendInteractionSection(interaction, "NEXT INPUT [tool]", writeNotApprovedPayload);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateToolMessage(writeNotApprovedPayload)];
                        continue;
                    }

                    var folder = Path.GetDirectoryName(fullWritePath);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    var normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + Environment.NewLine;
                    await File.WriteAllTextAsync(fullWritePath, normalizedContent, ct);
                    writtenPathsThisPhase.Add(requestedPath);
                    var writeSuccessPayload = $"OK: wrote '{requestedPath}'.";
                    AppendInteractionSection(interaction, "TOOL RESULT", $"write_file -> wrote {normalizedContent.Length} chars to '{requestedPath}'.");
                    AppendInteractionSection(interaction, "NEXT INPUT [tool]", writeSuccessPayload);
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateToolMessage(writeSuccessPayload)];
                    continue;
                }
                case "save_workflow_output":
                {
                    if (toolRequest.ResolvedOutput is not JsonElement output)
                    {
                        var retryMessage = "save_workflow_output requires an 'output' JSON object. Retry with a valid output payload.";
                        AppendInteractionSection(interaction, "TOOL RESULT", "save_workflow_output rejected: missing output object.");
                        AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateUserMessage(retryMessage)];
                        continue;
                    }

                    try
                    {
                        await LogIgnoredWorkflowRunIdAsync(toolRequest.ResolvedWorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
                        ValidateWorkflowOutputPayload(output);
                        var outputJson = output.GetRawText();
                        await payloadStore.SaveOutputAsync(workflowRunId, outputJson, ct);
                        AppendInteractionSection(interaction, "TOOL RESULT", $"save_workflow_output -> saved output for workflow run {workflowRunId}.");
                        AppendInteractionSection(interaction, "WORKFLOW OUTPUT", outputJson);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        return outputJson;
                    }
                    catch (Exception ex)
                    {
                        var retryMessage = $"save_workflow_output failed validation: {ex.Message} Retry with a valid output object.";
                        AppendInteractionSection(interaction, "TOOL RESULT", $"save_workflow_output failed validation: {ex.Message}");
                        AppendInteractionSection(interaction, "NEXT INPUT [user]", retryMessage);
                        await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                        currentMessages = [CreateUserMessage(retryMessage)];
                        continue;
                    }
                }
                default:
                {
                    var toolPayload = $"ERROR: unsupported tool action '{toolRequest.ResolvedAction}'. Allowed actions for this phase must be followed exactly.";
                    AppendInteractionSection(interaction, "TOOL RESULT", $"Unsupported tool action '{toolRequest.ResolvedAction}'.");
                    AppendInteractionSection(interaction, "NEXT INPUT [tool]", toolPayload);
                    await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} interaction {i + 1}", interaction.ToString(), ct);
                    currentMessages = [CreateToolMessage(toolPayload)];
                    continue;
                }
            }
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

    private static StringBuilder BeginInteractionLog(string phaseName, int interactionNumber, IReadOnlyList<ChatMessage> inputMessages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{phaseName} - interaction {interactionNumber}");
        AppendInteractionSection(sb, "INPUT", BuildInputMessagesLog(inputMessages));
        return sb;
    }

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

    private static string BuildInputMessagesLog(IReadOnlyList<ChatMessage> messages)
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
            var summary = SummarizeInputMessageForLog(content);
            sb.AppendLine($"Message {index + 1}/{messages.Count} [{message.Role}; {source}; {content.Length} chars]");
            sb.AppendLine(summary);
            if (index < messages.Count - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string SummarizeInputMessageForLog(string content)
    {
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
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}")) return "application/json";
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return "text/markdown";
        return "text/plain";
    }

    private static string SanitizeModelMessageForLog(string content)
    {
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

    private static async Task<string> RunModelWithTimeoutAsync(
        IAgentConversation conversation,
        IReadOnlyList<ChatMessage> messages,
        int maxModelResponseSeconds,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(maxModelResponseSeconds));

        try
        {
            return await conversation.RunAsync(messages, timeoutCts.Token);
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

            if (batch.NextIndex >= startIndex)
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
        bool AutoLoadAllAvailableFilesAfterFindAvailableFiles = false);

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
