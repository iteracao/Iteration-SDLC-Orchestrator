using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solutions")]
public sealed class SolutionsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromServices] AppDbContext db, CancellationToken ct)
    {
        var data = await db.SolutionTargets
            .Join(
                db.Solutions,
                target => target.SolutionId,
                solution => solution.Id,
                (target, solution) => new
                {
                    target.Id,
                    SolutionId = solution.Id,
                    target.Code,
                    solution.Name,
                    solution.Description,
                    target.RepositoryPath,
                    target.MainSolutionFile,
                    target.ProfileCode,
                    target.SolutionOverlayCode,
                    target.IsActive,
                    target.CreatedUtc
                })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return Ok(data);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] AppDbContext db, CancellationToken ct)
    {
        var solution = await db.SolutionTargets
            .Where(x => x.Id == id)
            .Join(
                db.Solutions,
                target => target.SolutionId,
                solution => solution.Id,
                (target, solution) => new
                {
                    target.Id,
                    SolutionId = solution.Id,
                    target.Code,
                    solution.Name,
                    solution.Description,
                    target.RepositoryPath,
                    target.MainSolutionFile,
                    target.ProfileCode,
                    target.SolutionOverlayCode,
                    target.IsActive,
                    target.CreatedUtc
                })
            .FirstOrDefaultAsync(ct);

        return solution is null ? NotFound() : Ok(solution);
    }
}
