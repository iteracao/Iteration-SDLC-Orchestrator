namespace Iteration.Orchestrator.Domain.Questions;

public sealed class OpenQuestion
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TargetSolutionId { get; private set; }
    public Guid? RequirementId { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public Guid? BacklogItemId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string? Category { get; private set; }
    public string Status { get; private set; } = "open";
    public string? ResolutionNotes { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; private set; }

    private OpenQuestion() { }

    public OpenQuestion(
        Guid targetSolutionId,
        Guid? requirementId,
        Guid? workflowRunId,
        Guid? backlogItemId,
        string title,
        string description,
        string? category,
        string status,
        string? resolutionNotes,
        DateTime createdAtUtc,
        DateTime? resolvedAtUtc)
    {
        TargetSolutionId = targetSolutionId;
        RequirementId = requirementId;
        WorkflowRunId = workflowRunId;
        BacklogItemId = backlogItemId;
        Title = title.Trim();
        Description = description.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        Status = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim();
        ResolutionNotes = string.IsNullOrWhiteSpace(resolutionNotes) ? null : resolutionNotes.Trim();
        CreatedAtUtc = createdAtUtc;
        ResolvedAtUtc = resolvedAtUtc;
    }

    public void LinkRequirement(Guid? requirementId)
    {
        RequirementId = requirementId;
    }
}
