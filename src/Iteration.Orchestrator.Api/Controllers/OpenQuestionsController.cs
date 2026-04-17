using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solution-targets/{targetSolutionId:guid}/open-questions")]
public sealed class OpenQuestionsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid targetSolutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var items = await db.OpenQuestions
            .Where(x => x.TargetSolutionId == targetSolutionId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(items);
    }
}
