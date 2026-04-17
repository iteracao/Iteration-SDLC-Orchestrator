using Iteration.Orchestrator.Application.Workflows;

namespace Iteration.Orchestrator.Api.Background;

public sealed class WorkflowExecutionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowExecutionQueue _queue;
    private readonly ILogger<WorkflowExecutionBackgroundService> _logger;

    public WorkflowExecutionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IWorkflowExecutionQueue queue,
        ILogger<WorkflowExecutionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid workflowRunId;
            try
            {
                workflowRunId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IWorkflowRunExecutor>();
                await executor.ExecuteAsync(workflowRunId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background workflow execution failed for run {WorkflowRunId}.", workflowRunId);
            }
        }
    }
}
