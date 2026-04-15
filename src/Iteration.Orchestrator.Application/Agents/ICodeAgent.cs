namespace Iteration.Orchestrator.Application.Agents;

public interface ICodeAgent
{
    Task<string> AnalyzeCodeAsync(string code, CancellationToken ct = default);
}