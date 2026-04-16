namespace Iteration.Orchestrator.Api.Contracts;

public sealed record StartPlanRunRequest(
    Guid RequirementId,
    string RequestedBy);
