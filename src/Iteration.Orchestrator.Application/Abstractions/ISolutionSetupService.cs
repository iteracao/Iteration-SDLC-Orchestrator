namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record SolutionSetupRequest(
    string SolutionCode,
    string SolutionName,
    string RepositoryRoot,
    string MainSolutionFile,
    string ProfileCode,
    string OverlaySolutionName,
    string OverlayTargetCode,
    string? OverlaySourceRepositoryRoot,
    string? RemoteRepositoryUrl);

public sealed record SolutionSetupResult(
    string KnowledgeRoot,
    bool RepositoryCreated,
    bool GitInitialized,
    bool RemoteConfigured,
    bool SolutionFileCreated,
    IReadOnlyList<string> CreatedDocuments,
    IReadOnlyList<string> ExistingDocuments,
    IReadOnlyList<string> CopiedEntries);

public interface ISolutionSetupService
{
    Task<SolutionSetupResult> SetupAsync(SolutionSetupRequest request, CancellationToken ct);
}
