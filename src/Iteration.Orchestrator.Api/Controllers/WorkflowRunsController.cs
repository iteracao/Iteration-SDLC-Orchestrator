using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Solutions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/workflow-runs")]
public sealed class WorkflowRunsController : ControllerBase
{
    [HttpPost("setup-solution")]
    public async Task<IActionResult> SetupSolution(
        [FromBody] SetupSolutionRequest request,
        [FromServices] SetupSolutionHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new SetupSolutionCommand(
                request.SolutionId,
                request.Code,
                request.Name,
                request.Description,
                request.RepositoryPath,
                request.MainSolutionFile,
                request.ProfileCode,
                request.SolutionOverlayCode,
                request.RemoteRepositoryUrl,
                request.RequestedBy),
            ct);

        return Ok(new
        {
            id = result.WorkflowRunId,
            result.SolutionId,
            result.KnowledgeRoot,
            result.RepositoryCreated,
            result.GitInitialized,
            result.RemoteConfigured,
            result.CreatedDocuments,
            result.ExistingDocuments
        });
    }

    [HttpPost("analyze-request")]
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
