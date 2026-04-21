using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class WorkflowLifecycleService
{
    private readonly IAppDbContext _db;

    public WorkflowLifecycleService(IAppDbContext db)
    {
        _db = db;
    }

    public async Task EnsureNoBlockingRunAsync(
        Guid requirementId,
        string workflowCode,
        Guid? backlogItemId,
        CancellationToken ct)
    {
        var query = _db.WorkflowRuns
            .Where(x => x.RequirementId == requirementId)
            .Where(x => x.WorkflowCode == workflowCode);

        query = backlogItemId.HasValue
            ? query.Where(x => x.BacklogItemId == backlogItemId.Value)
            : query.Where(x => x.BacklogItemId == null);

        var blockingRunExists = await query.AnyAsync(
            x => x.Status == WorkflowRunStatus.Pending
                || x.Status == WorkflowRunStatus.Running
                || x.Status == WorkflowRunStatus.Completed,
            ct);

        if (blockingRunExists)
        {
            throw new InvalidOperationException(WorkflowLifecycleCatalog.BuildBlockingRunMessage(workflowCode));
        }
    }

    public async Task<bool> HasBlockingRunsAsync(Guid requirementId, CancellationToken ct)
    {
        return await _db.WorkflowRuns
            .Where(x => x.RequirementId == requirementId)
            .AnyAsync(x => x.Status == WorkflowRunStatus.Pending
                || x.Status == WorkflowRunStatus.Running
                || x.Status == WorkflowRunStatus.Completed, ct);
    }


    public async Task<bool> HasRunningRunAsync(Guid requirementId, CancellationToken ct)
    {
        return await _db.WorkflowRuns
            .Where(x => x.RequirementId == requirementId)
            .AnyAsync(x => x.Status == WorkflowRunStatus.Running, ct);
    }

    public async Task<WorkflowRun> GetLatestValidatedRunAsync(Guid requirementId, string workflowCode, CancellationToken ct)
    {
        return await _db.WorkflowRuns
            .Where(x => x.RequirementId == requirementId)
            .Where(x => x.WorkflowCode == workflowCode)
            .Where(x => x.Status == WorkflowRunStatus.Validated)
            .OrderByDescending(x => x.CompletedUtc ?? x.StartedUtc)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Validated workflow run not found for '{workflowCode}'.");
    }
}
