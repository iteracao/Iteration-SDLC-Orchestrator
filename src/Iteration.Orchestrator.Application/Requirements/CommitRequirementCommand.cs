using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record CommitRequirementCommand(Guid RequirementId);

public sealed class CommitRequirementHandler
{
    private readonly IAppDbContext _db;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public CommitRequirementHandler(IAppDbContext db, WorkflowLifecycleService workflowLifecycle)
    {
        _db = db;
        _workflowLifecycle = workflowLifecycle;
    }

    public async Task HandleAsync(CommitRequirementCommand command, CancellationToken ct)
    {
        var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == command.RequirementId, ct)
            ?? throw new InvalidOperationException("Requirement not found.");

        if (!string.Equals(requirement.Status, RequirementLifecycleStatus.AwaitingDecision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requirement must be awaiting a final decision before it can be completed.");
        }

        if (await _workflowLifecycle.HasBlockingRunsAsync(requirement.Id, ct))
        {
            throw new InvalidOperationException("Requirement cannot be committed while it has pending, running, or completed workflow runs.");
        }

        requirement.Commit();
        await _db.SaveChangesAsync(ct);
    }
}
