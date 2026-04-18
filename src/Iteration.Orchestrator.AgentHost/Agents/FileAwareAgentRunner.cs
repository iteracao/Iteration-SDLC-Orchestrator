using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    private const int MaxToolCalls = 16;
    private const int MaxFileCharacters = 20000;

    public static async Task<string> RunAsync(
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
        CancellationToken ct)
    {
        var chatClient = new OllamaChatClient(new Uri(endpoint), modelId: model);
        AIAgent agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);
        var allowedPathSet = allowedPaths
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var transcript = new StringBuilder();
        var currentPrompt = initialPrompt;

        for (var i = 0; i < MaxToolCalls; i++)
        {
            var rawResponse = await agent.RunAsync(currentPrompt, cancellationToken: ct);
            var rawText = rawResponse.Text ?? string.Empty;
            await logs.AppendBlockAsync(workflowRunId, $"Agent response #{i + 1}", rawText, ct);

            if (!TryParseToolRequest(rawText, out var toolRequest))
            {
                return rawText;
            }

            var normalizedAction = toolRequest!.Action.Trim().ToLowerInvariant();
            switch (normalizedAction)
            {
                case "get_workflow_input":
                {
                    var requestedWorkflowRunId = ParseWorkflowRunId(toolRequest.WorkflowRunId, workflowRunId, "get_workflow_input");
                    var inputPayload = await payloadStore.GetInputAsync(requestedWorkflowRunId, ct);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call: get_workflow_input('{requestedWorkflowRunId}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, "Workflow input payload", inputPayload.InputPayloadJson, ct);

                    transcript.AppendLine($"AGENT TOOL REQUEST #{i + 1}:");
                    transcript.AppendLine(rawText.Trim());
                    transcript.AppendLine();
                    transcript.AppendLine($"TOOL RESULT FOR get_workflow_input('{requestedWorkflowRunId}'):");
                    transcript.AppendLine("--- WORKFLOW INPUT START ---");
                    transcript.AppendLine(inputPayload.InputPayloadJson);
                    transcript.AppendLine("--- WORKFLOW INPUT END ---");
                    transcript.AppendLine();
                    currentPrompt = BuildFollowUpPrompt(initialPrompt, transcript.ToString());
                    continue;
                }
                case "read_file":
                {
                    if (i == MaxToolCalls - 1)
                    {
                        throw new InvalidOperationException($"Agent exceeded the maximum of {MaxToolCalls} tool calls.");
                    }

                    if (string.IsNullOrWhiteSpace(toolRequest.Path))
                    {
                        throw new InvalidOperationException("Agent requested read_file without a valid 'path'.");
                    }

                    var normalizedPath = NormalizePath(toolRequest.Path);
                    if (!allowedPathSet.Contains(normalizedPath))
                    {
                        throw new InvalidOperationException($"Agent requested file outside allowed scope: {normalizedPath}");
                    }

                    var fileContent = ReadFile(repositoryRoot, normalizedPath);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call: read_file('{normalizedPath}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, $"File content: {normalizedPath}", fileContent, ct);

                    transcript.AppendLine($"AGENT TOOL REQUEST #{i + 1}:");
                    transcript.AppendLine(rawText.Trim());
                    transcript.AppendLine();
                    transcript.AppendLine($"TOOL RESULT FOR read_file('{normalizedPath}'):");
                    transcript.AppendLine("--- FILE CONTENT START ---");
                    transcript.AppendLine(fileContent);
                    transcript.AppendLine("--- FILE CONTENT END ---");
                    transcript.AppendLine();
                    currentPrompt = BuildFollowUpPrompt(initialPrompt, transcript.ToString());
                    continue;
                }
                case "save_workflow_output":
                {
                    var requestedWorkflowRunId = ParseWorkflowRunId(toolRequest.WorkflowRunId, workflowRunId, "save_workflow_output");
                    if (toolRequest.Output is null || toolRequest.Output.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    {
                        throw new InvalidOperationException("Agent requested save_workflow_output without an 'output' object.");
                    }

                    var outputJson = JsonSerializer.Serialize(toolRequest.Output.Value, JsonOptions);
                    await payloadStore.SaveOutputAsync(requestedWorkflowRunId, outputJson, ct);
                    await logs.AppendLineAsync(workflowRunId, $"Tool call: save_workflow_output('{requestedWorkflowRunId}').", ct);
                    await logs.AppendBlockAsync(workflowRunId, "Saved workflow output payload", outputJson, ct);
                    return outputJson;
                }
                default:
                    throw new InvalidOperationException($"Unsupported tool action requested by agent: {toolRequest.Action}");
            }
        }

        throw new InvalidOperationException("Agent did not produce a final response.");
    }

    private static Guid ParseWorkflowRunId(string? value, Guid fallbackWorkflowRunId, string action)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackWorkflowRunId;
        }

        if (Guid.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Agent requested {action} with an invalid workflowRunId: {value}");
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
            return request is not null && !string.IsNullOrWhiteSpace(request.Action);
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

        return text[..MaxFileCharacters] + "\n\n[TRUNCATED BY SERVER: file content exceeded 20000 characters]";
    }

    private static string BuildFollowUpPrompt(string initialPrompt, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine(initialPrompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("TOOL INTERACTION HISTORY:");
        sb.AppendLine(transcript.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Continue the workflow.");
        sb.AppendLine("- If you have not loaded the workflow input yet, call get_workflow_input.");
        sb.AppendLine("- If you need more repository evidence, return ONLY a JSON object with this exact schema: {\"action\":\"read_file\",\"path\":\"relative/path\"}.");
        sb.AppendLine("- When your result is ready, return ONLY a JSON object with this exact schema: {\"action\":\"save_workflow_output\",\"workflowRunId\":\"guid\",\"output\":{...}}.");
        sb.AppendLine("- Do not include markdown fences or commentary.");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class ToolRequest
    {
        public string Action { get; set; } = string.Empty;
        public string? WorkflowRunId { get; set; }
        public string? Path { get; set; }
        public JsonElement? Output { get; set; }
    }
}
