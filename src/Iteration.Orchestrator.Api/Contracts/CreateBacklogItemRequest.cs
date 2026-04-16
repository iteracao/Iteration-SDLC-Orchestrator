namespace Iteration.Orchestrator.Api.Contracts;

public sealed class CreateBacklogItemRequest
{
    public Guid TargetSolutionId { get; set; }
    public Guid? RequirementId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string WorkflowCode { get; set; } = "implement-solution-change";
    public string Priority { get; set; } = "Medium";
}
