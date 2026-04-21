using System.Diagnostics;
using System.Text;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Common;

public static class RepositoryPromptInputDiscovery
{
    private const int MaxSearchHits = 25;

    private static readonly string[] AllowedRepositoryExtensions =
    [
        ".cs",
        ".razor",
        ".csproj",
        ".sln",
        ".props",
        ".targets"
    ];

    private static readonly string[] SearchableRepositoryExtensions =
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
        ".xml",
        ".txt"
    ];

    public static async Task<IReadOnlyList<string>> LoadRepositoryFilesAsync(
        SolutionTarget target,
        CancellationToken ct)
    {
        var repositoryPath = target.RepositoryPath;

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Array.Empty<string>();
        }

        var visibleFiles = await LoadVisibleRepositoryFilesAsync(repositoryPath, ct);

        return visibleFiles
            .Where(path => !path.StartsWith("AI/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase))
            .Where(path => AllowedRepositoryExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IReadOnlyList<string>> LoadVisibleRepositoryFilesAsync(
        string repositoryPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Array.Empty<string>();
        }

        var visibleFiles = await RunGitRelativePathQueryAsync(
            repositoryPath,
            "ls-files --cached --others --exclude-standard",
            ct);

        if (visibleFiles.Count == 0)
        {
            return Array.Empty<string>();
        }

        return visibleFiles
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> FilterExcludedStructurePaths(
        IReadOnlyList<string> visibleFiles)
        => visibleFiles
            .Where(path => !IsStructureExcluded(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> GetInspectableTextFiles(
        IReadOnlyList<string> visibleFiles)
        => visibleFiles
            .Where(path => !IsStructureExcluded(path))
            .Where(path => SearchableRepositoryExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string FormatRepositoryTree(IReadOnlyList<string> visibleFiles)
    {
        var root = new TreeNode(string.Empty, isDirectory: true);

        foreach (var path in visibleFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isDirectory = i < segments.Length - 1;
                current = current.GetOrAddChild(segment, isDirectory);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(".");
        AppendTree(sb, root, string.Empty);
        return sb.ToString().TrimEnd();
    }

    public static IReadOnlyList<RepositoryEntry> ListVisibleRepositoryTreeEntries(
        IReadOnlyList<string> visibleFiles,
        string? relativePath)
    {
        var scope = NormalizeScope(relativePath);
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in visibleFiles)
        {
            if (!IsWithinScope(path, scope, out var remainder) || string.IsNullOrWhiteSpace(remainder))
            {
                continue;
            }

            var separatorIndex = remainder.IndexOf('/');
            var childSegment = separatorIndex >= 0 ? remainder[..separatorIndex] : remainder;
            var childPath = string.IsNullOrWhiteSpace(scope)
                ? childSegment
                : $"{scope}/{childSegment}";
            var isDirectory = separatorIndex >= 0;

            if (entries.TryGetValue(childPath, out var existing))
            {
                entries[childPath] = existing || isDirectory;
            }
            else
            {
                entries[childPath] = isDirectory;
            }
        }

        return entries
            .Select(pair => new RepositoryEntry(pair.Key, pair.Value))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IReadOnlyList<FileSearchHit>> SearchVisibleRepositoryFilesAsync(
        string repositoryPath,
        IReadOnlyList<string> inspectableFiles,
        string query,
        string? relativePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<FileSearchHit>();
        }

        var scope = NormalizeScope(relativePath);
        var hits = new List<FileSearchHit>();

        foreach (var relativeFile in inspectableFiles
                     .Where(path => IsPathInScope(path, scope))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(repositoryPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            string content;
            try
            {
                content = await File.ReadAllTextAsync(fullPath, ct);
            }
            catch
            {
                continue;
            }

            var matchIndex = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                continue;
            }

            var snippetStart = Math.Max(0, matchIndex - 120);
            var snippetLength = Math.Min(320, content.Length - snippetStart);
            var snippet = content.Substring(snippetStart, snippetLength)
                .Replace("\r", string.Empty)
                .Replace("\n", " | ");

            hits.Add(new FileSearchHit(relativeFile, snippet));
            if (hits.Count >= MaxSearchHits)
            {
                break;
            }
        }

        return hits;
    }

    public static IReadOnlyList<string> GetFrameworkDocumentationFiles(string repositoryPath)
        => GetMarkdownFilesUnder(repositoryPath, Path.Combine("AI", "framework"));

    public static IReadOnlyList<string> GetSolutionDocumentationFiles(string repositoryPath, string targetCode)
        => GetMarkdownFilesUnder(repositoryPath, Path.Combine("AI", "solutions", targetCode));

    private static IReadOnlyList<string> GetMarkdownFilesUnder(string repositoryPath, string relativeRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Array.Empty<string>();
        }

        var fullRoot = Path.Combine(repositoryPath, relativeRoot);
        if (!Directory.Exists(fullRoot))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(fullRoot, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repositoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> RunGitRelativePathQueryAsync(
        string repositoryPath,
        string arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Array.Empty<string>();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var output = await outputTask;
            _ = await errorTask;

            if (process.ExitCode != 0)
            {
                return Array.Empty<string>();
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AppendTree(StringBuilder sb, TreeNode node, string indent)
    {
        var orderedChildren = node.Children
            .OrderByDescending(child => child.IsDirectory)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < orderedChildren.Length; i++)
        {
            var child = orderedChildren[i];
            var isLast = i == orderedChildren.Length - 1;
            var marker = isLast ? "\\-- " : "|-- ";

            sb.Append(indent);
            sb.Append(marker);
            sb.AppendLine(child.Name);

            if (child.IsDirectory)
            {
                AppendTree(sb, child, indent + (isLast ? "    " : "|   "));
            }
        }
    }

    private static bool IsStructureExcluded(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.StartsWith("AI/contracts/", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("AI/framework/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim();

    private static string? NormalizeScope(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? null
            : NormalizePath(relativePath);

    private static bool IsWithinScope(string path, string? scope, out string remainder)
    {
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(scope))
        {
            remainder = NormalizePath(path);
            return true;
        }

        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Equals(scope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!normalizedPath.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        remainder = normalizedPath[(scope.Length + 1)..];
        return true;
    }

    private static bool IsPathInScope(string path, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return true;
        }

        return path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)
               || path.Equals(scope, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TreeNode
    {
        private readonly Dictionary<string, TreeNode> _children = new(StringComparer.OrdinalIgnoreCase);

        public TreeNode(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; }
        public bool IsDirectory { get; }
        public IEnumerable<TreeNode> Children => _children.Values;

        public TreeNode GetOrAddChild(string name, bool isDirectory)
        {
            if (_children.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var child = new TreeNode(name, isDirectory);
            _children[name] = child;
            return child;
        }
    }
}
