namespace Iteration.Orchestrator.Application.Workflows;

public sealed record StartAnalyzeSolutionRunCommand(Guid RequirementId, string RequestedBy);
