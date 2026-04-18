using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionBridge
{
    Task<SolutionSnapshot> GetSolutionSnapshotAsync(SolutionTarget target, CancellationToken ct);
    Task<IReadOnlyList<RepositoryEntry>> ListRepositoryTreeAsync(SolutionTarget target, string? relativePath, CancellationToken ct);
    Task<IReadOnlyList<FileSearchHit>> SearchFilesAsync(SolutionTarget target, string query, string? relativePath, CancellationToken ct);
    Task<string> ReadFileAsync(SolutionTarget target, string relativePath, CancellationToken ct);
}