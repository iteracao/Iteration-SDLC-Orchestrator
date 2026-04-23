
using Iteration.Orchestrator.Application.AI;

namespace Iteration.Orchestrator.Application.Agents;

public sealed class CodeAgent : ICodeAgent
{
    private readonly ITextGenerationService _textGeneration;

    public CodeAgent(ITextGenerationService textGeneration)
    {
        _textGeneration = textGeneration;
    }

    public async Task<string> AnalyzeCodeAsync(string code, CancellationToken ct = default)
    {
        var prompt = PromptBuilder.AnalyzeCode(code);

        // default = fast model
        return await _textGeneration.GenerateAsync(prompt, ct: ct);
    }
}
