using System.Diagnostics;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.Infrastructure.Solutions;

public sealed class FileSystemSolutionSetupService : ISolutionSetupService
{
    private static readonly (string RelativePath, string Content)[] DefaultDocuments =
    [
        ("context/overview.md", "# Solution Overview\n\n## Purpose\n\n## Scope\n\n## Current State\n\n## Notes\n"),
        ("business/business-rules.md", "# Business Rules\n\n## Purpose\n\n## Canonical Rules\n\n## Exceptions\n\n## Open Clarifications\n"),
        ("business/workflows.md", "# Business Workflows\n\n## Purpose\n\n## Main Workflows\n\n## Exceptions\n\n## Open Clarifications\n"),
        ("architecture/architecture-overview.md", "# Architecture Overview\n\n## Purpose\n\n## Main Components\n\n## Key Constraints\n\n## Notes\n"),
        ("architecture/module-map.md", "# Module Map\n\n## Purpose\n\n## Current Modules\n\n## Planned Modules\n\n## Known Drift\n"),
        ("history/decisions.md", "# Decisions\n\n## Active Decisions\n\n## Superseded Decisions\n"),
        ("history/open-questions.md", "# Open Questions\n\n## Open\n\n## Resolved\n"),
        ("history/known-gaps.md", "# Known Gaps\n\n## Current Gaps\n\n## Risks\n"),
        ("analysis/latest-analysis.md", "# Latest Analysis\n\n## Summary\n\n## Impacted Areas\n\n## Risks\n\n## Assumptions\n\n## Recommended Next Steps\n")
    ];

    public async Task<SolutionSetupResult> SetupAsync(SolutionSetupRequest request, CancellationToken ct)
    {
        var repositoryCreated = false;
        if (!Directory.Exists(request.RepositoryRoot))
        {
            Directory.CreateDirectory(request.RepositoryRoot);
            repositoryCreated = true;
        }

        var copiedEntries = await CopyOverlaySourceAsync(request, ct);

        var gitInitialized = EnsureGitRepository(request.RepositoryRoot);
        var remoteConfigured = ConfigureRemoteOrigin(request.RepositoryRoot, request.RemoteRepositoryUrl);
        var solutionFileCreated = await EnsureMainSolutionFileAsync(request, ct);

        var knowledgeRoot = Path.Combine(request.RepositoryRoot, "AI", "solutions", request.SolutionCode.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(knowledgeRoot);

        var createdDocuments = new List<string>();
        var existingDocuments = new List<string>();

        foreach (var (relativePath, content) in DefaultDocuments)
        {
            var fullPath = Path.Combine(knowledgeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            if (File.Exists(fullPath))
            {
                existingDocuments.Add(relativePath);
                continue;
            }

            await File.WriteAllTextAsync(fullPath, content, ct);
            createdDocuments.Add(relativePath);
        }

        return new SolutionSetupResult(
            knowledgeRoot,
            repositoryCreated,
            gitInitialized,
            remoteConfigured,
            solutionFileCreated,
            createdDocuments,
            existingDocuments,
            copiedEntries);
    }

    private static async Task<IReadOnlyList<string>> CopyOverlaySourceAsync(SolutionSetupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OverlaySourceRepositoryRoot))
        {
            return [];
        }

        var sourceRoot = request.OverlaySourceRepositoryRoot.Trim();
        if (!Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException($"Overlay source repository '{sourceRoot}' was not found.");
        }

        if (string.Equals(Path.GetFullPath(sourceRoot), Path.GetFullPath(request.RepositoryRoot), StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var copiedEntries = new List<string>();
        await CopyDirectoryAsync(sourceRoot, request.RepositoryRoot, sourceRoot, copiedEntries, ct);
        return copiedEntries;
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationRoot,
        string sourceRoot,
        List<string> copiedEntries,
        CancellationToken ct)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (ShouldSkipDirectory(name))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, directory);
            var destinationDirectory = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(destinationDirectory);
            await CopyDirectoryAsync(directory, destinationRoot, sourceRoot, copiedEntries, ct);
        }

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (ShouldSkipFile(fileName))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationFile = Path.Combine(destinationRoot, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (File.Exists(destinationFile))
            {
                continue;
            }

            await using var sourceStream = File.OpenRead(file);
            await using var destinationStream = File.Create(destinationFile);
            await sourceStream.CopyToAsync(destinationStream, ct);
            copiedEntries.Add(relativePath.Replace('\\', '/'));
        }
    }

    private static bool ShouldSkipDirectory(string name)
        => string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipFile(string name)
        => string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> EnsureMainSolutionFileAsync(SolutionSetupRequest request, CancellationToken ct)
    {
        var fullPath = Path.Combine(request.RepositoryRoot, request.MainSolutionFile);
        if (File.Exists(fullPath))
        {
            return false;
        }

        var technicalName = Path.GetFileNameWithoutExtension(request.MainSolutionFile);
        var content = $"""
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
# Generated by Iteration SDLC Orchestrator
# Profile: {request.ProfileCode}
# Solution: {request.SolutionName}
# TechnicalName: {technicalName}
""";

        await File.WriteAllTextAsync(fullPath, content, ct);
        return true;
    }

    private static bool EnsureGitRepository(string repositoryRoot)
    {
        var gitFolder = Path.Combine(repositoryRoot, ".git");
        if (Directory.Exists(gitFolder))
        {
            return false;
        }

        RunGitCommand(repositoryRoot, "init");
        return true;
    }

    private static bool ConfigureRemoteOrigin(string repositoryRoot, string? remoteRepositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteRepositoryUrl))
        {
            return false;
        }

        var remotes = RunGitCommand(repositoryRoot, "remote", throwOnError: false);
        if (remotes.ExitCode == 0)
        {
            var remoteNames = remotes.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (remoteNames.Contains("origin", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        RunGitCommand(repositoryRoot, $"remote add origin \"{remoteRepositoryUrl.Trim()}\"");
        return true;
    }

    private static (int ExitCode, string Output) RunGitCommand(string workingDirectory, string arguments, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: git {arguments}\n{stderr}".Trim());
        }

        return (process.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
    }
}
