namespace Iteration.Orchestrator.Domain.Backlog;

public enum BacklogItemStatus
{
    NotImplemented = 1,
    AwaitingValidation = 2,
    ImplementationError = 3,
    Validated = 4,
    Canceled = 5
}
