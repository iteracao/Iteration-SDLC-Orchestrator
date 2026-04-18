using System.Diagnostics;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Common;

internal static class RepositoryPromptInputDiscovery
{
    private static readonly string[] AllowedRepositoryExtensions =
    [
        ".cs",
        ".razor",
        ".csproj",
        ".sln",
        ".props",
        ".targets"
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

        var trackedFiles = await LoadGitTrackedFilesAsync(repositoryPath, ct);

        return trackedFiles
            .Where(path => !path.StartsWith("AI/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase))
            .Where(path => AllowedRepositoryExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static async Task<IReadOnlyList<string>> LoadGitTrackedFilesAsync(
        string repositoryPath,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-files",
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
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Replace('\\', '/'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}