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
    [HttpGet]
    public async Task<IActionResult> List([FromServices] AppDbContext db, CancellationToken ct)
    {
        var data = await db.Solutions
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return Ok(data);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var solution = await db.Solutions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (solution is null) return NotFound();

        var target = await db.SolutionTargets.FirstOrDefaultAsync(x => x.SolutionId == id, ct);

        return Ok(new
        {
            solution,
            target
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] RegisterSolutionRequest request,
        [FromServices] SetupSolutionHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new SetupSolutionCommand(
            null,
            request.Code,
            request.Name,
            request.Description,
            request.RepositoryPath,
            request.MainSolutionFile,
            request.ProfileCode,
            request.SolutionOverlayCode,
            request.RemoteRepositoryUrl,
            request.RequestedBy), ct);

        return Ok(new
        {
            id = result.SolutionId,
            result.WorkflowRunId,
            result.KnowledgeRoot,
            result.RepositoryCreated,
            result.GitInitialized,
            result.RemoteConfigured,
            result.CreatedDocuments,
            result.ExistingDocuments
        });
    }
}
