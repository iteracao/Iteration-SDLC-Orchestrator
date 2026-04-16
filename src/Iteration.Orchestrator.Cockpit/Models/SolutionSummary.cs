namespace Iteration.Orchestrator.Cockpit.Models;

public sealed class SolutionSummary
{
    public Guid SolutionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProfileCode { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public List<SolutionTargetSummary> Targets { get; set; } = [];
}

public sealed class SolutionTargetSummary
{
    public Guid Id { get; set; }
    public string StorageCode { get; set; } = string.Empty;
    public string TargetCode { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MainSolutionFile { get; set; } = string.Empty;
    public string OverlaySolutionName { get; set; } = string.Empty;
    public string OverlayTargetCode { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(TargetCode) ? StorageCode : TargetCode;
}
