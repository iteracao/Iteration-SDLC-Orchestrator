using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Solutions;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class WorkflowRunExecutor : IWorkflowRunExecutor
{
    private readonly IAppDbContext _db;
    private readonly StartAnalyzeSolutionRunHandler _analyzeHandler;
    private readonly SetupDocumentationHandler _setupDocumentationHandler;
    private readonly StartDesignSolutionRunHandler _designHandler;
    private readonly StartPlanImplementationRunHandler _planHandler;
    private readonly StartImplementSolutionChangeRunHandler _implementationHandler;
    private readonly IWorkflowRunLogStore _logs;
    private readonly WorkflowRunTerminalStateCoordinator _terminalStateCoordinator;

    public WorkflowRunExecutor(
        IAppDbContext db,
        StartAnalyzeSolutionRunHandler analyzeHandler,
        SetupDocumentationHandler setupDocumentationHandler,
        StartDesignSolutionRunHandler designHandler,
        StartPlanImplementationRunHandler planHandler,
        StartImplementSolutionChangeRunHandler implementationHandler,
        IWorkflowRunLogStore logs,
        WorkflowRunTerminalStateCoordinator terminalStateCoordinator)
    {
        _db = db;
        _analyzeHandler = analyzeHandler;
        _setupDocumentationHandler = setupDocumentationHandler;
        _designHandler = designHandler;
        _planHandler = planHandler;
        _implementationHandler = implementationHandler;
        _logs = logs;
        _terminalStateCoordinator = terminalStateCoordinator;
    }

    public async Task ExecuteAsync(Guid workflowRunId, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == workflowRunId, ct);
        if (run is null)
        {
            return;
        }

        await _logs.AppendLineAsync(workflowRunId, $"Workflow executor picked run for code '{run.WorkflowCode}'.", ct);

        try
        {
            switch (run.WorkflowCode)
            {
                case "analyze-request":
                    await _analyzeHandler.ExecuteAsync(workflowRunId, ct);
                    break;
                case "setup-documentation":
                    await _setupDocumentationHandler.ExecuteAsync(workflowRunId, ct);
                    break;
                case "design-solution-change":
                    await _designHandler.ExecuteAsync(workflowRunId, ct);
                    break;
                case "plan-implementation":
                    await _planHandler.ExecuteAsync(workflowRunId, ct);
                    break;
                case "implement-solution-change":
                    await _implementationHandler.ExecuteAsync(workflowRunId, ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported workflow code '{run.WorkflowCode}'.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var reason = "Workflow executor observed cancellation and stopped execution.";
            await _logs.AppendLineAsync(workflowRunId, reason, CancellationToken.None);
            await _terminalStateCoordinator.EnsureTerminalStateAsync(
                workflowRunId,
                cancellationRequested: true,
                WorkflowFailureCatalog.WorkflowExecutionCancelled,
                reason,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var failure = WorkflowFailureCatalog.Classify(ex);
            await _logs.AppendLineAsync(workflowRunId, "Workflow executor caught a top-level failure.", CancellationToken.None);
            await _logs.AppendKeyValuesAsync(workflowRunId, "Executor failure", new Dictionary<string, string?>
            {
                ["WorkflowRunId"] = workflowRunId.ToString(),
                ["WorkflowCode"] = run.WorkflowCode,
                ["FailureCode"] = failure.Code,
                ["ExceptionType"] = ex.GetType().Name,
                ["Message"] = failure.Message
            }, CancellationToken.None);
            await _logs.AppendBlockAsync(workflowRunId, "Executor exception", ex.ToString(), CancellationToken.None);
            await _terminalStateCoordinator.EnsureTerminalStateAsync(
                workflowRunId,
                cancellationRequested: false,
                failure.Code,
                failure.Message,
                CancellationToken.None);
            throw;
        }
        finally
        {
            await _terminalStateCoordinator.EnsureTerminalStateAsync(
                workflowRunId,
                cancellationRequested: ct.IsCancellationRequested,
                ct.IsCancellationRequested ? WorkflowFailureCatalog.WorkflowExecutionCancelled : WorkflowFailureCatalog.ExecutorExitedWithoutTerminalState,
                ct.IsCancellationRequested
                    ? "Workflow execution was cancelled before a terminal workflow state was persisted."
                    : "Workflow executor exited without reaching a terminal workflow state.",
                CancellationToken.None);
        }
    }
}
