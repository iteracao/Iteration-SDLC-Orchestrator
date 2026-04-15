namespace Iteration.Orchestrator.Domain.Solutions;

public sealed class SolutionTarget
{
    public Guid Id { get; private set; } = Guid.NewGuid();
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
        string code,
        string name,
        string repositoryPath,
        string mainSolutionFile,
        string profileCode,
        string solutionOverlayCode)
    {
        Code = code.Trim();
        Name = name.Trim();
        RepositoryPath = repositoryPath.Trim();
        MainSolutionFile = mainSolutionFile.Trim();
        ProfileCode = profileCode.Trim();
        SolutionOverlayCode = solutionOverlayCode.Trim();
    }
}
