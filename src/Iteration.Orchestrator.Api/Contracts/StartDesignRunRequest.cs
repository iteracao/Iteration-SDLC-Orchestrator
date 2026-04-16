namespace Iteration.Orchestrator.Api.Contracts;

public sealed class StartDesignRunRequest
{
    public Guid RequirementId { get; set; }
    public string RequestedBy { get; set; } = "rui";
}
