using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private readonly string _endpoint;
    private readonly string _model;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(string endpoint, string model)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
    }

    public async Task<SolutionAnalysisResult> AnalyzeAsync(SolutionAnalysisRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = new OllamaChatClient(new Uri(_endpoint), modelId: _model);

        AIAgent agent = chatClient.AsAIAgent(
            name: "SolutionAnalyst",
            instructions: """
You are a senior .NET solution analyst for an SDLC orchestrator.

Analyze the provided backlog item against the provided solution context.
Return ONLY valid JSON with this exact shape:
{
  "summary": "string",
  "impactedAreas": [
    { "area": "string", "reason": "string", "confidence": "low|medium|high" }
  ],
  "risks": ["string"],
  "assumptions": ["string"],
  "recommendedNextSteps": ["string"]
}

Rules:
- Do not include markdown.
- Do not include commentary outside JSON.
- Be concise but specific.
- Base the result only on the provided context.
""");

        var prompt = BuildPrompt(request);
        var rawResponse = await agent.RunAsync(prompt, cancellationToken: ct);
        var rawJson = rawResponse?.ToString() ?? string.Empty;

        var parsed = Parse(rawJson);

        return new SolutionAnalysisResult(
            parsed.Summary,
            parsed.ImpactedAreas
                .Select(x => new ImpactedAreaResult(x.Area, x.Reason, NormalizeConfidence(x.Confidence)))
                .ToList(),
            parsed.Risks,
            parsed.Assumptions,
            parsed.RecommendedNextSteps,
            rawJson);
    }

    private static string BuildPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("WORKFLOW RUN ID:");
        sb.AppendLine(request.WorkflowRunId.ToString());
        sb.AppendLine();

        sb.AppendLine("BACKLOG TITLE:");
        sb.AppendLine(request.BacklogTitle ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("BACKLOG DESCRIPTION:");
        sb.AppendLine(request.BacklogDescription ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("PROFILE SUMMARY:");
        sb.AppendLine(request.ProfileSummary ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("SOLUTION KNOWLEDGE:");
        sb.AppendLine(request.SolutionKnowledge ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("SOLUTION SNAPSHOT:");
        sb.AppendLine(JsonSerializer.Serialize(request.Snapshot, JsonOptions));
        sb.AppendLine();

        sb.AppendLine("SEARCH HITS:");
        sb.AppendLine(JsonSerializer.Serialize(request.SearchHits, JsonOptions));
        sb.AppendLine();

        sb.AppendLine("SAMPLE FILES:");
        sb.AppendLine(JsonSerializer.Serialize(request.SampleFiles, JsonOptions));

        return sb.ToString();
    }

    private static AgentResponse Parse(string raw)
    {
        var cleaned = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<AgentResponse>(cleaned, JsonOptions);

        if (parsed is null)
        {
            throw new InvalidOperationException("Agent returned an empty or invalid JSON payload.");
        }

        parsed.ImpactedAreas ??= [];
        parsed.Risks ??= [];
        parsed.Assumptions ??= [];
        parsed.RecommendedNextSteps ??= [];

        return parsed;
    }

    private static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Agent returned an empty response.");
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Agent response did not contain a valid JSON object.");
        }

        return raw[start..(end + 1)];
    }

    private static string NormalizeConfidence(string? confidence)
    {
        return confidence?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            _ => "medium"
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class AgentResponse
    {
        public string Summary { get; set; } = string.Empty;
        public List<ImpactedArea> ImpactedAreas { get; set; } = [];
        public List<string> Risks { get; set; } = [];
        public List<string> Assumptions { get; set; } = [];
        public List<string> RecommendedNextSteps { get; set; } = [];
    }

    private sealed class ImpactedArea
    {
        public string Area { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Confidence { get; set; } = "medium";
    }
}