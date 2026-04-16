using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed class RegisterSolutionTargetHandler
{
    private readonly IAppDbContext _db;

    public RegisterSolutionTargetHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> HandleAsync(RegisterSolutionTargetCommand command, CancellationToken ct)
    {
        var existing = await _db.SolutionTargets
            .FirstOrDefaultAsync(x => x.Code == command.Code, ct);

        if (existing is not null)
        {
            return existing.Id;
        }

        var solution = new Solution(
            command.Name,
            command.Description,
            command.ProfileCode);

        _db.Solutions.Add(solution);

        var target = new SolutionTarget(
            solution.Id,
            command.Code,
            command.Name,
            command.RepositoryPath,
            command.MainSolutionFile,
            command.ProfileCode,
            command.SolutionOverlayCode ?? string.Empty);

        _db.SolutionTargets.Add(target);

        await _db.SaveChangesAsync(ct);
        return target.Id;
    }
}