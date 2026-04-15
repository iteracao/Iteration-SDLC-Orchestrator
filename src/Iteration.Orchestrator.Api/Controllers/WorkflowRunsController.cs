using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/workflow-runs")]
public sealed class WorkflowRunsController : ControllerBase
{
    [HttpPost("analyze-solution")]
    public async Task<IActionResult> StartAnalyze(
        [FromBody] StartAnalyzeRunRequest request,
        [FromServices] StartAnalyzeSolutionRunHandler handler,
        CancellationToken ct)
    {
        var id = await handler.HandleAsync(
            new StartAnalyzeSolutionRunCommand(request.BacklogItemId, request.RequestedBy), ct);

        return Ok(new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return NotFound();

        var report = await db.AnalysisReports.FirstOrDefaultAsync(x => x.WorkflowRunId == id, ct);

        return Ok(new
        {
            run,
            report
        });
    }
}
