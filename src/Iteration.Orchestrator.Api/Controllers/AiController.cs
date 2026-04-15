using Microsoft.AspNetCore.Mvc;
using Iteration.Orchestrator.Infrastructure.AI;

[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IOllamaService _ollama;

    public AiController(IOllamaService ollama)
    {
        _ollama = ollama;
    }

    [HttpGet("test")]
    public async Task<IActionResult> Test()
    {
        var result = await _ollama.GenerateAsync("Say hello in one sentence.");
        return Ok(result);
    }
}