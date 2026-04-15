using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.SolutionBridge.Services;

public sealed class LocalFileSystemSolutionBridge : ISolutionBridge
{
    public Task<IReadOnlyList<RepositoryEntry>> ListRepositoryTreeAsync(SolutionTarget target, CancellationToken ct)
    {
        var entries = Directory.EnumerateFileSystemEntries(target.RepositoryPath, "*", SearchOption.AllDirectories)
            .Take(1000)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(target.RepositoryPath, path);
                return new RepositoryEntry(relative, Directory.Exists(path));
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RepositoryEntry>>(entries);
    }

    public Task<string> ReadFileAsync(SolutionTarget target, string relativePath, CancellationToken ct)
    {
        var fullPath = SafeCombine(target.RepositoryPath, relativePath);
        return File.ReadAllTextAsync(fullPath, ct);
    }

    public Task<IReadOnlyList<FileSearchHit>> SearchFilesAsync(SolutionTarget target, string query, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(target.RepositoryPath, "*.*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Take(500);

        var hits = new List<FileSearchHit>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, index - 80);
            var length = Math.Min(200, text.Length - start);
            var snippet = text.Substring(start, length);

            hits.Add(new FileSearchHit(Path.GetRelativePath(target.RepositoryPath, file), snippet));

            if (hits.Count >= 20)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<FileSearchHit>>(hits);
    }

    public Task<SolutionSnapshot> GetSolutionSnapshotAsync(SolutionTarget target, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(target.RepositoryPath, "*.*", SearchOption.AllDirectories).ToList();

        var snapshot = new SolutionSnapshot(
            target.RepositoryPath,
            files.Where(x => x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Test", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Web", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Api", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x))
                 .ToList(),
            Directory.EnumerateDirectories(target.RepositoryPath)
                     .Select(x => Path.GetFileName(x))
                     .OrderBy(x => x)
                     .ToList());

        return Task.FromResult(snapshot);
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var fullRoot = Path.GetFullPath(root);

        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path traversal detected.");
        }

        return full;
    }
}
