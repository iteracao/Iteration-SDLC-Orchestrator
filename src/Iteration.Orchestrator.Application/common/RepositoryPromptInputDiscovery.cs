using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Workflows;

internal static class RepositoryPromptInputDiscovery
{
    public static Task<IReadOnlyList<string>> LoadRepositoryFilesAsync(
        SolutionTarget target,
        CancellationToken ct)
    {
        var repositoryPath = target.RepositoryPath;

        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var srcPath = Path.Combine(repositoryPath, "src");

        if (!Directory.Exists(srcPath))
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        var files = Directory
            .EnumerateFiles(srcPath, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(repositoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public static IReadOnlyList<string> GetRepositoryDocumentationFiles(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return Array.Empty<string>();
        }

        var aiSolutionsPath = Path.Combine(repositoryPath, "AI", "solutions");

        return Directory
            .EnumerateFiles(repositoryPath, "*.md", SearchOption.AllDirectories)
            .Where(path => !IsUnder(path, aiSolutionsPath))
            .Select(path => Path.GetRelativePath(repositoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        static bool IsUnder(string filePath, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                return false;
            }

            var fullFile = Path.GetFullPath(filePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullFile, fullRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
