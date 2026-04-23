using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class WorkflowRunTerminalStateCoordinator
{
    private readonly IAppDbContext _db;
    private readonly IWorkflowRunLogStore _logs;

    public WorkflowRunTerminalStateCoordinator(IAppDbContext db, IWorkflowRunLogStore logs)
    {
        _db = db;
        _logs = logs;
    }

    public async Task EnsureTerminalStateAsync(
        Guid workflowRunId,
        bool cancellationRequested,
        string failureCode,
        string message,
        CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == workflowRunId, ct);
        if (run is null || IsTerminal(run.Status))
        {
            return;
        }

        var formattedReason = WorkflowFailureCatalog.Format(failureCode, message);

        if (cancellationRequested)
        {
            run.Cancel(BuildCancelledStage(run.CurrentStage), formattedReason);
        }
        else
        {
            run.Fail(BuildFailedStage(run.CurrentStage), formattedReason);
        }

        var activeTaskRuns = await _db.AgentTaskRuns
            .Where(x => x.WorkflowRunId == workflowRunId
                && (x.Status == AgentTaskStatus.Pending || x.Status == AgentTaskStatus.Running))
            .ToListAsync(ct);

        foreach (var taskRun in activeTaskRuns)
        {
            taskRun.Fail(formattedReason);
        }

        await _db.SaveChangesAsync(ct);

        await _logs.AppendSectionAsync(workflowRunId, cancellationRequested ? "Cancelled" : "Error", CancellationToken.None);
        await _logs.AppendKeyValuesAsync(workflowRunId, "Terminal state repair", new Dictionary<string, string?>
        {
            ["WorkflowRunId"] = workflowRunId.ToString(),
            ["FailureCode"] = failureCode,
            ["TerminalStatus"] = cancellationRequested ? WorkflowRunStatus.Cancelled.ToString() : WorkflowRunStatus.Error.ToString(),
            ["Message"] = message
        }, CancellationToken.None);
    }

    public async Task<int> RecoverOrphanedRunsAsync(string failureCode, string message, CancellationToken ct)
    {
        var runs = await _db.WorkflowRuns
            .Where(x => x.Status == WorkflowRunStatus.Pending || x.Status == WorkflowRunStatus.Running)
            .ToListAsync(ct);

        foreach (var run in runs)
        {
            await EnsureTerminalStateAsync(run.Id, false, failureCode, message, ct);
        }

        return runs.Count;
    }

    private static bool IsTerminal(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Completed
            or WorkflowRunStatus.Cancelled
            or WorkflowRunStatus.Error
            or WorkflowRunStatus.Validated;

    private static string BuildCancelledStage(string currentStage)
    {
        if (string.IsNullOrWhiteSpace(currentStage))
        {
            return "cancelled";
        }

        return currentStage.EndsWith("-cancelled", StringComparison.OrdinalIgnoreCase)
            ? currentStage
            : $"{currentStage}-cancelled";
    }

    private static string BuildFailedStage(string currentStage)
    {
        if (string.IsNullOrWhiteSpace(currentStage))
        {
            return "error";
        }

        return currentStage.EndsWith("-error", StringComparison.OrdinalIgnoreCase)
            ? currentStage
            : $"{currentStage}-error";
    }
}
