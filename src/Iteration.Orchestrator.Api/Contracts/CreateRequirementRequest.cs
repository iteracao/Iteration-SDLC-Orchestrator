namespace Iteration.Orchestrator.Api.Contracts;

public sealed class CreateRequirementRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid TargetSolutionId { get; set; }
    public string Priority { get; set; } = "Medium";
    public string RequirementType { get; set; } = "functional";
    public string Source { get; set; } = "user";
    public string Status { get; set; } = "submitted";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public List<string> Constraints { get; set; } = [];
}
