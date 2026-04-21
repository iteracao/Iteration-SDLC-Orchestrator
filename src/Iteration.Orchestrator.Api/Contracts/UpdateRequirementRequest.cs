namespace Iteration.Orchestrator.Api.Contracts;

public sealed class UpdateRequirementRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequirementType { get; set; } = "functional";
    public string Source { get; set; } = "user";
    public string Priority { get; set; } = "medium";
}
