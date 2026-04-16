namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionPlannerAgent
{
    Task<SolutionPlanResult> PlanAsync(
        SolutionPlanRequest request,
        AgentDefinition agentDefinition,
        CancellationToken ct);
}
