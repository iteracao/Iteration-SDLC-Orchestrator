namespace Iteration.Orchestrator.Domain.Workflows;

public sealed class WorkflowRun
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? RequirementId { get; private set; }
    public Guid? BacklogItemId { get; private set; }
    public Guid TargetSolutionId { get; private set; }
    public string WorkflowCode { get; private set; } = string.Empty;
    public WorkflowRunStatus Status { get; private set; } = WorkflowRunStatus.Pending;
    public string CurrentStage { get; private set; } = "pending";
    public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; private set; }
    public string RequestedBy { get; private set; } = "system";
    public string? FailureReason { get; private set; }

    private WorkflowRun() { }

    public WorkflowRun(Guid? requirementId, Guid? backlogItemId, Guid targetSolutionId, string workflowCode, string requestedBy)
    {
        RequirementId = requirementId;
        BacklogItemId = backlogItemId;
        TargetSolutionId = targetSolutionId;
        WorkflowCode = workflowCode.Trim();
        RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "system" : requestedBy.Trim();
    }

    public void Start(string stage)
    {
        if (Status != WorkflowRunStatus.Pending)
        {
            throw new InvalidOperationException("Only pending workflow runs can start.");
        }

        Status = WorkflowRunStatus.Running;
        CurrentStage = stage;
        FailureReason = null;
    }

    public void Complete(string stage)
    {
        if (Status != WorkflowRunStatus.Running)
        {
            throw new InvalidOperationException("Only running workflow runs can complete.");
        }

        Status = WorkflowRunStatus.Completed;
        CurrentStage = stage;
        CompletedUtc = DateTime.UtcNow;
        FailureReason = null;
    }

    public void Fail(string stage, string reason)
    {
        if (Status == WorkflowRunStatus.Validated || Status == WorkflowRunStatus.Cancelled)
        {
            throw new InvalidOperationException("Validated or cancelled workflow runs cannot fail.");
        }

        Status = WorkflowRunStatus.Error;
        CurrentStage = stage;
        FailureReason = reason;
        CompletedUtc = DateTime.UtcNow;
    }

    public void Validate(string stage)
    {
        if (Status != WorkflowRunStatus.Completed)
        {
            throw new InvalidOperationException("Only completed workflow runs can be validated.");
        }

        Status = WorkflowRunStatus.Validated;
        CurrentStage = stage;
        CompletedUtc = DateTime.UtcNow;
        FailureReason = null;
    }

    public void Cancel(string stage, string? reason = null)
    {
        if (Status != WorkflowRunStatus.Pending
            && Status != WorkflowRunStatus.Running
            && Status != WorkflowRunStatus.Completed
            && Status != WorkflowRunStatus.Error)
        {
            throw new InvalidOperationException("Workflow run cannot be cancelled from its current state.");
        }

        Status = WorkflowRunStatus.Cancelled;
        CurrentStage = stage;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? FailureReason : reason.Trim();
        CompletedUtc = DateTime.UtcNow;
    }
}
