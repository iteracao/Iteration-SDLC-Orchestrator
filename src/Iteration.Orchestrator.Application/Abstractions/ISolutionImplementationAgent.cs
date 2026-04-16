namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionImplementationAgent
{
    Task<SolutionImplementationResult> ImplementAsync(
        SolutionImplementationRequest request,
        AgentDefinition agentDefinition,
        CancellationToken ct);
}
