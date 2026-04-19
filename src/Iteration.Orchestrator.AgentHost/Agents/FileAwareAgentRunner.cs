using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    public static Task<string> RunAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        string initialPrompt,
        string repositoryRoot,
        IReadOnlyCollection<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        CancellationToken ct,
        IReadOnlyCollection<string>? requiredFrameworkPaths = null,
        IReadOnlyCollection<string>? requiredSolutionPaths = null,
        bool requireRepositoryEvidence = false)
        => RunPromptAsync(
            endpoint,
            model,
            agentName,
            instructions,
            initialPrompt,
            workflowRunId,
            logs,
            "single-pass-agent-run",
            ct);

    public static async Task<string> RunPromptAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        string prompt,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        string logTitle,
        CancellationToken ct)
    {
        var chatClient = new OllamaChatClient(new Uri(endpoint), modelId: model);
        AIAgent agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);

        await logs.AppendSectionAsync(workflowRunId, logTitle, ct);
        await logs.AppendBlockAsync(workflowRunId, $"Prompt: {logTitle}", prompt, ct);

        var rawResponse = await agent.RunAsync(prompt, cancellationToken: ct);
        var rawText = rawResponse.Text ?? string.Empty;

        await logs.AppendBlockAsync(workflowRunId, $"Response: {logTitle}", rawText, ct);
        return rawText;
    }
}
