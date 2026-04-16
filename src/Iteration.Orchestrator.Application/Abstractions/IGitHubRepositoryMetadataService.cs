namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record GitHubRepositoryMetadata(
    string Owner,
    string Name,
    string DefaultBranch,
    bool IsPrivate);

public interface IGitHubRepositoryMetadataService
{
    Task<GitHubRepositoryMetadata> GetMetadataAsync(string remoteRepositoryUrl, CancellationToken ct);
}
