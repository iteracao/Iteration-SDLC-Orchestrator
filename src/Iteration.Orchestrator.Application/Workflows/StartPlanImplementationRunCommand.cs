namespace Iteration.Orchestrator.Application.Workflows;

public sealed record StartPlanImplementationRunCommand(
    Guid RequirementId,
    string RequestedBy);
