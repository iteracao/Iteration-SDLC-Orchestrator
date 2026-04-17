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
        var solutions = await db.Solutions
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var targets = await db.SolutionTargets
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(ct);

        var data = solutions
            .Select(solution => new
            {
                SolutionId = solution.Id,
                solution.Name,
                solution.Description,
                solution.ProfileCode,
                CreatedUtc = solution.CreatedAtUtc,
                Targets = targets
                    .Where(target => target.SolutionId == solution.Id)
                    .Select(target => new
                    {
                        target.Id,
                        StorageCode = target.Code,
                        TargetCode = ExtractTargetCode(target.Code),
                        OverlaySolutionName = target.Name,
                        OverlayTargetCode = target.SolutionOverlayCode,
                        target.RepositoryPath,
                        target.MainSolutionFile,
                        target.IsActive,
                        target.CreatedUtc
                    })
                    .ToList()
            })
            .ToList();

        return Ok(data);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] AppDbContext db, CancellationToken ct)
    {
        var solution = await db.Solutions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (solution is null)
        {
            return NotFound();
        }

        var targets = await db.SolutionTargets
            .Where(x => x.SolutionId == id)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync(ct);

        return Ok(new
        {
            SolutionId = solution.Id,
            solution.Name,
            solution.Description,
            solution.ProfileCode,
            CreatedUtc = solution.CreatedAtUtc,
            Targets = targets.Select(target => new
            {
                target.Id,
                StorageCode = target.Code,
                TargetCode = ExtractTargetCode(target.Code),
                OverlaySolutionName = target.Name,
                OverlayTargetCode = target.SolutionOverlayCode,
                target.RepositoryPath,
                target.MainSolutionFile,
                target.IsActive,
                target.CreatedUtc
            })
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateSolutionRequest request,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var solution = await db.Solutions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (solution is null)
        {
            return NotFound();
        }

        var target = await db.SolutionTargets.FirstOrDefaultAsync(x => x.SolutionId == id, ct);
        if (target is null)
        {
            return NotFound();
        }

        if (!IsValidMainSolutionFile(request.MainSolutionFile))
        {
            return BadRequest(new { message = "Main solution file must use only A-Z, a-z, 0-9 and '.' and end with .sln." });
        }

        solution.Update(request.Name, request.Description);
        target.Update(
            target.Code,
            request.OverlaySolutionName ?? string.Empty,
            request.RepositoryPath,
            request.MainSolutionFile,
            target.ProfileCode,
            request.OverlayTargetCode ?? string.Empty);

        await db.SaveChangesAsync(ct);
        return await Get(id, db, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromServices] AppDbContext db, CancellationToken ct)
    {
        var solution = await db.Solutions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (solution is null)
        {
            return NotFound();
        }

        var targetIds = await db.SolutionTargets
            .Where(x => x.SolutionId == id)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var workflowRunIds = await db.WorkflowRuns
            .Where(x => targetIds.Contains(x.TargetSolutionId))
            .Select(x => x.Id)
            .ToListAsync(ct);

        if (workflowRunIds.Any())
        {
            var agentTaskRuns = await db.AgentTaskRuns
                .Where(x => workflowRunIds.Contains(x.WorkflowRunId))
                .ToListAsync(ct);

            if (agentTaskRuns.Any())
            {
                db.AgentTaskRuns.RemoveRange(agentTaskRuns);
            }
        }

        var targets = await db.SolutionTargets
            .Where(x => x.SolutionId == id)
            .ToListAsync(ct);

        if (targets.Any())
        {
            db.SolutionTargets.RemoveRange(targets);
        }

        db.Solutions.Remove(solution);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string ExtractTargetCode(string storageCode)
    {
        if (string.IsNullOrWhiteSpace(storageCode))
        {
            return string.Empty;
        }

        var slashIndex = storageCode.LastIndexOf('/');
        return slashIndex >= 0 ? storageCode[(slashIndex + 1)..] : storageCode;
    }

    private static bool IsValidMainSolutionFile(string value)
        => !string.IsNullOrWhiteSpace(value)
           && System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), "^[A-Za-z0-9.]+\\.sln$");

    public sealed class UpdateSolutionRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string RepositoryPath { get; set; } = string.Empty;
        public string MainSolutionFile { get; set; } = string.Empty;
        public string? OverlaySolutionName { get; set; }
        public string? OverlayTargetCode { get; set; }
    }
}
