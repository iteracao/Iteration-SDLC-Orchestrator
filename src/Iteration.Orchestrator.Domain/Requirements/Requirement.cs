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
    public string Status { get; private set; } = "submitted";
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
        Status = string.IsNullOrWhiteSpace(status) ? "submitted" : status.Trim();
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
        Status = string.IsNullOrWhiteSpace(status) ? Status : status.Trim();
        Priority = string.IsNullOrWhiteSpace(priority) ? Priority : priority.Trim().ToLowerInvariant();
        AcceptanceCriteriaJson = string.IsNullOrWhiteSpace(acceptanceCriteriaJson) ? "[]" : acceptanceCriteriaJson;
        ConstraintsJson = string.IsNullOrWhiteSpace(constraintsJson) ? "[]" : constraintsJson;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void MarkUnderAnalysis(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "under-analysis";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkAnalyzed(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "analyzed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkAnalysisFailed(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "analysis-failed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkUnderDesign(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "under-design";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkDesigned(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "designed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkDesignFailed(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "design-failed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkUnderPlanning(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "under-planning";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkPlanned(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "planned";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkPlanningFailed(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "planning-failed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkImplementing(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "under-implementation";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkAwaitingImplementationValidation(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "awaiting-implementation-validation";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkImplementationFailed(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "implementation-failed";
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkCanceled(Guid workflowRunId)
    {
        WorkflowRunId = workflowRunId;
        Status = "canceled";
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
