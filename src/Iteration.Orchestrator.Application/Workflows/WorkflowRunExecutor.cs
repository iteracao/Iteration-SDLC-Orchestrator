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

    public WorkflowRunExecutor(
        IAppDbContext db,
        StartAnalyzeSolutionRunHandler analyzeHandler,
        StartDesignSolutionRunHandler designHandler,
        StartPlanImplementationRunHandler planHandler,
        StartImplementSolutionChangeRunHandler implementationHandler)
    {
        _db = db;
        _analyzeHandler = analyzeHandler;
        _designHandler = designHandler;
        _planHandler = planHandler;
        _implementationHandler = implementationHandler;
    }

    public async Task ExecuteAsync(Guid workflowRunId, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == workflowRunId, ct);
        if (run is null)
        {
            return;
        }

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
        catch (Exception ex)
        {
            await MarkFailedAsync(run, ex.Message, ct);
            throw;
        }
    }

    private async Task MarkFailedAsync(Domain.Workflows.WorkflowRun run, string reason, CancellationToken ct)
    {
        if (run.Status == Domain.Workflows.WorkflowRunStatus.Succeeded || run.Status == Domain.Workflows.WorkflowRunStatus.Failed)
        {
            return;
        }

        run.Fail(run.CurrentStage, reason);

        if (run.RequirementId.HasValue)
        {
            var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == run.RequirementId.Value, ct);
            if (requirement is not null)
            {
                switch (run.WorkflowCode)
                {
                    case "analyze-request":
                        requirement.MarkAnalysisFailed(run.Id);
                        break;
                    case "design-solution-change":
                        requirement.MarkDesignFailed(run.Id);
                        break;
                    case "plan-implementation":
                        requirement.MarkPlanningFailed(run.Id);
                        break;
                    case "implement-solution-change":
                        requirement.MarkImplementationFailed(run.Id);
                        break;
                }
            }
        }

        if (run.BacklogItemId.HasValue && string.Equals(run.WorkflowCode, "implement-solution-change", StringComparison.OrdinalIgnoreCase))
        {
            var backlogItem = await _db.BacklogItems.FirstOrDefaultAsync(x => x.Id == run.BacklogItemId.Value, ct);
            backlogItem?.MarkImplementationError();
        }

        await _db.SaveChangesAsync(CancellationToken.None);
    }
}
