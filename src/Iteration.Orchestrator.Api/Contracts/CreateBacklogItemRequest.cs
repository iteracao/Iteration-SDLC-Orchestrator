namespace Iteration.Orchestrator.Api.Contracts;

public sealed class CreateBacklogItemRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid TargetSolutionId { get; set; }
    public string WorkflowCode { get; set; } = "analyze-request";
    public string Priority { get; set; } = "Medium";
}
