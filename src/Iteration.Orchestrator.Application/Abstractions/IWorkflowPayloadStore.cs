namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record WorkflowInputPayload(
    Guid WorkflowRunId,
    string WorkflowCode,
    string InputPayloadJson);

public interface IWorkflowPayloadStore
{
    Task<WorkflowInputPayload> GetInputAsync(Guid workflowRunId, CancellationToken ct);
    Task SaveOutputAsync(Guid workflowRunId, string outputPayloadJson, CancellationToken ct);
}
