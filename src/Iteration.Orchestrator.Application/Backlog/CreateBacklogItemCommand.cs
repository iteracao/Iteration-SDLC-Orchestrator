using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Backlog;

public sealed record CreateBacklogItemCommand(
    Guid TargetSolutionId,
    Guid? RequirementId,
    string Title,
    string Description,
    string WorkflowCode,
    PriorityLevel Priority);

public sealed class CreateBacklogItemHandler
{
    private readonly IAppDbContext _db;

    public CreateBacklogItemHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> HandleAsync(CreateBacklogItemCommand command, CancellationToken ct)
    {
        var solutionExists = await _db.Solutions.AnyAsync(x => x.Id == command.TargetSolutionId, ct);
        if (!solutionExists)
        {
            throw new InvalidOperationException("Target solution not found.");
        }

        if (command.RequirementId.HasValue)
        {
            var requirementExists = await _db.Requirements.AnyAsync(
                x => x.Id == command.RequirementId.Value && x.TargetSolutionId == command.TargetSolutionId, ct);

            if (!requirementExists)
            {
                throw new InvalidOperationException("Requirement not found for the target solution.");
            }
        }

        var entity = new BacklogItem(
            command.TargetSolutionId,
            command.RequirementId,
            command.Title,
            command.Description,
            command.WorkflowCode,
            command.Priority);

        _db.BacklogItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity.Id;
    }
}
