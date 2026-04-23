using System.Text;
using System.Text.Json;
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
        await logs.AppendBlockAsync(workflowRunId, $"Prompt: {logTitle}", prompt, ct);
        await logs.AppendLineAsync(
            workflowRunId,
            $"Provider '{conversationFactory.ProviderName}' selected model '{conversationFactory.SelectedModel}' using per-response timeout of {responseTimeoutSeconds} seconds. OpenAI config complete: {conversationFactory.IsOpenAiConfigurationComplete}.",
            ct);

        var rawText = await RunModelWithTimeoutAsync(
            conversation,
            [CreateUserMessage(prompt)],
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
        pendingMessages.Clear();
        var nextUnreadFileIndex = 0;
        var writtenPathsThisPhase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < MaxToolCallsPerPhase; i++)
        {
            var rawText = await RunModelWithTimeoutAsync(conversation, currentMessages, maxModelResponseSeconds, ct);
            await logs.AppendBlockAsync(workflowRunId, $"{phase.Name} - agent response #{i + 1}", rawText, ct);

            var toolResponseState = AnalyzeToolResponse(rawText);
            if (toolResponseState.State == ToolResponseState.MixedToolAndText)
            {
                await logs.AppendLineAsync(
                    workflowRunId,
                    $"Result ({phase.Name}): error. Mixed tool call and final Markdown/text in the same response are not allowed.",
                    ct);
                currentMessages =
                [
                    CreateUserMessage("INVALID RESPONSE. Return either exactly one JSON tool-call object OR the final Markdown output, never both in the same response.")
                ];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.MultipleToolObjects)
            {
                await logs.AppendLineAsync(
                    workflowRunId,
                    $"Result ({phase.Name}): error. Multiple tool-call objects in one response are not allowed.",
                    ct);
                currentMessages =
                [
                    CreateUserMessage("INVALID RESPONSE. Return exactly one JSON tool-call object per response.")
                ];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.SingleToolObject && phase.ResponseMode == AgentPhaseResponseMode.MarkdownOnly)
            {
                await logs.AppendLineAsync(
                    workflowRunId,
                    $"Tool call blocked ({phase.Name}): this phase requires a pure Markdown response with no tool calls.",
                    ct);
                currentMessages =
                [
                    CreateUserMessage("TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only the final Markdown output. Do not return JSON. Do not call any tools.")
                ];
                continue;
            }

            if (toolResponseState.State == ToolResponseState.NoToolObject)
            {
                if (phase.ResponseMode == AgentPhaseResponseMode.ToolCallsOnly)
                {
                    await logs.AppendLineAsync(
                        workflowRunId,
                        $"Result ({phase.Name}): error. This phase requires exactly one JSON tool-call object per response.",
                        ct);
                    currentMessages =
                    [
                        CreateUserMessage("INVALID RESPONSE. This phase is tool-call only. Return exactly one JSON tool-call object and no Markdown or prose.")
                    ];
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
                        await logs.AppendLineAsync(
                            workflowRunId,
                            $"Result ({phase.Name}): error. Final Markdown declared writes that were not executed in this phase: {string.Join(", ", missingWritePaths)}.",
                            ct);
                        currentMessages =
                        [
                            CreateUserMessage($"INVALID FINAL RESPONSE. Execute write_file first for these declared paths before returning final Markdown: {string.Join(", ", missingWritePaths)}.")
                        ];
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
                    await logs.AppendLineAsync(
                        workflowRunId,
                        $"Result ({phase.Name}): Markdown response saved to artifact '{phase.SavedMarkdownArtifactFileName}'.",
                        ct);

                    if (phase.InjectSavedMarkdownIntoNextPhase)
                    {
                        pendingMessages.Add(CreateUserMessage(BuildSavedMarkdownContextMessage(phase.SavedMarkdownArtifactFileName, rawText)));
                    }
                }

                if (phase.RequiresSavedOutput)
                {
                    await logs.AppendLineAsync(
                        workflowRunId,
                        $"Result ({phase.Name}): final Markdown output accepted as the workflow output for this phase.",
                        ct);
                }

                return rawText;
            }

            var toolRequest = toolResponseState.SingleToolRequest;
            var normalizedAction = toolRequest!.ResolvedAction.Trim().ToLowerInvariant();

            if (phase.AllowToolCalls && phase.AllowedToolActions is { Count: > 0 }
                && !phase.AllowedToolActions.Contains(normalizedAction, StringComparer.OrdinalIgnoreCase))
            {
                await logs.AppendLineAsync(
                    workflowRunId,
                    $"Result ({phase.Name}): error. Tool action '{toolRequest.ResolvedAction}' is not allowed in this phase.",
                    ct);
                currentMessages =
                [
                    CreateToolMessage($"ERROR: action '{toolRequest.ResolvedAction}' is not allowed in this phase. Use only: {string.Join(", ", phase.AllowedToolActions)}.")
                ];
                continue;
            }

            if (!phase.AllowToolCalls)
            {
                await logs.AppendLineAsync(
                    workflowRunId,
                    $"Tool call blocked ({phase.Name}): '{toolRequest.ResolvedAction}'. This phase requires a pure Markdown response with no tool calls.",
                    ct);

                currentMessages =
                [
                    CreateUserMessage("TOOLS ARE NOT ALLOWED IN THIS PHASE. Return only a short plain Markdown response. Do not return JSON. Do not call any tools.")
                ];
                continue;
            }

            switch (normalizedAction)
            {
                case "get_workflow_input":
                {
                    await LogIgnoredWorkflowRunIdAsync(toolRequest.ResolvedWorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
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

                    if (state.FilesAlreadyListed)
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): skipped repeated find_available_files call. Returning cached file list.", ct);
                        currentMessages = [CreateToolMessage(state.AvailableFilesPayload)];
                        continue;
                    }

                    var matchingPaths = FindAvailableFiles(availableFileIndex);
                    var fileListResult = FormatAvailableFiles(matchingPaths);
                    state.FilesAlreadyListed = true;
                    state.AvailableFilesPayload = fileListResult;
                    state.RepositoryDiscoveryUsed = true;
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): {matchingPaths.Count} file path(s) returned.", ct);

                    if (phase.AutoLoadAllAvailableFilesAfterFindAvailableFiles)
                    {
                        pendingMessages.Add(CreateToolMessage(fileListResult));
                        var autoLoadResult = AutoLoadRepositoryEvidence(availableFileIndex, state);
                        foreach (var payload in autoLoadResult.ToolPayloads)
                        {
                            pendingMessages.Add(CreateToolMessage(payload));
                        }

                        await logs.AppendLineAsync(
                            workflowRunId,
                            $"Result ({phase.Name}): repository evidence acquisition loaded automatically. Batches prepared: {autoLoadResult.BatchCount}. Files reviewed: {autoLoadResult.FilesReviewed}.",
                            ct);
                        return "Repository evidence acquisition completed.";
                    }

                    currentMessages = BuildPostToolMessages(phase, fileListResult, state.ReadPaths, availableFileIndex);
                    continue;
                }
                case "get_next_file_batch":
                {
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): get_next_file_batch().", ct);

                    var batch = BuildNextFileBatch(availableFileIndex, nextUnreadFileIndex);
                    nextUnreadFileIndex = batch.NextUnreadFileIndex;

                    foreach (var file in batch.Files)
                    {
                        state.ReadPaths.Add(file.NormalizedPath);
                    }

                    await logs.AppendLineAsync(
                        workflowRunId,
                        $"Result ({phase.Name}): batch {batch.BatchNumber} of {batch.TotalBatches}. Files returned: {batch.Files.Count}. Has more: {(batch.HasMore ? "yes" : "no")}.",
                        ct);

                    foreach (var logLine in DescribeBatchFilesForLog(batch.Files))
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): {logLine}", ct);
                    }

                    currentMessages = BuildPostToolMessages(phase, batch.Payload, state.ReadPaths, availableFileIndex);

                    if (phase.AutoCompleteWhenAllAvailableFilesRead &&
                        phase.RequireAllAvailableFilesRead &&
                        availableFileIndex.FullPaths.Count > 0 &&
                        availableFileIndex.NormalizedFullPaths.Keys.All(path => state.ReadPaths.Contains(path)))
                    {
                        await logs.AppendLineAsync(
                            workflowRunId,
                            $"Result ({phase.Name}): repository evidence acquisition completed automatically after all allowed files were reviewed.",
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
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. get_file cannot be used before find_available_files.", ct);
                        currentMessages =
                        [
                            CreateToolMessage("ERROR: call find_available_files first.")
                        ];
                        continue;
                    }

                    var requestedPath = toolRequest.ResolvedPath;
                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. get_file requires a non-empty 'path'.", ct);
                        currentMessages =
                        [
                            CreateToolMessage("ERROR: get_file requires a non-empty 'path' with an exact full physical path previously returned by find_available_files.")
                        ];
                        continue;
                    }

                    requestedPath = requestedPath.Trim();
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): get_file('{requestedPath}').", ct);

                    var fileRead = TryReadFileByPhysicalPath(availableFileIndex, requestedPath);
                    if (fileRead is null)
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. File path is not allowed for this run or the file does not exist.", ct);
                        currentMessages =
                        [
                            CreateToolMessage("ERROR: path not allowed. Use only an exact full physical path returned by find_available_files for this run.")
                        ];
                        continue;
                    }

                    state.ReadPaths.Add(fileRead.NormalizedPath);
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): success. Characters read: {fileRead.Content.Length}. Complete file loaded with no truncation.", ct);

                    currentMessages = BuildPostToolMessages(
                        phase,
                        BuildFullFileBatchBlock(fileRead.FullPath, fileRead.NormalizedPath, fileRead.Content),
                        state.ReadPaths,
                        availableFileIndex);
                    continue;
                }
                case "write_file":
                {
                    var requestedPath = toolRequest.ResolvedPath;
                    var content = toolRequest.ResolvedContent;

                    await logs.AppendLineAsync(
                        workflowRunId,
                        $"Parsed tool call ({phase.Name}): action='write_file', path='{requestedPath ?? "<null>"}', contentLength={(content?.Length ?? 0)}.",
                        ct);

                    if (string.IsNullOrWhiteSpace(requestedPath))
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. write_file requires a non-empty 'path'.", ct);
                        currentMessages = [CreateToolMessage(BuildWriteFilePathErrorMessage(writableFiles))];
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. write_file requires non-empty 'content'.", ct);
                        currentMessages = [CreateToolMessage(BuildWriteFileContentErrorMessage())];
                        continue;
                    }

                    requestedPath = requestedPath.Trim();
                    await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): write_file('{requestedPath}').", ct);

                    if (writableFiles is null || !writableFiles.TryGetValue(requestedPath, out var fullWritePath))
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. File path is not approved for writes in this workflow.", ct);
                        currentMessages = [CreateToolMessage(BuildWriteFileNotApprovedErrorMessage(writableFiles))];
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
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): success. Wrote {normalizedContent.Length} character(s) to '{requestedPath}'.", ct);
                    currentMessages = [CreateToolMessage($"OK: wrote '{requestedPath}'.")];
                    continue;
                }
                case "save_workflow_output":
                {
                    if (toolRequest.ResolvedOutput is not JsonElement output)
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. save_workflow_output requires an 'output' object.", ct);
                        currentMessages =
                        [
                            CreateUserMessage("save_workflow_output requires an 'output' JSON object. Retry with a valid output payload.")
                        ];
                        continue;
                    }

                    try
                    {
                        await LogIgnoredWorkflowRunIdAsync(toolRequest.ResolvedWorkflowRunId, workflowRunId, phase.Name, normalizedAction, logs, ct);
                        ValidateWorkflowOutputPayload(output);
                        var outputJson = output.GetRawText();
                        await logs.AppendLineAsync(workflowRunId, $"Tool call ({phase.Name}): save_workflow_output('{workflowRunId}').", ct);
                        await logs.AppendBlockAsync(workflowRunId, $"Workflow output payload ({phase.Name})", outputJson, ct);
                        await payloadStore.SaveOutputAsync(workflowRunId, outputJson, ct);
                        return outputJson;
                    }
                    catch (Exception ex)
                    {
                        await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error while saving workflow output. {ex.Message}", ct);
                        currentMessages =
                        [
                            CreateUserMessage($"save_workflow_output failed validation: {ex.Message} Retry with a valid output object.")
                        ];
                        continue;
                    }
                }
                default:
                    await logs.AppendLineAsync(workflowRunId, $"Result ({phase.Name}): error. Unsupported tool action '{toolRequest.ResolvedAction}'.", ct);
                    currentMessages =
                    [
                        CreateToolMessage($"ERROR: unsupported tool action '{toolRequest.ResolvedAction}'. Allowed actions for this phase must be followed exactly.")
                    ];
                    continue;
            }
        }

        throw new InvalidOperationException($"Agent exceeded maximum of {MaxToolCallsPerPhase} tool interactions in phase '{phase.Name}'.");
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

    private static IReadOnlyList<ChatMessage> BuildPostToolMessages(
        AgentPhaseDefinition phase,
        string toolPayload,
        IReadOnlyCollection<string> readPaths,
        AvailableFileIndex availableFileIndex)
    {
        var messages = new List<ChatMessage>
        {
            CreateToolMessage(toolPayload)
        };

        if (phase.RequireAllAvailableFilesRead)
        {
            var reviewedCount = availableFileIndex.NormalizedFullPaths.Keys.Count(path => readPaths.Contains(path));
            var remainingCount = Math.Max(0, availableFileIndex.FullPaths.Count - reviewedCount);
            messages.Add(CreateUserMessage($"Repository review progress: {reviewedCount}/{availableFileIndex.FullPaths.Count} files reviewed. Remaining: {remainingCount}."));
        }

        return messages;
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

        var totalBatches = EstimateTotalBatchCount(availableFileIndex);
        var batchNumber = CalculateBatchNumber(availableFileIndex, nextUnreadFileIndex);

        return new FileBatchResult(
            files,
            payload.Length == 0 ? "END OF FILE BATCHES" : payload.ToString(),
            currentIndex,
            currentIndex < availableFileIndex.FullPaths.Count,
            batchNumber,
            totalBatches);
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
        var sb = new StringBuilder();
        sb.AppendLine($"PHASE {phaseIndex + 1} OF {totalPhases}: {phase.Name}");
        sb.AppendLine();
        sb.AppendLine(phase.PurposeSummary.Trim());
        sb.AppendLine();
        sb.AppendLine(phase.Prompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("PHASE RULES:");
        if (phase.AllowToolCalls)
        {
            if (phase.ResponseMode == AgentPhaseResponseMode.ToolCallsOnly)
            {
                sb.AppendLine("- Return exactly one JSON tool call object per response.");
                sb.AppendLine("- Do not return any Markdown, prose, summaries, or final phase result in this phase.");
            }
            else
            {
                sb.AppendLine("- Return either one JSON tool call object or the final plain-text phase result.");
            }
            if (phase.AllowedToolActions is { Count: > 0 })
            {
                sb.AppendLine($"- Allowed tool actions in this phase: {string.Join(", ", phase.AllowedToolActions)}.");
            }
            else
            {
                sb.AppendLine("- Use only the tool actions required by the phase prompt.");
            }

            if (phase.AllowedToolActions?.Contains("find_available_files", StringComparer.OrdinalIgnoreCase) == true)
            {
                sb.AppendLine("- `find_available_files` takes no parameters and returns only file paths, one per line.");
            }

            if (phase.AllowedToolActions?.Contains("get_next_file_batch", StringComparer.OrdinalIgnoreCase) == true)
            {
                sb.AppendLine("- `get_next_file_batch` takes no parameters. Return exactly one JSON object in this shape: {\"tool\":\"get_next_file_batch\",\"args\":{}}.");
                sb.AppendLine("- The tool returns JSON with: batchIndex (current 1-based batch), totalBatches (fixed total batch count for the full run), hasMore (whether another batch exists), and files[].");
                sb.AppendLine("- Each files[] entry contains path (full relative repository path) and content (full raw file contents). Files are never truncated or split across batches.");
                sb.AppendLine("- After each tool result, if hasMore is true, return one more single get_next_file_batch tool call in the next response. If hasMore is false, stop.");
                sb.AppendLine("- Never return more than one tool call in the same response.");
            }

            if (phase.AllowedToolActions?.Contains("get_file", StringComparer.OrdinalIgnoreCase) == true)
            {
                sb.AppendLine("- `get_file` requires an exact full physical path returned earlier by `find_available_files`.");
            }

            if (phase.AllowedToolActions?.Contains("write_file", StringComparer.OrdinalIgnoreCase) == true
                && writableFiles is not null && writableFiles.Count > 0)
            {
                sb.AppendLine("- `write_file` may be used only with one approved stable documentation path exactly as listed in the prompt.");
            }
        }
        else
        {
            sb.AppendLine("- Return only a concise plain Markdown phase summary.");
            sb.AppendLine("- Tool calls are forbidden in this phase.");
            sb.AppendLine("- Do not return JSON.");
        }

        sb.AppendLine("- Do not include markdown fences.");
        sb.AppendLine("- Keep phase summaries concrete and evidence-based.");

        if (phase.RequiresSavedOutput)
        {
            sb.AppendLine("- This is the final phase: finish with the final plain-text result for the workflow.");
            sb.AppendLine("- You may use allowed tools earlier in this phase if needed before the final response.");
        }
        else if (phase.RequireCompletionValidation)
        {
            sb.AppendLine("- Before you finish this phase, gather enough direct evidence to support your final plain-text response.");
        }

        if (!string.IsNullOrWhiteSpace(phase.SavedMarkdownArtifactFileName))
        {
            sb.AppendLine($"- When you finish this phase, return only the Markdown artifact content. The system will save it as '{phase.SavedMarkdownArtifactFileName}'.");
        }

        if (phase.RequireAllAvailableFilesRead)
        {
            sb.AppendLine("- This phase must review the complete allowed repository context set before it can finish.");
        }

        return sb.ToString();
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
        int TotalBatches);

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
