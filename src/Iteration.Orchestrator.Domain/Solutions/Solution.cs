namespace Iteration.Orchestrator.Domain.Solutions;

public sealed class Solution
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string ProfileCode { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;

    private Solution() { }

    public Solution(string name, string description, string profileCode)
    {
        Update(name, description);
        ProfileCode = profileCode.Trim();
    }

    public void Update(string name, string description)
    {
        Name = name.Trim();
        Description = description.Trim();
    }
}
