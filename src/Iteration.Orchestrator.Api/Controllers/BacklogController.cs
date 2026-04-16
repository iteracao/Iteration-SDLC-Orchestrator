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
        var priority = Enum.Parse<PriorityLevel>(request.Priority, ignoreCase: true);

        var id = await handler.HandleAsync(new CreateBacklogItemCommand(
            request.Title,
            request.Description,
            request.TargetSolutionId,
            request.WorkflowCode,
            priority), ct);

        return Ok(new { id });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] AppDbContext db,
        [FromQuery] Guid? solutionId,
        CancellationToken ct)
    {
        var query = db.BacklogItems.AsQueryable();

        if (solutionId.HasValue)
        {
            query = query.Where(x => x.TargetSolutionId == solutionId.Value);
        }

        var data = await query
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(ct);

        return Ok(data);
    }
}
