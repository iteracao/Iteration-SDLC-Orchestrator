using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionImplementationAgent : ISolutionImplementationAgent
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly int _maxModelResponseSeconds;

    public MicrosoftAgentFrameworkSolutionImplementationAgent(string endpoint, string model, IWorkflowRunLogStore logs, IWorkflowPayloadStore payloadStore, int maxModelResponseSeconds)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
        _payloadStore = payloadStore;
        _maxModelResponseSeconds = Math.Clamp(maxModelResponseSeconds, 1, 180);
    }

    public async Task<SolutionImplementationResult> ImplementAsync(SolutionImplementationRequest request, AgentDefinition agentDefinition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agentDefinition);

        var instructions = BuildInstructions(agentDefinition);
        var prompt = BuildPrompt(request);
        var allowedPaths = request.RepositoryFiles
            .Concat(request.RepositoryDocumentationFiles)
            .Concat(request.SolutionKnowledgeFiles.Select(x => x.Path))
            .Concat(request.ProfileRuleFiles.Select(x => x.Path))
            .Concat(request.RepositoryEvidenceFiles.Select(x => x.Path))
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
                maxModelResponseSeconds: _maxModelResponseSeconds);

            var payload = ParseAndNormalize(rawText, request);
            var normalizedJson = JsonSerializer.Serialize(payload, JsonOptions);
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent response parsed successfully.", ct);

            return new SolutionImplementationResult(
                payload.Summary,
                payload.Status,
                JsonSerializer.Serialize(payload.ImplementedChanges, JsonOptions),
                JsonSerializer.Serialize(payload.FilesTouched, JsonOptions),
                JsonSerializer.Serialize(payload.TestsExecuted, JsonOptions),
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
        sb.AppendLine("Output contract:");
        sb.AppendLine(agentDefinition.OutputSchemaJson.Trim());
        sb.AppendLine();
        sb.AppendLine("Global rules:");
        sb.AppendLine("- Return JSON tool calls only.");
        sb.AppendLine("- First load your structured workflow input with get_workflow_input.");
        sb.AppendLine("- Read repository or documentation files only when needed by returning ONLY: {\"action\":\"read_file\",\"path\":\"relative/path\"}.");
        sb.AppendLine("- When the implementation result is ready, persist it by returning ONLY: {\"action\":\"save_workflow_output\",\"workflowRunId\":\"guid\",\"output\":{...}}.");
        sb.AppendLine("- The output object must satisfy every required field in the schema.");
        sb.AppendLine("- Implement only the current backlog slice. Do not jump ahead to future backlog items.");
        return sb.ToString();
    }

    private static string BuildPrompt(SolutionImplementationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKFLOW RUN ID:");
        sb.AppendLine(request.WorkflowRunId.ToString());
        sb.AppendLine();
        sb.AppendLine("WORKFLOW DISCIPLINE:");
        sb.AppendLine("- This is an IMPLEMENTATION workflow.");
        sb.AppendLine("- Deliver only the current backlog slice.");
        sb.AppendLine("- Do NOT jump ahead to future backlog items.");
        sb.AppendLine();
        sb.AppendLine("OPERATING MODE:");
        sb.AppendLine("- Load the structured workflow input from the database with get_workflow_input.");
        sb.AppendLine("- Use read_file only for evidence you actually need.");
        sb.AppendLine("- Save only the final business output payload with save_workflow_output.");
        return sb.ToString();
    }

    private static WorkflowOutputPayload ParseAndNormalize(string raw, SolutionImplementationRequest request)
    {
        var cleaned = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<WorkflowOutputPayload>(cleaned, JsonOptions)
            ?? throw new InvalidOperationException($"Agent returned an empty or invalid JSON payload. Raw response: {raw}");

        parsed.Status = NormalizeStatus(parsed.Status, parsed.GeneratedOpenQuestions?.Count ?? 0);
        parsed.Summary = string.IsNullOrWhiteSpace(parsed.Summary)
            ? $"Implementation completed for backlog '{request.BacklogTitle}'."
            : parsed.Summary.Trim();
        parsed.ImplementedChanges = NormalizeStringList(parsed.ImplementedChanges, [$"Implement backlog item {request.BacklogTitle}"]);
        parsed.FilesTouched = NormalizeStringList(parsed.FilesTouched, []);
        parsed.TestsExecuted = NormalizeStringList(parsed.TestsExecuted, []);
        parsed.GeneratedRequirements ??= [];
        parsed.GeneratedOpenQuestions ??= [];
        parsed.GeneratedDecisions ??= [];
        parsed.DocumentationUpdates = NormalizeStringList(parsed.DocumentationUpdates, request.KnowledgeUpdates);
        parsed.KnowledgeUpdates = NormalizeStringList(parsed.KnowledgeUpdates, request.KnowledgeUpdates);
        parsed.RecommendedNextWorkflowCodes = NormalizeStringList(parsed.RecommendedNextWorkflowCodes, request.NextWorkflowCodes.Count > 0 ? request.NextWorkflowCodes : [request.WorkflowCode]);
        return parsed;
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

    private static List<string> NormalizeStringList(IEnumerable<string>? values, IEnumerable<string> fallback)
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

        return fallback
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            throw new InvalidOperationException($"Agent response did not contain a valid JSON object. Raw response: {raw}");
        }

        return raw[start..(end + 1)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed class WorkflowOutputPayload
    {
        public string Status { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string>? ImplementedChanges { get; set; }
        public List<string>? FilesTouched { get; set; }
        public List<string>? TestsExecuted { get; set; }
        public List<GeneratedRequirementPayload>? GeneratedRequirements { get; set; }
        public List<GeneratedOpenQuestionPayload>? GeneratedOpenQuestions { get; set; }
        public List<GeneratedDecisionPayload>? GeneratedDecisions { get; set; }
        public List<string>? DocumentationUpdates { get; set; }
        public List<string>? KnowledgeUpdates { get; set; }
        public List<string>? RecommendedNextWorkflowCodes { get; set; }
    }

    private sealed class GeneratedRequirementPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string RequirementType { get; set; } = "functional";
        public string Source { get; set; } = "implementation";
        public string Status { get; set; } = "Pending";
        public string Priority { get; set; } = "medium";
        public string AcceptanceCriteriaJson { get; set; } = "[]";
        public string ConstraintsJson { get; set; } = "[]";
    }

    private sealed class GeneratedOpenQuestionPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? Status { get; set; }
        public string? ResolutionNotes { get; set; }
        public string? RaisedAtUtc { get; set; }
        public string? ResolvedAtUtc { get; set; }
    }

    private sealed class GeneratedDecisionPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? DecisionType { get; set; }
        public string? Status { get; set; }
        public string? Rationale { get; set; }
        public List<string>? Consequences { get; set; }
        public List<string>? AlternativesConsidered { get; set; }
        public string? DecidedAtUtc { get; set; }
    }
}
