using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Backlog;
using Iteration.Orchestrator.Domain.Common;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/backlog-items")]
public sealed class BacklogController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateBacklogItemRequest request,
        [FromServices] CreateBacklogItemHandler handler,
        CancellationToken ct)
    {
        var priority = Enum.TryParse<PriorityLevel>(request.Priority, true, out var parsed)
            ? parsed
            : PriorityLevel.Medium;

        var id = await handler.HandleAsync(
            new CreateBacklogItemCommand(
                request.TargetSolutionId,
                request.RequirementId,
                request.Title,
                request.Description,
                request.WorkflowCode,
                priority),
            ct);

        return Ok(new { id });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] AppDbContext db,
        [FromQuery] Guid? solutionId,
        [FromQuery] Guid? requirementId,
        CancellationToken ct)
    {
        var query = db.BacklogItems.AsQueryable();

        if (solutionId.HasValue)
        {
            query = query.Where(x => x.TargetSolutionId == solutionId.Value);
        }

        if (requirementId.HasValue)
        {
            query = query.Where(x => x.RequirementId == requirementId.Value);
        }

        var items = await query
            .OrderBy(x => x.RequirementId)
            .ThenBy(x => x.PlanningOrder)
            .ThenBy(x => x.CreatedUtc)
            .Select(x => new
            {
                x.Id,
                x.TargetSolutionId,
                x.RequirementId,
                x.PlanWorkflowRunId,
                x.PlanningOrder,
                x.Title,
                x.Description,
                x.WorkflowCode,
                Priority = x.Priority.ToString(),
                Status = x.Status.ToString(),
                x.CreatedUtc,
                x.UpdatedUtc
            })
            .ToListAsync(ct);

        return Ok(items);
    }
}
