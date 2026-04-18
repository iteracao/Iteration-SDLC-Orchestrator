using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(string endpoint, string model, IWorkflowRunLogStore logs, IWorkflowPayloadStore payloadStore)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
        _payloadStore = payloadStore;
    }

    public async Task<SolutionAnalysisResult> AnalyzeAsync(SolutionAnalysisRequest request, AgentDefinition agentDefinition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agentDefinition);

        var instructions = BuildInstructions(agentDefinition);
        var prompt = BuildPrompt(request);
        var requiredFrameworkPaths = new[]
        {
            "AI/framework/rules/sdlc/analyze.md",
            "AI/framework/agents/solution-analyst/prompt.md"
        }
        .Concat(request.ProfileRuleFiles.Select(x => x.Path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var requiredSolutionPaths = request.SolutionKnowledgeFiles
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowedPaths = request.RepositoryFiles
            .Concat(request.RepositoryDocumentationFiles)
            .Concat(requiredSolutionPaths)
            .Concat(requiredFrameworkPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _logs.AppendLineAsync(request.WorkflowRunId, "Agent prompt prepared.", ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, "Prompt", prompt, ct);

        try
        {
            var rawText = await FileAwareAgentRunner.RunAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                instructions,
                prompt,
                request.RepositoryPath,
                allowedPaths,
                request.WorkflowRunId,
                _logs,
                _payloadStore,
                ct,
                requiredFrameworkPaths,
                requiredSolutionPaths,
                requireRepositoryEvidence: true);

            var payload = ParseAndNormalize(rawText, request);
            var normalizedJson = JsonSerializer.Serialize(payload, JsonOptions);
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent response parsed successfully.", ct);

            return new SolutionAnalysisResult(
                payload.Summary,
                payload.Status,
                JsonSerializer.Serialize(payload.Artifacts, JsonOptions),
                JsonSerializer.Serialize(payload.GeneratedRequirements, JsonOptions),
                JsonSerializer.Serialize(payload.GeneratedOpenQuestions, JsonOptions),
                JsonSerializer.Serialize(payload.GeneratedDecisions, JsonOptions),
                JsonSerializer.Serialize(payload.DocumentationUpdates, JsonOptions),
                JsonSerializer.Serialize(payload.KnowledgeUpdates, JsonOptions),
                JsonSerializer.Serialize(payload.RecommendedNextWorkflowCodes, JsonOptions),
                normalizedJson);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent execution failed.", CancellationToken.None);
            await _logs.AppendBlockAsync(request.WorkflowRunId, "Exception", ex.ToString(), CancellationToken.None);
            throw;
        }
    }

    private static string BuildInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("OUTPUT CONTRACT:");
        sb.AppendLine(agentDefinition.OutputSchemaJson.Trim());
        sb.AppendLine();
        sb.AppendLine("TOOL USAGE RULES:");
        sb.AppendLine("- Return JSON tool calls only.");
        sb.AppendLine("- You MUST call get_workflow_input first.");
        sb.AppendLine("- You MUST load required framework context before analysis.");
        sb.AppendLine("- You MUST load required solution context before analysis.");
        sb.AppendLine("- You MUST read repository evidence before saving output.");
        sb.AppendLine("- When reading a file, return ONLY: {\"action\":\"read_file\",\"path\":\"relative/path\"}.");
        sb.AppendLine("- When saving output, return ONLY: {\"action\":\"save_workflow_output\",\"workflowRunId\":\"guid\",\"output\":{...}}.");
        sb.AppendLine("- The workflowRunId used in save_workflow_output MUST match the active workflow run.");
        sb.AppendLine("- The output object MUST satisfy every required field in the schema.");
        sb.AppendLine("- Do not include markdown fences or commentary outside the JSON object.");
        sb.AppendLine("- Do not save output before required context-loading is complete.");
        return sb.ToString();
    }

    private static string BuildPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKFLOW RUN ID:");
        sb.AppendLine(request.WorkflowRunId.ToString());
        sb.AppendLine();
        sb.AppendLine("WORKFLOW TYPE:");
        sb.AppendLine("ANALYSIS");
        sb.AppendLine();
        sb.AppendLine("ROLE:");
        sb.AppendLine("You are the Solution Analyst working inside a strict SDLC workflow.");
        sb.AppendLine();
        sb.AppendLine("MANDATORY EXECUTION SEQUENCE:");
        sb.AppendLine("1. Call get_workflow_input using the workflowRunId above.");
        sb.AppendLine("2. Read required framework context first.");
        sb.AppendLine("3. Read required solution knowledge files.");
        sb.AppendLine("4. Read relevant repository evidence files.");
        sb.AppendLine("5. Only then perform the analysis.");
        sb.AppendLine("6. Only then save final output with save_workflow_output.");
        sb.AppendLine();
        sb.AppendLine("REQUIRED FRAMEWORK CONTEXT:");
        sb.AppendLine("- AI/framework/rules/sdlc/analyze.md");
        sb.AppendLine("- AI/framework/agents/solution-analyst/prompt.md");
        foreach (var file in request.ProfileRuleFiles)
        {
            sb.AppendLine($"- {file.Path}");
        }
        sb.AppendLine();
        sb.AppendLine("REQUIRED SOLUTION CONTEXT:");
        foreach (var file in request.SolutionKnowledgeFiles)
        {
            sb.AppendLine($"- {file.Path}");
        }
        sb.AppendLine();
        sb.AppendLine("REPOSITORY EVIDENCE RULE:");
        sb.AppendLine("- You MUST inspect relevant repository files before concluding.");
        sb.AppendLine("- Do not rely only on workflow input or solution documentation.");
        sb.AppendLine("- You MUST read at least one repository file before saving output.");
        sb.AppendLine();
        sb.AppendLine("ANALYSIS EXPECTATIONS:");
        sb.AppendLine("- Identify impacted areas.");
        sb.AppendLine("- Identify risks.");
        sb.AppendLine("- Identify assumptions.");
        sb.AppendLine("- Identify gaps and ambiguities.");
        sb.AppendLine("- Generate open questions when information is missing.");
        sb.AppendLine();
        sb.AppendLine("FORBIDDEN BEHAVIOR:");
        sb.AppendLine("- Do NOT design the solution.");
        sb.AppendLine("- Do NOT create backlog items.");
        sb.AppendLine("- Do NOT describe implementation steps.");
        sb.AppendLine("- Do NOT skip required context loading.");
        sb.AppendLine("- Do NOT save output before reading required files.");
        sb.AppendLine("- Do NOT invent workflowRunId, file contents, or evidence.");
        sb.AppendLine();
        sb.AppendLine("FINAL OUTPUT EXPECTATIONS:");
        sb.AppendLine("- Return top-level workflow output fields only.");
        sb.AppendLine("- summary is required.");
        sb.AppendLine("- artifacts are required.");
        sb.AppendLine("- recommendedNextWorkflowCodes are required.");
        sb.AppendLine("- generatedOpenQuestions are required when ambiguity exists.");
        sb.AppendLine("- Use the exact same workflowRunId when saving output.");
        return sb.ToString();
    }

    private static WorkflowOutputPayload ParseAndNormalize(string raw, SolutionAnalysisRequest request)
    {
        var cleaned = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<WorkflowOutputPayload>(cleaned, JsonOptions)
            ?? throw new InvalidOperationException($"Agent returned an empty or invalid JSON payload. Raw response: {raw}");

        parsed.Status = NormalizeStatus(parsed.Status, parsed.GeneratedOpenQuestions?.Count ?? 0);
        parsed.Summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? $"Analysis completed for '{request.RequirementTitle}'."
            : parsed.Summary.Trim();
        parsed.Artifacts = NormalizeArtifacts(parsed.Artifacts, request.ProducedArtifacts, "analysis-report", "Analysis Report");
        parsed.GeneratedRequirements ??= [];
        parsed.GeneratedOpenQuestions ??= [];
        parsed.GeneratedDecisions ??= [];
        parsed.DocumentationUpdates = NormalizeStringList(parsed.DocumentationUpdates, request.KnowledgeUpdates);
        parsed.KnowledgeUpdates = NormalizeStringList(parsed.KnowledgeUpdates, request.KnowledgeUpdates);
        parsed.RecommendedNextWorkflowCodes = NormalizeStringList(
            parsed.RecommendedNextWorkflowCodes,
            request.NextWorkflowCodes.Count > 0 ? request.NextWorkflowCodes : [request.WorkflowCode]);
        return parsed;
    }

    private static List<ArtifactPayload> NormalizeArtifacts(List<ArtifactPayload>? artifacts, IReadOnlyList<WorkflowArtifactDefinition> fallbackArtifacts, string fallbackType, string fallbackName)
    {
        var normalized = artifacts?
            .Where(x => !string.IsNullOrWhiteSpace(x.ArtifactType) && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new ArtifactPayload
            {
                ArtifactType = x.ArtifactType.Trim(),
                Name = x.Name.Trim(),
                Path = string.IsNullOrWhiteSpace(x.Path) ? null : x.Path.Trim()
            })
            .ToList() ?? [];

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallbackArtifacts.Count > 0
            ? fallbackArtifacts.Select(x => new ArtifactPayload { ArtifactType = x.Type, Name = x.Name, Path = null }).ToList()
            : [new ArtifactPayload { ArtifactType = fallbackType, Name = fallbackName }];
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values, IEnumerable<string> fallbackValues)
    {
        var normalized = values?
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallbackValues
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeStatus(string? status, int openQuestionCount)
        => status?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "completed-with-open-questions" => "completed-with-open-questions",
            "blocked" => "blocked",
            "failed" => "failed",
            _ => openQuestionCount > 0 ? "completed-with-open-questions" : "completed"
        };

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
            throw new InvalidOperationException($"Agent response did not contain a valid JSON object. Raw response: {raw}");
        }

        return raw[start..(end + 1)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class WorkflowOutputPayload
    {
        public string Status { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<ArtifactPayload>? Artifacts { get; set; }
        public List<JsonElement>? GeneratedRequirements { get; set; }
        public List<JsonElement>? GeneratedOpenQuestions { get; set; }
        public List<JsonElement>? GeneratedDecisions { get; set; }
        public List<string>? DocumentationUpdates { get; set; }
        public List<string>? KnowledgeUpdates { get; set; }
        public List<string>? RecommendedNextWorkflowCodes { get; set; }
    }

    private sealed class ArtifactPayload
    {
        public string ArtifactType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
    }
}
