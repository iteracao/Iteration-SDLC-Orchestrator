using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Solutions;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solutions")]
public sealed class SolutionsController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] RegisterSolutionRequest request,
        [FromServices] RegisterSolutionTargetHandler handler,
        CancellationToken ct)
    {
        var id = await handler.HandleAsync(new RegisterSolutionTargetCommand(
            request.Code,
            request.Name,
            request.RepositoryPath,
            request.MainSolutionFile,
            request.ProfileCode,
            request.SolutionOverlayCode), ct);

        return Ok(new { id });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromServices] AppDbContext db, CancellationToken ct)
    {
        var data = await db.SolutionTargets
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return Ok(data);
    }
}
