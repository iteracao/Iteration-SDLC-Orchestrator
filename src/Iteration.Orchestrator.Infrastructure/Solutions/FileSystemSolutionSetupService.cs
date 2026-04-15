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

        var gitInitialized = EnsureGitRepository(request.RepositoryRoot);
        var remoteConfigured = ConfigureRemoteOrigin(request.RepositoryRoot, request.RemoteRepositoryUrl);

        var knowledgeRoot = Path.Combine(request.RepositoryRoot, "ai", "solutions", request.SolutionCode);
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
            createdDocuments,
            existingDocuments);
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
