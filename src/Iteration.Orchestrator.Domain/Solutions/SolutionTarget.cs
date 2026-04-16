namespace Iteration.Orchestrator.Domain.Solutions;

public sealed class SolutionTarget
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SolutionId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string RepositoryPath { get; private set; } = string.Empty;
    public string MainSolutionFile { get; private set; } = string.Empty;
    public string ProfileCode { get; private set; } = string.Empty;
    public string SolutionOverlayCode { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedUtc { get; private set; } = DateTime.UtcNow;

    private SolutionTarget() { }

    public SolutionTarget(
        Guid solutionId,
        string code,
        string name,
        string repositoryPath,
        string mainSolutionFile,
        string profileCode,
        string solutionOverlayCode)
    {
        SolutionId = solutionId;
        Code = code.Trim();
        Name = name.Trim();
        RepositoryPath = repositoryPath.Trim();
        MainSolutionFile = mainSolutionFile.Trim();
        ProfileCode = profileCode.Trim();
        SolutionOverlayCode = solutionOverlayCode.Trim();
    }
}
