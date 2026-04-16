using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solutions/{solutionId:guid}/decisions")]
public sealed class DecisionsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid solutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var items = await db.Decisions
            .Where(x => x.TargetSolutionId == solutionId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(items);
    }
}
