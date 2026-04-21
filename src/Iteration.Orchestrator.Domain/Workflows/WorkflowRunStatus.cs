namespace Iteration.Orchestrator.Domain.Workflows;

public enum WorkflowRunStatus
{
    Pending = 1,
    Running = 2,
    Validated = 3,
    Error = 4,
    Cancelled = 5,
    Completed = 6
}
