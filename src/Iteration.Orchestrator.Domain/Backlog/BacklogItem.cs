using Iteration.Orchestrator.Domain.Common;

namespace Iteration.Orchestrator.Domain.Backlog;

public sealed class BacklogItem
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TargetSolutionId { get; private set; }
    public Guid? RequirementId { get; private set; }
    public Guid? PlanWorkflowRunId { get; private set; }
    public int PlanningOrder { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string WorkflowCode { get; private set; } = string.Empty;
    public PriorityLevel Priority { get; private set; }
    public BacklogItemStatus Status { get; private set; } = BacklogItemStatus.NotImplemented;
    public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; private set; }

    private BacklogItem() { }

    public BacklogItem(
        Guid targetSolutionId,
        Guid? requirementId,
        string title,
        string description,
        string workflowCode,
        PriorityLevel priority)
        : this(targetSolutionId, requirementId, null, 0, title, description, workflowCode, priority)
    {
    }

    public BacklogItem(
        Guid targetSolutionId,
        Guid? requirementId,
        Guid? planWorkflowRunId,
        int planningOrder,
        string title,
        string description,
        string workflowCode,
        PriorityLevel priority)
    {
        TargetSolutionId = targetSolutionId;
        RequirementId = requirementId;
        PlanWorkflowRunId = planWorkflowRunId;
        PlanningOrder = planningOrder;
        Title = title.Trim();
        Description = description.Trim();
        WorkflowCode = workflowCode.Trim();
        Priority = priority;
        Status = BacklogItemStatus.NotImplemented;
    }

    public void MarkAwaitingValidation()
    {
        Status = BacklogItemStatus.AwaitingValidation;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void ResetToNotImplemented()
    {
        Status = BacklogItemStatus.NotImplemented;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void MarkImplementationError()
    {
        Status = BacklogItemStatus.ImplementationError;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void MarkValidated()
    {
        Status = BacklogItemStatus.Validated;
        UpdatedUtc = DateTime.UtcNow;
    }

    public void MarkCanceled()
    {
        Status = BacklogItemStatus.Canceled;
        UpdatedUtc = DateTime.UtcNow;
    }

    public bool CanStartImplementationAttempt()
        => Status == BacklogItemStatus.NotImplemented || Status == BacklogItemStatus.ImplementationError;
}
