namespace Iteration.Orchestrator.Api.Contracts;

public sealed class RegisterSolutionRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MainSolutionFile { get; set; } = string.Empty;
    public string ProfileCode { get; set; } = string.Empty;
    public string SolutionOverlayCode { get; set; } = string.Empty;
    public string? RemoteRepositoryUrl { get; set; }
    public string RequestedBy { get; set; } = "rui";
}
