using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Artifacts;

public sealed class FileSystemWorkflowRunLogStore : IWorkflowRunLogStore
{
    private const int HighlightPreviewMaxChars = 500;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemWorkflowRunLogStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public Task AppendLineAsync(Guid workflowRunId, string message, CancellationToken ct)
        => AppendEventAsync(
            workflowRunId,
            new WorkflowLogEvent
            {
                EventType = "message",
                Summary = message ?? string.Empty,
                PayloadPreview = BuildPreview(message),
                PayloadChars = message?.Length ?? 0,
                RawPayload = message
            },
            ct);

    public Task AppendSectionAsync(Guid workflowRunId, string title, CancellationToken ct)
        => AppendEventAsync(
            workflowRunId,
            new WorkflowLogEvent
            {
                EventType = "section",
                Summary = title ?? string.Empty,
                PayloadPreview = BuildPreview(title),
                PayloadChars = title?.Length ?? 0,
                RawPayload = title
            },
            ct);

    public Task AppendKeyValuesAsync(Guid workflowRunId, string title, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(values, RawJsonOptions);
        var summary = string.IsNullOrWhiteSpace(title)
            ? $"key_values count={values.Count}"
            : $"{title} count={values.Count}";

        return AppendEventAsync(
            workflowRunId,
            new WorkflowLogEvent
            {
                EventType = "key_values",
                Summary = summary,
                PayloadPreview = BuildPreview(payload),
                PayloadChars = payload.Length,
                RawPayload = payload
            },
            ct);
    }

