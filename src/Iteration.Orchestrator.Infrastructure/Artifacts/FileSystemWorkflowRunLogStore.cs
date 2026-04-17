using System.Text;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Artifacts;

public sealed class FileSystemWorkflowRunLogStore : IWorkflowRunLogStore
{
    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemWorkflowRunLogStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public async Task AppendLineAsync(Guid workflowRunId, string message, CancellationToken ct)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
        await AppendAsync(workflowRunId, line, ct);
    }

    public async Task AppendBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] {title}");
        sb.AppendLine();
        sb.AppendLine($"--- {title.ToUpperInvariant()} START ---");
        sb.AppendLine(content ?? string.Empty);
        sb.AppendLine($"--- {title.ToUpperInvariant()} END ---");
        sb.AppendLine();
        await AppendAsync(workflowRunId, sb.ToString(), ct);
    }

    public async Task<string?> ReadAsync(Guid workflowRunId, CancellationToken ct)
    {
        var path = GetPath(workflowRunId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    private async Task AppendAsync(Guid workflowRunId, string content, CancellationToken ct)
    {
        var path = GetPath(workflowRunId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, content, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(Guid workflowRunId)
        => Path.Combine(_rootPath, "workflow-logs", $"{workflowRunId}.log");
}
