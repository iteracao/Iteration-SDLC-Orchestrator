using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Requirements;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
public sealed class RequirementsController : ControllerBase
{
    [HttpPost("api/requirements")]
    public async Task<IActionResult> Create(
        [FromBody] CreateRequirementRequest request,
        [FromServices] CreateRequirementHandler handler,
        CancellationToken ct)
    {
        try
        {
            var id = await handler.HandleAsync(
                new CreateRequirementCommand(
                    request.TargetSolutionId,
                    request.Title,
                    request.Description,
                    request.RequirementType,
                    request.Source,
                    request.Priority),
                ct);

            return Ok(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/requirements/{requirementId:guid}/commit")]
    public async Task<IActionResult> Commit(
        Guid requirementId,
        [FromServices] CommitRequirementHandler handler,
        CancellationToken ct)
    {
        try
        {
            await handler.HandleAsync(new CommitRequirementCommand(requirementId), ct);
            return Ok(new { id = requirementId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("api/requirements/{requirementId:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid requirementId,
        [FromServices] CancelRequirementHandler handler,
        CancellationToken ct)
    {
        try
        {
            await handler.HandleAsync(new CancelRequirementCommand(requirementId), ct);
            return Ok(new { id = requirementId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("api/solution-targets/{targetSolutionId:guid}/requirements")]
    public async Task<IActionResult> ListByTargetSolution(
        Guid targetSolutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var items = await db.Requirements
            .Where(x => x.TargetSolutionId == targetSolutionId)
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
