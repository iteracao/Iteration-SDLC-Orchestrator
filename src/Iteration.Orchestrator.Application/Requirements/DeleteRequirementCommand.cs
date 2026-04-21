using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record DeleteRequirementCommand(Guid RequirementId);

public sealed class DeleteRequirementHandler
{
    private readonly IAppDbContext _db;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public DeleteRequirementHandler(IAppDbContext db, WorkflowLifecycleService workflowLifecycle)
    {
        _db = db;
        _workflowLifecycle = workflowLifecycle;
    }

    public async Task HandleAsync(DeleteRequirementCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == command.RequirementId, ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        if (!string.Equals(requirement.Status, RequirementLifecycleStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only new pending requirements can be deleted.");
        }

        if (await _workflowLifecycle.HasBlockingRunsAsync(requirement.Id, ct))
        {
            throw new InvalidOperationException("Requirement cannot be deleted while it has pending, running, or awaiting-validation workflow runs.");
        }

        var hasWorkflowHistory = await _db.WorkflowRuns.AnyAsync(x => x.RequirementId == requirement.Id, ct);
        if (hasWorkflowHistory)
        {
            throw new InvalidOperationException("Requirement cannot be deleted after workflow history already exists.");
        }

        var hasBacklogItems = await _db.BacklogItems.AnyAsync(x => x.RequirementId == requirement.Id, ct);
        if (hasBacklogItems)
        {
            throw new InvalidOperationException("Requirement cannot be deleted after backlog work already exists.");
        }

        _db.Requirements.Remove(requirement);
        await _db.SaveChangesAsync(ct);
    }
}
