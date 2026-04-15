namespace Iteration.Orchestrator.Application.Workflows;

public sealed record StartAnalyzeSolutionRunCommand(Guid BacklogItemId, string RequestedBy);