    public Task AppendBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct)
    {
        content ??= string.Empty;
        return AppendEventAsync(workflowRunId, BuildBlockEvent(title, content, isRaw: false), ct);
    }

    public Task<string?> ReadAsync(Guid workflowRunId, CancellationToken ct)
        => ReadFileAsync(GetPath(workflowRunId), ct);

    public Task AppendRawBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct)
    {
        content ??= string.Empty;
        return AppendEventAsync(workflowRunId, BuildBlockEvent(title, content, isRaw: true), ct);
    }

    public Task<string?> ReadRawAsync(Guid workflowRunId, CancellationToken ct)
        => ReadFileAsync(GetRawPath(workflowRunId), ct);

    public async Task AppendEventAsync(Guid workflowRunId, WorkflowLogEvent logEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var timestamp = logEvent.Timestamp == default ? DateTimeOffset.UtcNow : logEvent.Timestamp;
        var normalizedEvent = logEvent with
        {
            Timestamp = timestamp,
            Level = NormalizeToken(logEvent.Level, "info"),
            EventType = NormalizeToken(logEvent.EventType, "event"),
            Phase = NormalizeNullable(logEvent.Phase),
            Role = NormalizeNullable(logEvent.Role),
            ToolName = NormalizeNullable(logEvent.ToolName),
            CorrelationId = NormalizeNullable(logEvent.CorrelationId),
            DurationMs = logEvent.DurationMs,
            Summary = NormalizeInline(logEvent.Summary),
            PayloadPreview = BuildPreview(logEvent.PayloadPreview),
            PayloadChars = logEvent.PayloadChars > 0
                ? logEvent.PayloadChars
                : logEvent.RawPayload?.Length ?? logEvent.PayloadPreview?.Length ?? 0
        };

        var highlightLine = FormatHighlightLine(normalizedEvent);
        var rawLine = ShouldWriteRawEvent(normalizedEvent)
            ? JsonSerializer.Serialize(normalizedEvent, RawJsonOptions) + "\n"
            : null;

        Directory.CreateDirectory(Path.GetDirectoryName(GetPath(workflowRunId))!);

        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(GetPath(workflowRunId), highlightLine, ct);
            if (rawLine is not null)
            {
                await File.AppendAllTextAsync(GetRawPath(workflowRunId), rawLine, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    private static string FormatHighlightLine(WorkflowLogEvent logEvent)
    {
        var sb = new StringBuilder();
        sb.Append(logEvent.Timestamp.UtcDateTime.ToString("O"));
        sb.Append(" | ");
        sb.Append(logEvent.Level);
        sb.Append(" | ");
        sb.Append(logEvent.EventType);

        if (!string.IsNullOrWhiteSpace(logEvent.Phase))
        {
            sb.Append(" | phase=");
            sb.Append(logEvent.Phase);
        }

        if (logEvent.Interaction is not null)
        {
            sb.Append(" | interaction=");
            sb.Append(logEvent.Interaction.Value);
        }

        if (!string.IsNullOrWhiteSpace(logEvent.Role))
        {
            sb.Append(" | role=");
            sb.Append(logEvent.Role);
        }

        if (!string.IsNullOrWhiteSpace(logEvent.ToolName))
        {
            sb.Append(" | tool=");
            sb.Append(logEvent.ToolName);
        }

        if (!string.IsNullOrWhiteSpace(logEvent.CorrelationId))
        {
            sb.Append(" | cid=");
            sb.Append(logEvent.CorrelationId);
        }

        if (logEvent.DurationMs is not null)
        {
            sb.Append(" | durationMs=");
            sb.Append(logEvent.DurationMs.Value);
        }

        if (!string.IsNullOrWhiteSpace(logEvent.Summary))
        {
            sb.Append(" | ");
            sb.Append(logEvent.Summary);
        }

        sb.Append(" | chars=");
        sb.Append(logEvent.PayloadChars);

        if (!string.IsNullOrWhiteSpace(logEvent.PayloadPreview))
        {
            sb.Append(" | preview=\"");
            sb.Append(EscapeHighlightValue(logEvent.PayloadPreview!));
            sb.Append('"');
        }

        sb.Append(Environment.NewLine);
        return sb.ToString();
    }

    private static WorkflowLogEvent BuildBlockEvent(string? title, string content, bool isRaw)
    {
        var eventType = isRaw ? InferRawEventType(title) : InferBlockEventType(title);
        var metadata = ExtractBlockMetadata(title);
        var summary = BuildCompactBlockSummary(eventType, title);

        return new WorkflowLogEvent
        {
            Level = summary == "failure" ? "error" : "info",
            EventType = eventType,
            Phase = metadata.Phase,
            Interaction = metadata.Interaction,
            ToolName = metadata.ToolName,
            Summary = summary,
            PayloadPreview = BuildBlockPreview(eventType, content, isRaw),
            PayloadChars = content.Length,
            RawPayload = content
        };
    }


    private static string BuildBlockPreview(string eventType, string content, bool isRaw)
    {
        if (isRaw)
        {
            return "transport payload stored in RAW log only";
        }

        if (string.Equals(eventType, "interaction", StringComparison.OrdinalIgnoreCase))
        {
            return "interaction details omitted from highlight log; see RAW log and artifacts for payloads";
        }

        if (string.Equals(eventType, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            return "prompt content omitted from highlight log";
        }

        if (string.Equals(eventType, "model_response", StringComparison.OrdinalIgnoreCase))
        {
            return "model response content omitted from highlight log";
        }

        if (string.Equals(eventType, "model_message", StringComparison.OrdinalIgnoreCase))
        {
            return "model message content omitted from highlight log";
        }

        return BuildPreview(content);
    }

    private static string BuildCompactBlockSummary(string eventType, string? title)
    {
        return eventType switch
        {
            "model_sent" => "sent",
            "model_received" when (title ?? string.Empty).Contains("failure", StringComparison.OrdinalIgnoreCase) => "failure",
            "model_received" => "received",
            "model_message" => "message",
            "tool_sent" => "sent",
            "tool_received" when (title ?? string.Empty).Contains("failure", StringComparison.OrdinalIgnoreCase) => "failure",
            "tool_received" => "received",
            "interaction" => "summary",
            "prompt" => "phase prompt",
            "section" => NormalizeInline(title),
            _ => string.IsNullOrWhiteSpace(title) ? eventType : NormalizeInline(title)
        };
    }

    private static BlockMetadata ExtractBlockMetadata(string? title)
    {
        var normalized = NormalizeInline(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return default;
        }

        var match = Regex.Match(
            normalized,
            @"^(?<phase>.+?)\s+interaction\s+(?<interaction>\d+)\s+(?<kind>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return default;
        }

        var kind = match.Groups["kind"].Value;
        return new BlockMetadata(
            NormalizeNullable(match.Groups["phase"].Value),
            int.TryParse(match.Groups["interaction"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interaction)
                ? interaction
                : null,
            ExtractToolName(kind));
    }

    private static string? ExtractToolName(string kind)
    {
        var normalized = NormalizeInline(kind);
        var toolMatch = Regex.Match(normalized, @"tool\s+(request|response|result|failure)\s*[:=]?\s*(?<tool>[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return toolMatch.Success ? NormalizeNullable(toolMatch.Groups["tool"].Value) : null;
    }

    private static bool ShouldWriteRawEvent(WorkflowLogEvent logEvent)
        => string.Equals(logEvent.EventType, "model_sent", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(logEvent.EventType, "model_received", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(logEvent.EventType, "tool_sent", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(logEvent.EventType, "tool_received", StringComparison.OrdinalIgnoreCase);

    private readonly record struct BlockMetadata(string? Phase, int? Interaction, string? ToolName);

    private static string BuildPreview(string? content)
    {
        var preview = NormalizeInline(content);
        if (preview.Length <= HighlightPreviewMaxChars)
        {
            return preview;
        }

        return preview[..HighlightPreviewMaxChars] + "…";
    }

    private static string NormalizeInline(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Trim();

        return WhitespaceRegex.Replace(normalized, " ");
    }

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = NormalizeInline(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeNullable(string? value)
    {
        var normalized = NormalizeInline(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string EscapeHighlightValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string InferBlockEventType(string? title)
    {
        var normalized = title ?? string.Empty;
        if (normalized.Contains("interaction", StringComparison.OrdinalIgnoreCase)) return "interaction";
        if (normalized.Contains("response", StringComparison.OrdinalIgnoreCase)) return "model_response";
        if (normalized.Contains("model message", StringComparison.OrdinalIgnoreCase)) return "model_message";
        if (normalized.Contains("prompt", StringComparison.OrdinalIgnoreCase)) return "prompt";
        return "block";
    }

    private static string InferRawEventType(string? title)
    {
        var normalized = title ?? string.Empty;
        if (normalized.Contains("model request", StringComparison.OrdinalIgnoreCase)) return "model_sent";
        if (normalized.Contains("model response", StringComparison.OrdinalIgnoreCase)) return "model_received";
        if (normalized.Contains("model failure", StringComparison.OrdinalIgnoreCase)) return "model_received";
        if (normalized.Contains("tool request", StringComparison.OrdinalIgnoreCase)) return "tool_sent";
        if (normalized.Contains("tool response", StringComparison.OrdinalIgnoreCase)) return "tool_received";
        if (normalized.Contains("tool result", StringComparison.OrdinalIgnoreCase)) return "tool_received";
        if (normalized.Contains("tool failure", StringComparison.OrdinalIgnoreCase)) return "tool_received";
        return "raw";
    }

    private string GetPath(Guid workflowRunId)
        => Path.Combine(_rootPath, "workflow-logs", $"{workflowRunId}.log");

    private string GetRawPath(Guid workflowRunId)
        => Path.Combine(_rootPath, "workflow-logs", $"{workflowRunId}.raw.log");
}
