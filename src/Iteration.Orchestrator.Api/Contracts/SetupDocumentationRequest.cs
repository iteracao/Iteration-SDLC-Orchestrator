namespace Iteration.Orchestrator.Api.Contracts;

public sealed class SetupDocumentationRequest
{
    public Guid TargetSolutionId { get; set; }
    public string RequestedBy { get; set; } = "rui";
}
