namespace Iteration.Orchestrator.Api.Contracts;

public sealed class SetupSolutionRequest
{
    public Guid? SolutionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MainSolutionFile { get; set; } = string.Empty;
    public string ProfileCode { get; set; } = string.Empty;
    public string TargetCode { get; set; } = "dev";
    public Guid? OverlayTargetId { get; set; }
    public string? RemoteRepositoryUrl { get; set; }
    public string RequestedBy { get; set; } = "rui";
}
