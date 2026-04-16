namespace Iteration.Orchestrator.Application.Workflows;

public sealed record StartImplementSolutionChangeRunCommand(
    Guid BacklogItemId,
    string RequestedBy);
