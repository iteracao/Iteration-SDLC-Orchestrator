namespace Iteration.Orchestrator.Domain.Requirements;

public sealed class Requirement
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TargetSolutionId { get; private set; }
    public Guid? OriginatingBacklogItemId { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public Guid? ParentRequirementId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string RequirementType { get; private set; } = "functional";
    public string Source { get; private set; } = "user";
    public string Status { get; private set; } = RequirementLifecycleStatus.Pending;
    public string Priority { get; private set; } = "medium";
    public string AcceptanceCriteriaJson { get; private set; } = "[]";
    public string ConstraintsJson { get; private set; } = "[]";
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; private set; }

    private Requirement() { }

    public Requirement(
        Guid targetSolutionId,
        Guid? originatingBacklogItemId,
        Guid? workflowRunId,
        Guid? parentRequirementId,
        string title,
        string description,
        string requirementType,
        string source,
        string status,
        string priority,
        string acceptanceCriteriaJson,
        string constraintsJson,
        DateTime createdAtUtc,
        DateTime? updatedAtUtc)
    {
        TargetSolutionId = targetSolutionId;
        OriginatingBacklogItemId = originatingBacklogItemId;
        WorkflowRunId = workflowRunId;
        ParentRequirementId = parentRequirementId;
        Title = title.Trim();
        Description = description.Trim();
        RequirementType = string.IsNullOrWhiteSpace(requirementType) ? "functional" : requirementType.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? "user" : source.Trim();
        Status = RequirementLifecycleStatus.Normalize(status);
        Priority = string.IsNullOrWhiteSpace(priority) ? "medium" : priority.Trim().ToLowerInvariant();
        AcceptanceCriteriaJson = string.IsNullOrWhiteSpace(acceptanceCriteriaJson) ? "[]" : acceptanceCriteriaJson;
        ConstraintsJson = string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void UpdateFromAnalysis(
        Guid? workflowRunId,
        string title,
        string description,
        string requirementType,
        string source,
        string status,
        string priority,
        string acceptanceCriteriaJson,
        string constraintsJson,
        DateTime updatedAtUtc)
    {
        WorkflowRunId = workflowRunId;
        Title = title.Trim();
        Description = description.Trim();
        RequirementType = string.IsNullOrWhiteSpace(requirementType) ? RequirementType : requirementType.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? Source : source.Trim();
        Status = string.IsNullOrWhiteSpace(status) ? Status : RequirementLifecycleStatus.Normalize(status);
        Priority = string.IsNullOrWhiteSpace(priority) ? Priority : priority.Trim().ToLowerInvariant();
        AcceptanceCriteriaJson = string.IsNullOrWhiteSpace(acceptanceCriteriaJson) ? "[]" : acceptanceCriteriaJson;
        ConstraintsJson = string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void AttachWorkflowRun(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateDetails(
        string title,
        string description,
        string requirementType,
        string source,
        string priority)
    {
        Title = title.Trim();
        Description = description.Trim();
        RequirementType = string.IsNullOrWhiteSpace(requirementType) ? RequirementType : requirementType.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? Source : source.Trim();
        Priority = string.IsNullOrWhiteSpace(priority) ? Priority : priority.Trim().ToLowerInvariant();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AdvanceLifecycle(Guid workflowRunId, string status)
    {
        WorkflowRunId = workflowRunId;
        Status = RequirementLifecycleStatus.Normalize(status);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void EnterAwaitingDecision(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = RequirementLifecycleStatus.AwaitingDecision;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Commit()
    {
        if (!string.Equals(Status, RequirementLifecycleStatus.AwaitingDecision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requirement must be awaiting a final decision before it can be completed.");
        }

        Status = RequirementLifecycleStatus.Completed;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Cancel()
    {
        var normalizedStatus = RequirementLifecycleStatus.Normalize(Status);
        if (string.Equals(normalizedStatus, RequirementLifecycleStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pending requirements cannot be cancelled.");
        }

        if (string.Equals(normalizedStatus, RequirementLifecycleStatus.Completed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedStatus, RequirementLifecycleStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requirement is already finalized.");
        }

        Status = RequirementLifecycleStatus.Cancelled;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
