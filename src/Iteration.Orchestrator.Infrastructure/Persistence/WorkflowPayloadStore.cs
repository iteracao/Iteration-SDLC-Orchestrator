using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Infrastructure.Persistence;

public sealed class WorkflowPayloadStore : IWorkflowPayloadStore
{
    private readonly AppDbContext _db;

    public WorkflowPayloadStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<WorkflowInputPayload> GetInputAsync(Guid workflowRunId, CancellationToken ct)
    {
        var workflowRun = await _db.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == workflowRunId, ct)
            ?? throw new InvalidOperationException("Workflow run not found.");

        var taskRun = await _db.AgentTaskRuns
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .OrderByDescending(x => x.StartedUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Agent task run not found for workflow.");

        return new WorkflowInputPayload(
            workflowRunId,
            workflowRun.WorkflowCode,
            string.IsNullOrWhiteSpace(taskRun.InputPayloadJson) ? "{}" : taskRun.InputPayloadJson);
    }

    public async Task SaveOutputAsync(Guid workflowRunId, string outputPayloadJson, CancellationToken ct)
    {
        var taskRun = await _db.AgentTaskRuns
            .Where(x => x.WorkflowRunId == workflowRunId)
            .OrderByDescending(x => x.StartedUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Agent task run not found for workflow.");

        taskRun.SetOutputPayload(outputPayloadJson);
        await _db.SaveChangesAsync(ct);
    }
}
