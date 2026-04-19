using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class WorkflowRunExecutor : IWorkflowRunExecutor
{
    private readonly IAppDbContext _db;
    private readonly StartAnalyzeSolutionRunHandler _analyzeHandler;
    private readonly StartDesignSolutionRunHandler _designHandler;
    private readonly StartPlanImplementationRunHandler _planHandler;
    private readonly StartImplementSolutionChangeRunHandler _implementationHandler;
    private readonly IWorkflowRunLogStore _logs;

    public WorkflowRunExecutor(
        IAppDbContext db,
        StartAnalyzeSolutionRunHandler analyzeHandler,
        StartDesignSolutionRunHandler designHandler,
        StartPlanImplementationRunHandler planHandler,
        StartImplementSolutionChangeRunHandler implementationHandler,
        IWorkflowRunLogStore logs)
    {
        _db = db;
        _analyzeHandler = analyzeHandler;
        _designHandler = designHandler;
        _planHandler = planHandler;
        _implementationHandler = implementationHandler;
        _logs = logs;
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
            await _logs.AppendLineAsync(workflowRunId, "Workflow executor observed cancellation and stopped execution.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(workflowRunId, "Workflow executor caught a top-level failure.", CancellationToken.None);
            await _logs.AppendBlockAsync(workflowRunId, "Executor exception", ex.ToString(), CancellationToken.None);
            await MarkFailedAsync(run, ex.Message, ct);
            throw;
        }
    }

    private async Task MarkFailedAsync(Domain.Workflows.WorkflowRun run, string reason, CancellationToken ct)
    {
        if (run.Status != Domain.Workflows.WorkflowRunStatus.Pending
            && run.Status != Domain.Workflows.WorkflowRunStatus.Running)
        {
            return;
        }

        run.Fail(run.CurrentStage, reason);
        await _logs.AppendLineAsync(run.Id, $"Workflow marked as failed. Reason: {reason}", CancellationToken.None);

        if (run.BacklogItemId.HasValue && string.Equals(run.WorkflowCode, "implement-solution-change", StringComparison.OrdinalIgnoreCase))
        {
            var backlogItem = await _db.BacklogItems.FirstOrDefaultAsync(x => x.Id == run.BacklogItemId.Value, ct);
            backlogItem?.MarkImplementationError();
        }

        await _db.SaveChangesAsync(CancellationToken.None);
    }
}
