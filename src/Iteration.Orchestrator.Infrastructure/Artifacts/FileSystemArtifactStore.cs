using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Artifacts;

public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _rootPath;

    public FileSystemArtifactStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public async Task SaveTextAsync(Guid runId, string fileName, string content, CancellationToken ct)
    {
        var dir = Path.Combine(_rootPath, "runs", runId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, content, ct);
    }

    public Task DeleteRunAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dir = Path.Combine(_rootPath, "runs", runId.ToString("N"));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }
}
