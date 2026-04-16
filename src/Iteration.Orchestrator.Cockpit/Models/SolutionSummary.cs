namespace Iteration.Orchestrator.Cockpit.Models;

public sealed class SolutionSummary
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MainSolutionFile { get; set; } = string.Empty;
    public string ProfileCode { get; set; } = string.Empty;
    public string SolutionOverlayCode { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
}
