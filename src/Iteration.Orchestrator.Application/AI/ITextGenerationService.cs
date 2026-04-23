namespace Iteration.Orchestrator.Application.AI;

public interface ITextGenerationService
{
    Task<string> GenerateAsync(string prompt, string? model = null, CancellationToken ct = default);
}
