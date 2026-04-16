namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionAnalystAgent
{
    Task<SolutionAnalysisResult> AnalyzeAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        CancellationToken ct);
}
