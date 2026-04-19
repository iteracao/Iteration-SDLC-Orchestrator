using System.Collections.Concurrent;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class InMemoryWorkflowRunCancellationRegistry : IWorkflowRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registrations = new();

    public CancellationToken Register(Guid workflowRunId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_registrations.TryAdd(workflowRunId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"Workflow run '{workflowRunId}' is already registered for cancellation.");
        }

        return cts.Token;
    }

    public bool Cancel(Guid workflowRunId)
    {
        if (!_registrations.TryGetValue(workflowRunId, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    public void Complete(Guid workflowRunId)
    {
        if (_registrations.TryRemove(workflowRunId, out var cts))
        {
            cts.Dispose();
        }
    }
}
