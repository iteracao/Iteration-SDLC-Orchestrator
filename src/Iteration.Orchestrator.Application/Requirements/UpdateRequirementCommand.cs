using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record UpdateRequirementCommand(
    Guid RequirementId,
    string Title,
    string Description,
    string RequirementType,
    string Source,
    string Priority);

public sealed class UpdateRequirementHandler
{
    private readonly IAppDbContext _db;
    private readonly WorkflowLifecycleService _workflowLifecycle;

    public UpdateRequirementHandler(IAppDbContext db, WorkflowLifecycleService workflowLifecycle)
    {
        _db = db;
        _workflowLifecycle = workflowLifecycle;
    }

    public async Task HandleAsync(UpdateRequirementCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Title))
        {
            throw new InvalidOperationException("Requirement title is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Description))
        {
            throw new InvalidOperationException("Requirement description is required.");
        }

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
            throw new InvalidOperationException("Requirement cannot be edited while it has pending, running, or awaiting-validation workflow runs.");
        }

        requirement.UpdateDetails(
            command.Title,
            command.Description,
            command.RequirementType,
            command.Source,
            command.Priority);

        await _db.SaveChangesAsync(ct);
    }
}
