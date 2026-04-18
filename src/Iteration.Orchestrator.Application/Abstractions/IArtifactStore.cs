namespace Iteration.Orchestrator.Application.Abstractions;

public interface IArtifactStore
{
    Task SaveTextAsync(Guid runId, string fileName, string content, CancellationToken ct);
    Task<IReadOnlyList<string>> ListFilesAsync(Guid runId, CancellationToken ct);
    Task<string?> ReadTextAsync(Guid runId, string fileName, CancellationToken ct);
    Task DeleteRunAsync(Guid runId, CancellationToken ct);
}
