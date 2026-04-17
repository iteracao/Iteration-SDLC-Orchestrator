using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

internal static class FileAwareAgentRunner
{
    private const int MaxToolCalls = 12;
    private const int MaxFileCharacters = 20000;

    public static async Task<string> RunAsync(
        string endpoint,
        string model,
        string agentName,
        string instructions,
        string initialPrompt,
        string repositoryRoot,
        IReadOnlyList<string> allowedPaths,
        Guid workflowRunId,
        IWorkflowRunLogStore logs,
        CancellationToken ct)
    {
        var normalizedAllowedPaths = new HashSet<string>(allowedPaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var chatClient = new OllamaChatClient(new Uri(endpoint), modelId: model);
        AIAgent agent = chatClient.AsAIAgent(name: agentName, instructions: instructions);

        var transcript = new StringBuilder();
        var currentPrompt = initialPrompt;

        for (var i = 0; i <= MaxToolCalls; i++)
        {
            await logs.AppendLineAsync(workflowRunId, $"Calling model '{model}' at '{endpoint}'. Iteration {i + 1}.", ct);
            var rawResponse = await agent.RunAsync(currentPrompt, cancellationToken: ct);
            var rawText = rawResponse?.ToString() ?? string.Empty;
            await logs.AppendLineAsync(workflowRunId, "Raw agent response received.", ct);
            await logs.AppendBlockAsync(workflowRunId, "Raw response", rawText, ct);

            if (!TryParseReadFileRequest(rawText, out var relativePath))
            {
                return rawText;
            }

            if (i == MaxToolCalls)
            {
                throw new InvalidOperationException($"Agent exceeded the maximum of {MaxToolCalls} read_file tool calls.");
            }

            var normalizedPath = NormalizePath(relativePath!);
            if (!normalizedAllowedPaths.Contains(normalizedPath))
            {
                throw new InvalidOperationException($"Agent requested a file outside the advertised repository/documentation lists: {relativePath}");
            }

            var fileContent = ReadFile(repositoryRoot, normalizedPath);
            await logs.AppendLineAsync(workflowRunId, $"Tool call: read_file('{normalizedPath}').", ct);
            await logs.AppendBlockAsync(workflowRunId, $"File content: {normalizedPath}", fileContent, ct);

            transcript.AppendLine($"AGENT TOOL REQUEST #{i + 1}:");
            transcript.AppendLine(rawText.Trim());
            transcript.AppendLine();
            transcript.AppendLine($"TOOL RESULT FOR read_file('{normalizedPath}'):");
            transcript.AppendLine("--- FILE CONTENT START ---");
            transcript.AppendLine(fileContent);
            transcript.AppendLine("--- FILE CONTENT END ---");
            transcript.AppendLine();

            currentPrompt = BuildFollowUpPrompt(initialPrompt, transcript.ToString());
        }

        throw new InvalidOperationException("Agent did not produce a final response.");
    }

    private static bool TryParseReadFileRequest(string raw, out string? relativePath)
    {
        relativePath = null;

        var json = ExtractJsonObjectOrNull(raw);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("action", out var actionElement))
            {
                return false;
            }

            var action = actionElement.GetString();
            if (!string.Equals(action, "read_file", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("path", out var pathElement))
            {
                throw new InvalidOperationException("Agent requested read_file without a 'path' property.");
            }

            relativePath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException("Agent requested read_file with an empty path.");
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObjectOrNull(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string NormalizePath(string relativePath)
        => relativePath.Replace('\\', '/').Trim();

    private static string ReadFile(string repositoryRoot, string relativePath)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid path requested: {relativePath}");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Requested file does not exist: {relativePath}");
        }

        var text = File.ReadAllText(fullPath);
        if (text.Length <= MaxFileCharacters)
        {
            return text;
        }

        return text[..MaxFileCharacters] + "\n\n[TRUNCATED BY SERVER: file content exceeded 20000 characters]";
    }

    private static string BuildFollowUpPrompt(string initialPrompt, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine(initialPrompt.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("TOOL INTERACTION HISTORY:");
        sb.AppendLine(transcript.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Continue the workflow.");
        sb.AppendLine("- If you need another file, return ONLY a JSON object with this exact schema: {\"action\":\"read_file\",\"path\":\"relative/path\"}");
        sb.AppendLine("- If you have enough information, return ONLY the final JSON output required by the workflow output contract.");
        sb.AppendLine("- Do not include markdown fences or commentary.");
        return sb.ToString();
    }
}
