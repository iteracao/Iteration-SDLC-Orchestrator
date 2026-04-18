using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionAnalystAgent
{
    Task<SolutionAnalysisResult> AnalyzeAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        CancellationToken ct);
}
