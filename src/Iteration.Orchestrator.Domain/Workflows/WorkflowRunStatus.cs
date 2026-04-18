namespace Iteration.Orchestrator.Domain.Workflows;

public enum WorkflowRunStatus
{
    Pending = 1,
    Running = 2,
    CompletedValidated = 3,
    Error = 4,
    Cancelled = 5,
    CompletedAwaitingValidation = 6
}
