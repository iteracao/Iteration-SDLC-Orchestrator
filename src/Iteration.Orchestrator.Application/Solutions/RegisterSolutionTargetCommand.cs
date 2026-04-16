namespace Iteration.Orchestrator.Application.Solutions;

public sealed record RegisterSolutionTargetCommand(
    string Code,
    string Name,
    string Description,
    string RepositoryPath,
    string MainSolutionFile,
    string ProfileCode,
    string? SolutionOverlayCode);