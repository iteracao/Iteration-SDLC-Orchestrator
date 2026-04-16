using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
public sealed class RequirementsController : ControllerBase
{
    [HttpGet("api/solutions/{solutionId:guid}/requirements")]
    public async Task<IActionResult> ListBySolution(
        Guid solutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var items = await db.Requirements
            .Where(x => x.TargetSolutionId == solutionId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("api/requirements/{requirementId:guid}")]
    public async Task<IActionResult> GetById(
        Guid requirementId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var item = await db.Requirements.FirstOrDefaultAsync(x => x.Id == requirementId, ct);
        return item is null ? NotFound() : Ok(item);
    }
}
