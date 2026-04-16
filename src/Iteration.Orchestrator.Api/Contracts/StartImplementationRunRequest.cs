namespace Iteration.Orchestrator.Api.Contracts;

public sealed class StartImplementationRunRequest
{
    public Guid BacklogItemId { get; set; }
    public string RequestedBy { get; set; } = "system";
}
