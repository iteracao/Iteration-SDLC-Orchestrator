namespace Iteration.Orchestrator.Application.Workflows;

public interface IWorkflowRunExecutor
{
    Task ExecuteAsync(Guid workflowRunId, CancellationToken ct);
}
