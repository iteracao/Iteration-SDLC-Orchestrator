using System.Threading.Channels;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class InMemoryWorkflowExecutionQueue : IWorkflowExecutionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public ValueTask EnqueueAsync(Guid workflowRunId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(workflowRunId, ct);

    public ValueTask<Guid> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAsync(ct);
}
