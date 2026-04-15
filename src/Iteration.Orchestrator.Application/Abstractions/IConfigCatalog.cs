namespace Iteration.Orchestrator.Application.Abstractions;

public interface IConfigCatalog
{
    Task<WorkflowDefinition> GetWorkflowAsync(string workflowCode, CancellationToken ct);
    Task<AgentDefinition> GetAgentAsync(string agentCode, CancellationToken ct);
    Task<ProfileDefinition> GetProfileAsync(string profileCode, CancellationToken ct);
    Task<SolutionOverlayDefinition> GetSolutionOverlayAsync(string solutionCode, CancellationToken ct);
}
