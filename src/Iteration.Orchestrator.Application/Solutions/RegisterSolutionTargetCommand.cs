using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.Application.Solutions;

public sealed record RegisterSolutionTargetCommand(
    string Code,
    string Name,
    string RepositoryPath,
    string MainSolutionFile,
    string ProfileCode,
    string SolutionOverlayCode);

public sealed class RegisterSolutionTargetHandler
{
    private readonly IAppDbContext _db;

    public RegisterSolutionTargetHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> HandleAsync(RegisterSolutionTargetCommand command, CancellationToken ct)
    {
        var entity = new SolutionTarget(
            command.Code,
            command.Name,
            command.RepositoryPath,
            command.MainSolutionFile,
            command.ProfileCode,
            command.SolutionOverlayCode);

        _db.SolutionTargets.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity.Id;
    }
}
