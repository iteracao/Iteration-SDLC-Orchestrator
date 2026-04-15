using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Backlog;
using Iteration.Orchestrator.Domain.Common;

namespace Iteration.Orchestrator.Application.Backlog;

public sealed record CreateBacklogItemCommand(
    string Title,
    string Description,
    Guid TargetSolutionId,
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
        var entity = new BacklogItem(
            command.Title,
            command.Description,
            command.TargetSolutionId,
            command.WorkflowCode,
            command.Priority);

        _db.BacklogItems.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity.Id;
    }
}
