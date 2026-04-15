namespace Iteration.Orchestrator.Infrastructure.AI;

public interface IOllamaService
{
    Task<string> GenerateAsync(string prompt, string? model = null, CancellationToken ct = default);
}