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
        await RecoverOrphanedRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid workflowRunId;
            try
            {
                workflowRunId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Background workflow execution loop stopping due to host cancellation.");
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IWorkflowRunExecutor>();
                var terminalStateCoordinator = scope.ServiceProvider.GetRequiredService<WorkflowRunTerminalStateCoordinator>();
                var executionToken = _cancellationRegistry.Register(workflowRunId, stoppingToken);

                try
                {
                    await executor.ExecuteAsync(workflowRunId, executionToken);
                }
                finally
                {
                    _cancellationRegistry.Complete(workflowRunId);
                    await terminalStateCoordinator.EnsureTerminalStateAsync(
                        workflowRunId,
                        cancellationRequested: executionToken.IsCancellationRequested,
                        executionToken.IsCancellationRequested ? WorkflowFailureCatalog.HostShutdownCancelled : WorkflowFailureCatalog.ExecutorExitedWithoutTerminalState,
                        executionToken.IsCancellationRequested
                            ? "Background workflow execution stopped because the host requested cancellation."
                            : "Background workflow execution finished without a terminal workflow state.",
                        CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Background workflow execution stopped for run {WorkflowRunId} because the host is shutting down.", workflowRunId);
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

    private async Task RecoverOrphanedRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var terminalStateCoordinator = scope.ServiceProvider.GetRequiredService<WorkflowRunTerminalStateCoordinator>();
        var recoveredCount = await terminalStateCoordinator.RecoverOrphanedRunsAsync(
            WorkflowFailureCatalog.OrphanedActiveRunRecovered,
            "Recovered an orphaned workflow run that was left pending or running when background execution restarted.",
            ct);

        if (recoveredCount > 0)
        {
            _logger.LogWarning("Recovered {RecoveredCount} orphaned workflow run(s) during background service startup.", recoveredCount);
        }
    }
}
