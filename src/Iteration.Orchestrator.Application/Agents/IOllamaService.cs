namespace Iteration.Orchestrator.Application.AI;

public interface IOllamaService
{
    Task<string> GenerateAsync(string prompt, string? model = null, CancellationToken ct = default);
}