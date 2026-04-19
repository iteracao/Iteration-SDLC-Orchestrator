namespace Iteration.Orchestrator.Application.Workflows;

public interface IWorkflowRunCancellationRegistry
{
    CancellationToken Register(Guid workflowRunId, CancellationToken stoppingToken);
    bool Cancel(Guid workflowRunId);
    void Complete(Guid workflowRunId);
}
