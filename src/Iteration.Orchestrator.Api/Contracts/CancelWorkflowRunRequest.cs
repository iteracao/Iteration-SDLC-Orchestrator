namespace Iteration.Orchestrator.Api.Contracts;

public sealed class CancelWorkflowRunRequest
{
    public Guid WorkflowRunId { get; set; }
    public bool TerminateRequirementLifecycle { get; set; }
    public string? Reason { get; set; }
}
