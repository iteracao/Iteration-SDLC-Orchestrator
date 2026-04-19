using Iteration.Orchestrator.Application.Workflows;

namespace Iteration.Orchestrator.Api.Background;

public sealed class WorkflowExecutionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowExecutionQueue _queue;
    private readonly IWorkflowRunCancellationRegistry _cancellationRegistry;
    private readonly ILogger<WorkflowExecutionBackgroundService> _logger;

    public WorkflowExecutionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IWorkflowExecutionQueue queue,
        IWorkflowRunCancellationRegistry cancellationRegistry,
        ILogger<WorkflowExecutionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _cancellationRegistry = cancellationRegistry;
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
                var executionToken = _cancellationRegistry.Register(workflowRunId, stoppingToken);

                try
                {
                    await executor.ExecuteAsync(workflowRunId, executionToken);
                }
                finally
                {
                    _cancellationRegistry.Complete(workflowRunId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Background workflow execution cancelled for run {WorkflowRunId}.", workflowRunId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background workflow execution failed for run {WorkflowRunId}.", workflowRunId);
            }
        }
    }
}
