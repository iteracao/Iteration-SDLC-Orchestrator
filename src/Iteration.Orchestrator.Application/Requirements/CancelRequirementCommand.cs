using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Workflows;
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

        if (await _workflowLifecycle.HasBlockingRunsAsync(requirement.Id, ct))
        {
            throw new InvalidOperationException("Requirement cannot be cancelled while it has pending, running, or awaiting-validation workflow runs.");
        }

        await using var transaction = await _db.BeginTransactionAsync(ct);

        requirement.Cancel();

        var backlogItems = await _db.BacklogItems
            .Where(x => x.RequirementId == requirement.Id)
            .ToListAsync(ct);

        foreach (var backlogItem in backlogItems)
        {
            if (backlogItem.Status != BacklogItemStatus.Validated && backlogItem.Status != BacklogItemStatus.Canceled)
            {
                backlogItem.MarkCanceled();
            }
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
