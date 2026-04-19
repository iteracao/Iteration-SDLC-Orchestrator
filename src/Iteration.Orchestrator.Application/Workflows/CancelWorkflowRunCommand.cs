using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed record CancelWorkflowRunCommand(
    Guid WorkflowRunId,
    bool TerminateRequirementLifecycle,
    string? Reason);

public sealed class CancelWorkflowRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IWorkflowRunCancellationRegistry _cancellationRegistry;

    public CancelWorkflowRunHandler(IAppDbContext db, IWorkflowRunCancellationRegistry cancellationRegistry)
    {
        _db = db;
        _cancellationRegistry = cancellationRegistry;
    }

    public async Task HandleAsync(CancelWorkflowRunCommand command, CancellationToken ct)
    {
        var run = await _db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == command.WorkflowRunId, ct)
            ?? throw new InvalidOperationException("Workflow run not found.");

        if (!WorkflowLifecycleCatalog.CanCancel(run.Status))
        {
            throw new InvalidOperationException("Workflow run cannot be cancelled from its current state.");
        }

        await using var transaction = await _db.BeginTransactionAsync(ct);

        var previousStatus = run.Status;
        run.Cancel(BuildCancelledStage(run.CurrentStage), command.Reason);

        if (run.RequirementId.HasValue && command.TerminateRequirementLifecycle)
        {
            var requirement = await _db.Requirements.FirstOrDefaultAsync(x => x.Id == run.RequirementId.Value, ct)
                ?? throw new InvalidOperationException("Requirement not found for workflow run.");

            requirement.Cancel();
        }

        if (run.BacklogItemId.HasValue && string.Equals(run.WorkflowCode, "implement-solution-change", StringComparison.OrdinalIgnoreCase))
        {
            var backlogItem = await _db.BacklogItems.FirstOrDefaultAsync(x => x.Id == run.BacklogItemId.Value, ct)
                ?? throw new InvalidOperationException("Backlog item not found for workflow run.");

            if (command.TerminateRequirementLifecycle)
            {
                backlogItem.MarkCanceled();
            }
            else if (previousStatus == WorkflowRunStatus.CompletedAwaitingValidation)
            {
                backlogItem.ResetToNotImplemented();
            }
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _cancellationRegistry.Cancel(command.WorkflowRunId);
    }

    private static string BuildCancelledStage(string currentStage)
    {
        if (string.IsNullOrWhiteSpace(currentStage))
        {
            return "cancelled";
        }

        return currentStage.EndsWith("-cancelled", StringComparison.OrdinalIgnoreCase)
            ? currentStage
            : $"{currentStage}-cancelled";
    }
}
