namespace Iteration.Orchestrator.Application.Abstractions;

public interface IArtifactStore
{
    Task SaveTextAsync(Guid runId, string fileName, string content, CancellationToken ct);
    Task DeleteRunAsync(Guid runId, CancellationToken ct);
}
