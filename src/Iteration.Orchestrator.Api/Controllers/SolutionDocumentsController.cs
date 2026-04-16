using Iteration.Orchestrator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/solutions/{solutionId:guid}/documents")]
public sealed class SolutionDocumentsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid solutionId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var target = await db.SolutionTargets
            .Where(x => x.SolutionId == solutionId)
            .Select(x => new { x.SolutionId, x.Code, x.RepositoryPath })
            .FirstOrDefaultAsync(ct);

        if (target is null)
        {
            return NotFound();
        }

        var root = BuildKnowledgeRoot(target.RepositoryPath, target.Code);
        if (!Directory.Exists(root))
        {
            return Ok(Array.Empty<object>());
        }

        var documents = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');

                return new
                {
                    key = relativePath,
                    name = BuildDisplayName(relativePath),
                    relativePath,
                    exists = true,
                    lastModifiedUtc = System.IO.File.GetLastWriteTimeUtc(path)
                };
            })
            .OrderBy(x => x.relativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(documents);
    }

    [HttpGet("content")]
    public async Task<IActionResult> GetContent(
        Guid solutionId,
        [FromServices] AppDbContext db,
        [FromQuery] string path,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Document path is required.");
        }

        var target = await db.SolutionTargets
            .Where(x => x.SolutionId == solutionId)
            .Select(x => new { x.SolutionId, x.Code, x.RepositoryPath })
            .FirstOrDefaultAsync(ct);

        if (target is null)
        {
            return NotFound();
        }

        var knowledgeRoot = BuildKnowledgeRoot(target.RepositoryPath, target.Code);
        var normalizedRelativePath = path.Replace('\\', '/').TrimStart('/');
        var requestedPath = Path.GetFullPath(Path.Combine(knowledgeRoot, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(knowledgeRoot);

        if (!requestedPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid document path.");
        }

        if (!System.IO.File.Exists(requestedPath))
        {
            return NotFound();
        }

        var content = await System.IO.File.ReadAllTextAsync(requestedPath, ct);
        var relativePath = Path.GetRelativePath(fullRoot, requestedPath).Replace(Path.DirectorySeparatorChar, '/');

        return Ok(new
        {
            key = relativePath,
            name = BuildDisplayName(relativePath),
            relativePath,
            content,
            lastModifiedUtc = System.IO.File.GetLastWriteTimeUtc(requestedPath)
        });
    }

    private static string BuildKnowledgeRoot(string repositoryPath, string solutionCode)
        => Path.Combine(repositoryPath, "AI", "solutions", solutionCode);

    private static string BuildDisplayName(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var parts = fileName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return fileName;
        }

        return string.Join(' ', parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
