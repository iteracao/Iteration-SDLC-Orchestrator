using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;
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
        var title = command.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Requirement title is required.");
        }

        var solutionExists = await _db.SolutionTargets.AnyAsync(
            x => x.Id == command.TargetSolutionId,
            ct);

        if (!solutionExists)
        {
            throw new InvalidOperationException("Target solution not found.");
        }

        var hasBlockingRuns = await _db.WorkflowRuns.AnyAsync(
            x => x.TargetSolutionId == command.TargetSolutionId
                 && (x.Status == WorkflowRunStatus.Pending
                     || x.Status == WorkflowRunStatus.Running
                     || x.Status == WorkflowRunStatus.Completed),
            ct);

        if (hasBlockingRuns)
        {
            throw new InvalidOperationException("Requirements cannot be created while the selected solution has an active workflow run.");
        }

        await using var transaction = await _db.BeginTransactionAsync(ct);

        var entity = new Requirement(
            command.TargetSolutionId,
            null,
            null,
            null,
            title,
            command.Description,
            string.IsNullOrWhiteSpace(command.RequirementType) ? "functional" : command.RequirementType,
            string.IsNullOrWhiteSpace(command.Source) ? "user" : command.Source,
            RequirementLifecycleStatus.Pending,
            string.IsNullOrWhiteSpace(command.Priority) ? "medium" : command.Priority,
            "[]",
            "[]",
            DateTime.UtcNow,
            null);

        _db.Requirements.Add(entity);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return entity.Id;
    }
}