namespace Iteration.Orchestrator.Application.Agents;

public static class PromptBuilder
{
    public static string AnalyzeCode(string code)
    {
        return $@"
You are a senior .NET architect.

Analyze the following C# code and:
- identify issues
- suggest improvements
- suggest better structure if needed

Code:
{code}
";
    }
}