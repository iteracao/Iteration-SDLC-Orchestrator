using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Abstractions;

public interface ISolutionDocumentationSetupAgent
{
    Task<SolutionDocumentationSetupResult> RunAsync(
        SolutionDocumentationSetupRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        CancellationToken ct);
}
