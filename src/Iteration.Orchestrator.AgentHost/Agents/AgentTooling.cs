using System.Text.Json;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class AgentToolDefinition
{
    public AgentToolDefinition(string name, string description, JsonElement parametersSchema)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Tool name is required.", nameof(name)) : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? throw new ArgumentException("Tool description is required.", nameof(description)) : description.Trim();
        ParametersSchema = parametersSchema.Clone();
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement ParametersSchema { get; }

    public static AgentToolDefinition Create(string name, string description, string parametersSchemaJson)
        => new(name, description, ParseSchema(parametersSchemaJson));

    private static JsonElement ParseSchema(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            throw new ArgumentException("Tool schema JSON is required.", nameof(schemaJson));
        }

        using var document = JsonDocument.Parse(schemaJson);
        return document.RootElement.Clone();
    }
}

public sealed class AgentToolCall
{
    public AgentToolCall(string name, JsonElement arguments, string? nativeCallId = null, bool isNative = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Tool call name is required.", nameof(name)) : name.Trim();
        Arguments = arguments.Clone();
        NativeCallId = string.IsNullOrWhiteSpace(nativeCallId) ? null : nativeCallId.Trim();
        IsNative = isNative;
    }

    public string Name { get; }

    public JsonElement Arguments { get; }

    public string? NativeCallId { get; }

    public bool IsNative { get; }
}

public sealed class AgentConversationResponse
{
    public AgentConversationResponse(
        string text,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        bool supportsNativeToolCalls = false)
    {
        Text = text ?? string.Empty;
        ToolCalls = toolCalls ?? Array.Empty<AgentToolCall>();
        SupportsNativeToolCalls = supportsNativeToolCalls;
    }

    public string Text { get; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; }

    public bool SupportsNativeToolCalls { get; }
}

public sealed class AgentToolExecutionResult
{
    public AgentToolExecutionResult(
        string modelPayload,
        string logSummary,
        string? debugPayload = null,
        string? finalPhaseOutput = null)
    {
        ModelPayload = modelPayload ?? string.Empty;
        LogSummary = logSummary ?? string.Empty;
        DebugPayload = debugPayload;
        FinalPhaseOutput = finalPhaseOutput;
    }

    public string ModelPayload { get; }

    public string LogSummary { get; }

    public string? DebugPayload { get; }

    public string? FinalPhaseOutput { get; }
}

public interface IAgentTool
{
    AgentToolDefinition Definition { get; }

    Task<AgentToolExecutionResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken);
}

internal sealed class DelegateAgentTool : IAgentTool
{
    private readonly Func<JsonElement, CancellationToken, Task<AgentToolExecutionResult>> _executeAsync;

    public DelegateAgentTool(
        AgentToolDefinition definition,
        Func<JsonElement, CancellationToken, Task<AgentToolExecutionResult>> executeAsync)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    public AgentToolDefinition Definition { get; }

    public Task<AgentToolExecutionResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken)
        => _executeAsync(args, cancellationToken);
}

public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (_tools.ContainsKey(tool.Definition.Name))
            {
                throw new InvalidOperationException($"Duplicate agent tool '{tool.Definition.Name}'.");
            }

            _tools.Add(tool.Definition.Name, tool);
        }
    }

    public bool TryResolve(string toolName, out IAgentTool tool)
        => _tools.TryGetValue(toolName, out tool!);

    public IAgentTool Resolve(string toolName)
        => TryResolve(toolName, out var tool)
            ? tool
            : throw new InvalidOperationException($"Unknown tool '{toolName}'.");

    public IReadOnlyList<AgentToolDefinition> GetDefinitions()
        => _tools.Values.Select(x => x.Definition).ToArray();
}

public sealed class AgentToolExecutor
{
    private readonly AgentToolRegistry _registry;
    private readonly HashSet<string>? _allowedToolNames;

    public AgentToolExecutor(AgentToolRegistry registry, IReadOnlyCollection<string>? allowedToolNames)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        if (allowedToolNames is { Count: > 0 })
        {
            _allowedToolNames = allowedToolNames
                .Select(NormalizeToolName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<AgentToolDefinition> GetAllowedDefinitions()
    {
        if (_allowedToolNames is null)
        {
            return _registry.GetDefinitions();
        }

        return _allowedToolNames
            .Select(_registry.Resolve)
            .Select(x => x.Definition)
            .ToArray();
    }

    public async Task<AgentToolExecutionResult> ExecuteAsync(AgentToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var normalizedToolName = NormalizeToolName(call.Name);
        if (_allowedToolNames is not null && !_allowedToolNames.Contains(normalizedToolName))
        {
            throw new InvalidOperationException($"Tool '{call.Name}' is not allowed in this phase.");
        }

        var tool = _registry.Resolve(normalizedToolName);
        return await tool.ExecuteAsync(call.Arguments, cancellationToken);
    }

    public static string NormalizeToolName(string toolName)
        => string.Equals(toolName?.Trim(), "read_file", StringComparison.OrdinalIgnoreCase)
            ? "get_file"
            : (toolName ?? string.Empty).Trim();
}

internal static class AgentToolMessageProtocol
{
    private const string Prefix = "NATIVE TOOL RESULT";
    private const string CallIdHeader = "CALL ID:";
    private const string ToolHeader = "TOOL:";

    public static string BuildNativeToolResultMessage(string callId, string toolName, string payload)
    {
        var normalizedPayload = payload ?? string.Empty;
        return $"{Prefix}{Environment.NewLine}" +
               $"{CallIdHeader} {callId}{Environment.NewLine}" +
               $"{ToolHeader} {toolName}{Environment.NewLine}" +
               Environment.NewLine +
               normalizedPayload;
    }

    public static bool TryParseNativeToolResultMessage(
        string content,
        out NativeToolResultEnvelope envelope)
    {
        envelope = default;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith(Prefix + "\n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = normalized.Split('\n');
        if (lines.Length < 4)
        {
            return false;
        }

        var callIdLine = lines[1].Trim();
        var toolLine = lines[2].Trim();
        if (!callIdLine.StartsWith(CallIdHeader, StringComparison.OrdinalIgnoreCase) ||
            !toolLine.StartsWith(ToolHeader, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var callId = callIdLine[CallIdHeader.Length..].Trim();
        var toolName = toolLine[ToolHeader.Length..].Trim();
        var payload = string.Join("\n", lines.Skip(4));

        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        envelope = new NativeToolResultEnvelope(callId, toolName, payload);
        return true;
    }

    internal readonly record struct NativeToolResultEnvelope(string CallId, string ToolName, string Payload);
}
