using Iteration.Orchestrator.Domain.Common;

namespace Iteration.Orchestrator.Domain.Backlog;

public sealed class BacklogItem
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TargetSolutionId { get; private set; }
    public Guid? RequirementId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string WorkflowCode { get; private set; } = string.Empty;
    public PriorityLevel Priority { get; private set; }
    public BacklogItemStatus Status { get; private set; } = BacklogItemStatus.Draft;
    public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;

    private BacklogItem() { }

    public BacklogItem(
        Guid targetSolutionId,
        Guid? requirementId,
        string title,
        string description,
        string workflowCode,
        PriorityLevel priority)
    {
        TargetSolutionId = targetSolutionId;
        RequirementId = requirementId;
        Title = title.Trim();
        Description = description.Trim();
        WorkflowCode = workflowCode.Trim();
        Priority = priority;
        Status = BacklogItemStatus.Ready;
    }
}
