using Iteration.Orchestrator.Domain.Common;

namespace Iteration.Orchestrator.Domain.Backlog;

public sealed class BacklogItem
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public Guid TargetSolutionId { get; private set; }
    public string WorkflowCode { get; private set; } = string.Empty;
    public PriorityLevel Priority { get; private set; }
    public BacklogItemStatus Status { get; private set; } = BacklogItemStatus.Draft;
    public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;

    private BacklogItem() { }

    public BacklogItem(
        string title,
        string description,
        Guid targetSolutionId,
        string workflowCode,
        PriorityLevel priority)
    {
        Title = title.Trim();
        Description = description.Trim();
        TargetSolutionId = targetSolutionId;
        WorkflowCode = workflowCode.Trim();
        Priority = priority;
        Status = BacklogItemStatus.Ready;
    }

    public void MarkInAnalysis() => Status = BacklogItemStatus.InAnalysis;
    public void MarkAnalysisCompleted() => Status = BacklogItemStatus.AnalysisCompleted;
    public void MarkFailed() => Status = BacklogItemStatus.Failed;
}
