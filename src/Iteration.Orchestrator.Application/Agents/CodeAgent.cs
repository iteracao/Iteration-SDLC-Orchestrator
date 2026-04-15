using Iteration.Orchestrator.Application.AI;

namespace Iteration.Orchestrator.Application.Agents;

public sealed class CodeAgent : ICodeAgent
{
    private readonly IOllamaService _ollama;

    public CodeAgent(IOllamaService ollama)
    {
        _ollama = ollama;
    }

    public async Task<string> AnalyzeCodeAsync(string code, CancellationToken ct = default)
    {
        var prompt = PromptBuilder.AnalyzeCode(code);

        // default = fast model
        return await _ollama.GenerateAsync(prompt, ct: ct);
    }
}