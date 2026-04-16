namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionDesignerAgent
{
    Task<SolutionDesignResult> DesignAsync(
        SolutionDesignRequest request,
        AgentDefinition agentDefinition,
        CancellationToken ct);
}
