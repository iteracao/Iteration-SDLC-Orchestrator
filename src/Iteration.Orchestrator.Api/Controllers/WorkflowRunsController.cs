using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Api.Contracts;
using Iteration.Orchestrator.Application.Solutions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Domain.Backlog;
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
        try
        {
            var result = await handler.HandleAsync(
                new SetupSolutionCommand(
                    request.SolutionId,
                    request.Name,
                    request.Description,
                    request.RepositoryPath,
                    request.MainSolutionFile,
                    request.ProfileCode,
                    request.TargetCode,
                    request.OverlayTargetId,
                    request.RemoteRepositoryUrl,
                    request.RequestedBy),
                ct);

            return Ok(new
            {
                id = result.WorkflowRunId,
                result.SolutionId,
                result.NextWorkflowCode,
                result.KnowledgeRoot,
                result.TargetStorageCode,
                result.TargetCode,
                result.ProfileCode,
                result.OverlaySolutionName,
                result.OverlayTargetCode,
                result.RepositoryCreated,
                result.GitInitialized,
                result.RemoteConfigured,
                result.SolutionFileCreated,
                result.CreatedDocuments,
                result.ExistingDocuments,
                result.CopiedEntries
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("analyze-request")]
    public async Task<IActionResult> StartAnalyze(
        [FromBody] StartAnalyzeRunRequest request,
        [FromServices] StartAnalyzeSolutionRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            var id = await handler.HandleAsync(
                new StartAnalyzeSolutionRunCommand(request.RequirementId, request.RequestedBy), ct);

            return Ok(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("design-solution-change")]
    public async Task<IActionResult> StartDesign(
        [FromBody] StartDesignRunRequest request,
        [FromServices] StartDesignSolutionRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            var id = await handler.HandleAsync(
                new StartDesignSolutionRunCommand(request.RequirementId, request.RequestedBy), ct);

            return Ok(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("plan-implementation")]
    public async Task<IActionResult> StartPlan(
        [FromBody] StartPlanRunRequest request,
        [FromServices] StartPlanImplementationRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            var id = await handler.HandleAsync(
                new StartPlanImplementationRunCommand(request.RequirementId, request.RequestedBy), ct);

            return Ok(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("implement-solution-change")]
    public async Task<IActionResult> StartImplementation(
        [FromBody] StartImplementationRunRequest request,
        [FromServices] StartImplementSolutionChangeRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            var id = await handler.HandleAsync(
                new StartImplementSolutionChangeRunCommand(request.BacklogItemId, request.RequestedBy), ct);

            return Ok(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/log")]
    public async Task<IActionResult> GetLog(
        Guid id,
        [FromServices] IWorkflowRunLogStore logs,
        CancellationToken ct)
    {
        var content = await logs.ReadAsync(id, ct);
        if (content is null)
        {
            return NotFound(new { message = "Workflow log not found." });
        }

        return Ok(new
        {
            workflowRunId = id,
            fileName = $"{id}.log",
            content
        });
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(
        [FromBody] ValidateWorkflowRunRequest request,
        [FromServices] ValidateWorkflowRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            await handler.HandleAsync(new ValidateWorkflowRunCommand(request.WorkflowRunId), ct);
            return Ok(new { id = request.WorkflowRunId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(
        [FromBody] CancelWorkflowRunRequest request,
        [FromServices] CancelWorkflowRunHandler handler,
        CancellationToken ct)
    {
        try
        {
            await handler.HandleAsync(
                new CancelWorkflowRunCommand(
                    request.WorkflowRunId,
                    request.TerminateRequirementLifecycle,
                    request.Reason),
                ct);

            return Ok(new { id = request.WorkflowRunId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] AppDbContext db,
        [FromQuery] Guid? targetSolutionId,
        CancellationToken ct)
    {
        var query = db.WorkflowRuns.AsQueryable();

        if (targetSolutionId.HasValue)
        {
            query = query.Where(x => x.TargetSolutionId == targetSolutionId.Value);
        }

        var runs = await query
            .OrderByDescending(x => x.StartedUtc)
            .Select(x => new
            {
                x.Id,
                x.RequirementId,
                x.BacklogItemId,
                x.TargetSolutionId,
                x.WorkflowCode,
                x.Status,
                x.CurrentStage,
                x.StartedUtc,
                x.CompletedUtc,
                x.RequestedBy,
                x.FailureReason
            })
            .ToListAsync(ct);

        return Ok(runs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(
        Guid id,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return NotFound();

        var analysisReport = await db.AnalysisReports.FirstOrDefaultAsync(x => x.WorkflowRunId == id, ct);
        var designReport = await db.DesignReports.FirstOrDefaultAsync(x => x.WorkflowRunId == id, ct);
        var planReport = await db.PlanReports.FirstOrDefaultAsync(x => x.WorkflowRunId == id, ct);
        var implementationReport = await db.ImplementationReports.FirstOrDefaultAsync(x => x.WorkflowRunId == id, ct);

        return Ok(new
        {
            run,
            analysisReport,
            designReport,
            planReport,
            implementationReport,
            report = implementationReport as object
                ?? planReport as object
                ?? designReport as object
                ?? analysisReport as object
        });
    }
}
