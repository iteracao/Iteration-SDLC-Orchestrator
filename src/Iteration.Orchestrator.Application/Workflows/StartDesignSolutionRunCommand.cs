namespace Iteration.Orchestrator.Application.Workflows;

public sealed record StartDesignSolutionRunCommand(Guid RequirementId, string RequestedBy);
