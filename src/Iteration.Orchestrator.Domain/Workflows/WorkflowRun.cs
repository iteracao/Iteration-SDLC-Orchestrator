namespace Iteration.Orchestrator.Domain.Workflows;

public sealed class WorkflowRun
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid BacklogItemId { get; private set; }
    public Guid TargetSolutionId { get; private set; }
    public string WorkflowCode { get; private set; } = string.Empty;
    public WorkflowRunStatus Status { get; private set; } = WorkflowRunStatus.Pending;
    public string CurrentStage { get; private set; } = "pending";
    public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; private set; }
    public string RequestedBy { get; private set; } = "system";
    public string? FailureReason { get; private set; }

    private WorkflowRun() { }

    public WorkflowRun(Guid backlogItemId, Guid targetSolutionId, string workflowCode, string requestedBy)
    {
        BacklogItemId = backlogItemId;
        TargetSolutionId = targetSolutionId;
        WorkflowCode = workflowCode.Trim();
        RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "system" : requestedBy.Trim();
    }

    public void Start(string stage)
    {
        Status = WorkflowRunStatus.Running;
        CurrentStage = stage;
    }

    public void Succeed(string stage)
    {
        Status = WorkflowRunStatus.Succeeded;
        CurrentStage = stage;
        CompletedUtc = DateTime.UtcNow;
    }

    public void Fail(string stage, string reason)
    {
        Status = WorkflowRunStatus.Failed;
        CurrentStage = stage;
        FailureReason = reason;
        CompletedUtc = DateTime.UtcNow;
    }
}
