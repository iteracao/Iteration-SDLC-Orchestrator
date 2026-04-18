using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.SolutionBridge.Services;

public sealed class LocalFileSystemSolutionBridge : ISolutionBridge
{
    private static readonly string[] SearchableExtensions =
    [
        ".cs",
        ".razor",
        ".csproj",
        ".sln",
        ".props",
        ".targets",
        ".json",
        ".md",
        ".yml",
        ".yaml",
        ".xml"
    ];

    public Task<IReadOnlyList<RepositoryEntry>> ListRepositoryTreeAsync(SolutionTarget target, string? relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var basePath = ResolveDirectory(target.RepositoryPath, relativePath);
        var rootPath = Path.GetFullPath(target.RepositoryPath);

        var entries = Directory.EnumerateFileSystemEntries(basePath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                return new RepositoryEntry(relative, Directory.Exists(path));
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<RepositoryEntry>>(entries);
    }

    public Task<string> ReadFileAsync(SolutionTarget target, string relativePath, CancellationToken ct)
    {
        var fullPath = ResolveFile(target.RepositoryPath, relativePath);
        return File.ReadAllTextAsync(fullPath, ct);
    }

    public Task<IReadOnlyList<FileSearchHit>> SearchFilesAsync(SolutionTarget target, string query, string? relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<FileSearchHit>>(Array.Empty<FileSearchHit>());
        }

        var basePath = ResolveDirectory(target.RepositoryPath, relativePath);
        var rootPath = Path.GetFullPath(target.RepositoryPath);
        var hits = new List<FileSearchHit>();

        foreach (var file in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories)
                     .Where(IsSearchableFile)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = Math.Max(0, index - 120);
            var length = Math.Min(320, text.Length - start);
            var snippet = text.Substring(start, length)
                .Replace("\r", string.Empty)
                .Replace("\n", " ⏎ ");

            hits.Add(new FileSearchHit(
                Path.GetRelativePath(rootPath, file).Replace('\\', '/'),
                snippet));

            if (hits.Count >= 25)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<FileSearchHit>>(hits);
    }

    public Task<SolutionSnapshot> GetSolutionSnapshotAsync(SolutionTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var files = Directory.EnumerateFiles(target.RepositoryPath, "*.*", SearchOption.AllDirectories).ToList();

        var snapshot = new SolutionSnapshot(
            target.RepositoryPath,
            files.Where(x => x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x).Replace('\\', '/'))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x).Replace('\\', '/'))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Test", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x).Replace('\\', '/'))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Web", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x).Replace('\\', '/'))
                 .ToList(),
            files.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && x.Contains("Api", StringComparison.OrdinalIgnoreCase))
                 .Select(x => Path.GetRelativePath(target.RepositoryPath, x).Replace('\\', '/'))
                 .ToList(),
            Directory.EnumerateDirectories(target.RepositoryPath)
                     .Select(x => Path.GetFileName(x))
                     .OrderBy(x => x)
                     .ToList());

        return Task.FromResult(snapshot);
    }

    private static bool IsSearchableFile(string path)
        => SearchableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string ResolveFile(string root, string relativePath)
    {
        var full = GetSafePath(root, relativePath);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"Requested file does not exist: {relativePath}");
        }

        return full;
    }

    private static string ResolveDirectory(string root, string? relativePath)
    {
        var full = string.IsNullOrWhiteSpace(relativePath)
            ? Path.GetFullPath(root)
            : GetSafePath(root, relativePath);

        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"Requested directory does not exist: {relativePath}");
        }

        return full;
    }

    private static string GetSafePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path traversal detected.");
        }

        return full;
    }
}
