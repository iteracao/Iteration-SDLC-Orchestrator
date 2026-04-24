using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

public interface IAgentConversationFactory
{
    string ProviderName { get; }
    string SelectedModel { get; }
    bool IsOpenAiConfigurationComplete { get; }

    IAgentConversation CreateConversation(string agentName, string instructions);
}

public interface IAgentConversation
{
    Task<AgentConversationResponse> RunAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        CancellationToken ct);
}

public sealed class SelectedAgentConversationFactory : IAgentConversationFactory
{
    private readonly LlmProviderSelection _selection;
    private readonly Func<HttpClient> _createOpenAiHttpClient;

    public SelectedAgentConversationFactory(
        LlmProviderSelection selection,
        Func<HttpClient> createOpenAiHttpClient)
    {
        _selection = selection;
        _createOpenAiHttpClient = createOpenAiHttpClient;
    }

    public string ProviderName => _selection.ProviderName;

    public string SelectedModel => _selection.Model;

    public bool IsOpenAiConfigurationComplete => _selection.IsOpenAiConfigurationComplete;

    public IAgentConversation CreateConversation(string agentName, string instructions)
        => string.Equals(_selection.ProviderName, "OpenAI", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiResponsesConversation(_createOpenAiHttpClient(), _selection, instructions)
            : new OllamaAgentConversation(_selection, agentName, instructions);
}

internal sealed class OllamaAgentConversation : IAgentConversation
{
    private readonly AIAgent _agent;
    private AgentSession? _session;

    public OllamaAgentConversation(LlmProviderSelection selection, string agentName, string instructions)
    {
        var chatClient = new OllamaChatClient(new Uri(selection.BaseUrl), modelId: selection.Model);
        _agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);
    }

    public async Task<AgentConversationResponse> RunAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        CancellationToken ct)
    {
        _session ??= await _agent.CreateSessionAsync(ct);
        var rawResponse = await _agent.RunAsync(messages, session: _session, cancellationToken: ct);
        return new AgentConversationResponse(rawResponse.Text ?? string.Empty);
    }
}

internal sealed class OpenAiResponsesConversation : IAgentConversation
{
    private readonly HttpClient _httpClient;
    private readonly LlmProviderSelection _selection;
    private readonly string _instructions;
    private string? _previousResponseId;

    public OpenAiResponsesConversation(HttpClient httpClient, LlmProviderSelection selection, string instructions)
    {
        _httpClient = httpClient;
        _selection = selection;
        _instructions = instructions;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, selection.TimeoutSeconds));
    }

    public async Task<AgentConversationResponse> RunAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentToolDefinition>? tools,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_selection.ApiKey))
        {
            throw new InvalidOperationException("OpenAI configuration is incomplete.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_selection.BaseUrl.TrimEnd('/')}/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _selection.ApiKey);
        request.Content = JsonContent.Create(BuildPayload(messages, tools));

        using var response = await SendWithRateLimitRetriesAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(responseContent);
        if (document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            _previousResponseId = idElement.GetString();
        }

        return new AgentConversationResponse(
            ExtractOutputText(document.RootElement),
            ExtractToolCalls(document.RootElement),
            supportsNativeToolCalls: true);
    }


    private async Task<HttpResponseMessage> SendWithRateLimitRetriesAsync(HttpRequestMessage request, CancellationToken ct)
    {
        const int maxAttempts = 4;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var clonedRequest = await CloneRequestAsync(request, ct);
            var response = await _httpClient.SendAsync(clonedRequest, ct);
            if (response.StatusCode != (HttpStatusCode)429 || attempt == maxAttempts)
            {
                return response;
            }

            var retryDelay = response.Headers.RetryAfter?.Delta ?? delay;
            response.Dispose();
            await Task.Delay(retryDelay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }

        throw new InvalidOperationException("Unreachable retry state.");
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync(ct);
            var contentClone = new ByteArrayContent(contentBytes);
            foreach (var header in request.Content.Headers)
            {
                contentClone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = contentClone;
        }

        clone.Version = request.Version;
        clone.VersionPolicy = request.VersionPolicy;
        return clone;
    }
    private object BuildPayload(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentToolDefinition>? tools)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _selection.Model,
            ["instructions"] = _instructions,
            ["input"] = messages.Select(MapMessage).ToArray()
        };

        if (!string.IsNullOrWhiteSpace(_previousResponseId))
        {
            payload["previous_response_id"] = _previousResponseId;
        }

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(MapToolDefinition).ToArray();
            payload["parallel_tool_calls"] = false;
        }

        return payload;
    }

    private static object MapMessage(ChatMessage message)
    {
        var normalizedRole = message.Role.ToString();
        var text = message.Text ?? string.Empty;
        if (normalizedRole.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            if (AgentToolMessageProtocol.TryParseNativeToolResultMessage(text, out var nativeToolResult))
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = nativeToolResult.CallId,
                    ["output"] = nativeToolResult.Payload
                };
            }

            text = $"TOOL RESULT\n{text}";
        }

        var role = normalizedRole.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : normalizedRole.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? "developer"
                : "user";

        return new
        {
            role,
            content = text
        };
    }

    private static object MapToolDefinition(AgentToolDefinition tool)
        => new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = tool.ParametersSchema
        };

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextElement)
            && outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contentElement.EnumerateArray())
            {
                if (!content.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(text);
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<AgentToolCall> ExtractToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentToolCall>();
        }

        var toolCalls = new List<AgentToolCall>();
        foreach (var item in outputElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var arguments = ParseArguments(item);
            var callId = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
                ? callIdElement.GetString()
                : null;

            toolCalls.Add(new AgentToolCall(
                nameElement.GetString() ?? string.Empty,
                arguments,
                callId,
                isNative: true));
        }

        return toolCalls;
    }

    private static JsonElement ParseArguments(JsonElement functionCallItem)
    {
        if (functionCallItem.TryGetProperty("arguments", out var argumentsElement))
        {
            if (argumentsElement.ValueKind == JsonValueKind.String)
            {
                var rawArguments = argumentsElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawArguments))
                {
                    using var argsDocument = JsonDocument.Parse(rawArguments);
                    return argsDocument.RootElement.Clone();
                }
            }
            else if (argumentsElement.ValueKind == JsonValueKind.Object)
            {
                return argumentsElement.Clone();
            }
        }

        using var emptyArgsDocument = JsonDocument.Parse("{}");
        return emptyArgsDocument.RootElement.Clone();
    }
}
