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
        var dir = GetRunDirectory(runId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, content, ct);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dir = GetRunDirectory(runId);
        if (!Directory.Exists(dir))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var files = Directory
            .EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public async Task<string?> ReadTextAsync(Guid runId, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var path = Path.Combine(GetRunDirectory(runId), fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct);
    }

    public Task DeleteRunAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dir = GetRunDirectory(runId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string GetRunDirectory(Guid runId)
        => Path.Combine(_rootPath, "runs", runId.ToString("N"));
}
