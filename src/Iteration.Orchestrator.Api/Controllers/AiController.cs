using Iteration.Orchestrator.Application.Agents;
using Microsoft.AspNetCore.Mvc;

namespace Iteration.Orchestrator.Api.Controllers;

[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly ICodeAgent _agent;

    public AiController(ICodeAgent agent)
    {
        _agent = agent;
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest("Code is required.");

        var result = await _agent.AnalyzeCodeAsync(code, ct);
        return Ok(result);
    }
}