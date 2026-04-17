using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Requirements;

public sealed record CreateRequirementCommand(
    Guid TargetSolutionId,
    string Title,
    string Description,
    string RequirementType,
    string Source,
    string Priority);

public sealed class CreateRequirementHandler
{
    private readonly IAppDbContext _db;

    public CreateRequirementHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> HandleAsync(CreateRequirementCommand command, CancellationToken ct)
    {
        var solutionExists = await _db.SolutionTargets.AnyAsync(x => x.Id == command.TargetSolutionId, ct);
        if (!solutionExists)
        {
            throw new InvalidOperationException("Target solution not found.");
        }

        var entity = new Requirement(
            command.TargetSolutionId,
            null,
            null,
            null,
            command.Title,
            command.Description,
            string.IsNullOrWhiteSpace(command.RequirementType) ? "functional" : command.RequirementType,
            string.IsNullOrWhiteSpace(command.Source) ? "user" : command.Source,
            "submitted",
            string.IsNullOrWhiteSpace(command.Priority) ? "medium" : command.Priority,
            "[]",
            "[]",
            DateTime.UtcNow,
            null);

        _db.Requirements.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity.Id;
    }
}
