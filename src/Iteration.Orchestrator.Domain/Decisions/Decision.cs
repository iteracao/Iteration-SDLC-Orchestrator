namespace Iteration.Orchestrator.Domain.Decisions;

public sealed class Decision
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TargetSolutionId { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public Guid? BacklogItemId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string DecisionType { get; private set; } = "technical";
    public string Status { get; private set; } = "proposed";
    public string? Rationale { get; private set; }
    public string ConsequencesJson { get; private set; } = "[]";
    public string AlternativesConsideredJson { get; private set; } = "[]";
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;

    private Decision() { }

    public Decision(
        Guid targetSolutionId,
        Guid? workflowRunId,
        Guid? backlogItemId,
        string title,
        string summary,
        string decisionType,
        string status,
        string? rationale,
        string consequencesJson,
        string alternativesConsideredJson,
        DateTime createdAtUtc)
    {
        TargetSolutionId = targetSolutionId;
        WorkflowRunId = workflowRunId;
        BacklogItemId = backlogItemId;
        Title = title.Trim();
        Summary = summary.Trim();
        DecisionType = string.IsNullOrWhiteSpace(decisionType) ? "technical" : decisionType.Trim();
        Status = string.IsNullOrWhiteSpace(status) ? "proposed" : status.Trim();
        Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale.Trim();
        ConsequencesJson = string.IsNullOrWhiteSpace(consequencesJson) ? "[]" : consequencesJson;
        AlternativesConsideredJson = string.IsNullOrWhiteSpace(alternativesConsideredJson) ? "[]" : alternativesConsideredJson;
        CreatedAtUtc = createdAtUtc;
    }
}
