using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionBridge
{
    Task<SolutionSnapshot> GetSolutionSnapshotAsync(SolutionTarget target, CancellationToken ct);
    Task<IReadOnlyList<RepositoryEntry>> ListRepositoryTreeAsync(SolutionTarget target, CancellationToken ct);
    Task<IReadOnlyList<FileSearchHit>> SearchFilesAsync(SolutionTarget target, string query, CancellationToken ct);
    Task<string> ReadFileAsync(SolutionTarget target, string relativePath, CancellationToken ct);
}
