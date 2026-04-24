namespace Iteration.Orchestrator.Application.Abstractions;

public interface IWorkflowRunLogStore
{
    Task AppendLineAsync(Guid workflowRunId, string message, CancellationToken ct);
    Task AppendSectionAsync(Guid workflowRunId, string title, CancellationToken ct);
    Task AppendKeyValuesAsync(Guid workflowRunId, string title, IReadOnlyDictionary<string, string?> values, CancellationToken ct);
    Task AppendBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct);
    Task<string?> ReadAsync(Guid workflowRunId, CancellationToken ct);
    Task AppendRawBlockAsync(Guid workflowRunId, string title, string content, CancellationToken ct);
    Task<string?> ReadRawAsync(Guid workflowRunId, CancellationToken ct);
}
