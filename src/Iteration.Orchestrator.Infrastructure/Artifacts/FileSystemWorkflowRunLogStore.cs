using System.Text;
using System.Text.RegularExpressions;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Artifacts;

public sealed class FileSystemWorkflowRunLogStore : IWorkflowRunLogStore
{
    private static readonly Regex ExcessBlankLinesRegex = new("\n{3,}", RegexOptions.Compiled);

    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemWorkflowRunLogStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public async Task AppendLineAsync(Guid workflowRunId, string message, CancellationToken ct)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
        await AppendAsync(GetPath(workflowRunId), line, ct);
    }

    public async Task AppendSectionAsync(Guid workflowRunId, string title, CancellationToken ct)
    {
        var content = $"{Environment.NewLine}[{DateTime.UtcNow:O}] == {title.ToUpperInvariant()} =={Environment.NewLine}";
        await AppendAsync(GetPath(workflowRunId), content, ct);
    }

    public async Task AppendKeyValuesAsync(Guid workflowRunId, string title, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"[{DateTime.UtcNow:O}] == {title.ToUpperInvariant()} ==");
        foreach (var pair in values)
        {
            sb.AppendLine($"- {pair.Key}: {pair.Value ?? string.Empty}");
        }

        await AppendAsync(GetPath(workflowRunId), sb.ToString(), ct);
    }

    public async Task AppendBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"[{DateTime.UtcNow:O}] == {title.ToUpperInvariant()} ==");
        sb.AppendLine(content ?? string.Empty);
        await AppendAsync(GetPath(workflowRunId), sb.ToString(), ct);
    }

    public Task<string?> ReadAsync(Guid workflowRunId, CancellationToken ct)
        => ReadFileAsync(GetPath(workflowRunId), ct);

    public async Task AppendRawBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"[{DateTime.UtcNow:O}] == RAW: {title.ToUpperInvariant()} ==");
        sb.AppendLine(content ?? string.Empty);
        await AppendAsync(GetRawPath(workflowRunId), sb.ToString(), ct);
    }

    public Task<string?> ReadRawAsync(Guid workflowRunId, CancellationToken ct)
        => ReadFileAsync(GetRawPath(workflowRunId), ct);

    private static async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    private async Task AppendAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await _gate.WaitAsync(ct);
        try
        {
            var normalizedContent = NormalizeLogContent(content, File.Exists(path));
            await File.AppendAllTextAsync(path, normalizedContent, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeLogContent(string content, bool hasExistingContent)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = ExcessBlankLinesRegex.Replace(normalized, "\n\n");

        if (hasExistingContent)
        {
            normalized = normalized.TrimStart('\n');
            if (!normalized.StartsWith("\n", StringComparison.Ordinal))
            {
                normalized = "\n" + normalized;
            }
        }
        else
        {
            normalized = normalized.TrimStart('\n');
        }

        return normalized.Replace("\n", Environment.NewLine);
    }

    private string GetPath(Guid workflowRunId)
        => Path.Combine(_rootPath, "workflow-logs", $"{workflowRunId}.log");

    private string GetRawPath(Guid workflowRunId)
        => Path.Combine(_rootPath, "workflow-logs", $"{workflowRunId}.raw.log");
}
