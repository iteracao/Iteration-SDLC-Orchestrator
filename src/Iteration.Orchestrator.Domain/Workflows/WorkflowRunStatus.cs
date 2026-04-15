namespace Iteration.Orchestrator.Domain.Workflows;

public enum WorkflowRunStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}
