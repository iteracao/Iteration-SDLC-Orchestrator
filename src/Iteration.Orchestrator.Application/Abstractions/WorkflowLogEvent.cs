namespace Iteration.Orchestrator.Application.Abstractions;

/// <summary>
/// Structured workflow log event.
/// Highlight logs render this as one compact line; raw logs persist the full event as JSONL.
/// </summary>
public sealed record WorkflowLogEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Level { get; init; } = "info";
    public string EventType { get; init; } = "event";
    public string? Phase { get; init; }
    public int? Interaction { get; init; }
    public string? Role { get; init; }
    public string? ToolName { get; init; }
    public string? CorrelationId { get; init; }
    public long? DurationMs { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? PayloadPreview { get; init; }
    public int PayloadChars { get; init; }
    public string? RawPayload { get; init; }
}
