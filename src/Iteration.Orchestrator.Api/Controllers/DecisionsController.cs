using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solution-targets/{targetSolutionId:guid}/decisions")]
public sealed class DecisionsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid targetSolutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var items = await db.Decisions
            .Where(x => x.TargetSolutionId == targetSolutionId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(items);
    }
}
