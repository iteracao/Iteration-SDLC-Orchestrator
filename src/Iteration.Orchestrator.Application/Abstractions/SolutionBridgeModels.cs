namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record RepositoryEntry(string RelativePath, bool IsDirectory);

public sealed record FileSearchHit(string RelativePath, string Snippet);

public sealed record SolutionSnapshot(
    string RepositoryPath,
    IReadOnlyList<string> SolutionFiles,
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> WebProjects,
    IReadOnlyList<string> ApiProjects,
    IReadOnlyList<string> MajorFolders);
