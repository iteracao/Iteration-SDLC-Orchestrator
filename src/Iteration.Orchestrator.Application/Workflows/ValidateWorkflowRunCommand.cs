using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed record ValidateWorkflowRunCommand(Guid WorkflowRunId);

public sealed class ValidateWorkflowRunHandler
{
    private readonly IAppDbContext _db;

    public ValidateWorkflowRunHandler(IAppDbContext db)
    {
        _db = db;
    }

    public async Task HandleAsync(ValidateWorkflowRunCommand command, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == command.WorkflowRunId, ct)
            ?? throw new InvalidOperationException("Workflow run not found.");

        if (!WorkflowLifecycleCatalog.CanValidate(run.Status))
        {
            throw new InvalidOperationException("Workflow run is not awaiting validation.");
        }

        await using var transaction = await _db.BeginTransactionAsync(ct);

        run.Validate(BuildValidatedStage(run.CurrentStage));

        if (run.RequirementId.HasValue)
        {
            var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == run.RequirementId.Value, ct)
                ?? throw new InvalidOperationException("Requirement not found for workflow run.");

            var nextStatus = WorkflowLifecycleCatalog.GetValidatedRequirementStatus(run.WorkflowCode);
            if (!string.IsNullOrWhiteSpace(nextStatus))
            {
                requirement.AdvanceLifecycle(run.Id, nextStatus);
            }
        }

        if (run.BacklogItemId.HasValue && string.Equals(run.WorkflowCode, "implement-solution-change", StringComparison.OrdinalIgnoreCase))
        {
            var backlogItem = await _db.BacklogItems.FirstOrDefaultAsync(x => x.Id == run.BacklogItemId.Value, ct)
                ?? throw new InvalidOperationException("Backlog item not found for workflow run.");

            backlogItem.MarkValidated();
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string BuildValidatedStage(string currentStage)
    {
        if (string.IsNullOrWhiteSpace(currentStage))
        {
            return "validated";
        }

        const string awaitingSuffix = "-awaiting-validation";
        return currentStage.EndsWith(awaitingSuffix, StringComparison.OrdinalIgnoreCase)
            ? $"{currentStage[..^awaitingSuffix.Length]}-validated"
            : $"{currentStage}-validated";
    }
}
