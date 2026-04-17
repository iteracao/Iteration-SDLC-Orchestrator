using System.Threading.Channels;

namespace Iteration.Orchestrator.Application.Workflows;

public interface IWorkflowExecutionQueue
{
    ValueTask EnqueueAsync(Guid workflowRunId, CancellationToken ct = default);
    ValueTask<Guid> DequeueAsync(CancellationToken ct);
}
