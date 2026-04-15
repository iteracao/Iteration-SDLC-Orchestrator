namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record SolutionSetupRequest(
    string SolutionCode,
    string SolutionName,
    string RepositoryRoot,
    string MainSolutionFile,
    string ProfileCode,
    string SolutionOverlayCode,
    string? RemoteRepositoryUrl);

public sealed record SolutionSetupResult(
    string KnowledgeRoot,
    bool RepositoryCreated,
    bool GitInitialized,
    bool RemoteConfigured,
    IReadOnlyList<string> CreatedDocuments,
    IReadOnlyList<string> ExistingDocuments);

public interface ISolutionSetupService
{
    Task<SolutionSetupResult> SetupAsync(SolutionSetupRequest request, CancellationToken ct);
}
