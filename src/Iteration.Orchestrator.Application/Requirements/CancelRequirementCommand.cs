using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Workflows;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record CancelRequirementCommand(Guid RequirementId);

public sealed class CancelRequirementHandler
{
    private readonly IAppDbContext _db;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public CancelRequirementHandler(IAppDbContext db, WorkflowLifecycleService workflowLifecycle)
    {
        _db = db;
        _workflowLifecycle = workflowLifecycle;
    }

    public async Task HandleAsync(CancelRequirementCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == command.RequirementId, ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        var normalizedStatus = RequirementLifecycleStatus.Normalize(requirement.Status);
        if (string.Equals(normalizedStatus, RequirementLifecycleStatus.Completed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedStatus, RequirementLifecycleStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requirement is already finalized.");
        }

        if (await _workflowLifecycle.HasRunningRunAsync(requirement.Id, ct))
        {
            throw new InvalidOperationException("Requirement cannot be cancelled while a workflow is running.");
        }

        await using var transaction = await _db.BeginTransactionAsync(ct);

        requirement.Cancel();

        if (requirement.WorkflowRunId.HasValue)
        {
            var currentRun = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == requirement.WorkflowRunId.Value, ct);
            if (currentRun is not null && currentRun.Status != WorkflowRunStatus.Validated && currentRun.Status != WorkflowRunStatus.Cancelled)
            {
                currentRun.Cancel($"{currentRun.CurrentStage}-cancelled", "Requirement cancelled.");
            }
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
