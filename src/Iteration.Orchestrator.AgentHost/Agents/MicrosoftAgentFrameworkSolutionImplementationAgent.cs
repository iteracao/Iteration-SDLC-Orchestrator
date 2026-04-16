using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionImplementationAgent : ISolutionImplementationAgent
{
    private readonly string _endpoint;
    private readonly string _model;

    public MicrosoftAgentFrameworkSolutionImplementationAgent(string endpoint, string model)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
    }

    public async Task<SolutionImplementationResult> ImplementAsync(SolutionImplementationRequest request, AgentDefinition agentDefinition, CancellationToken ct)
    {
        var chatClient = new OllamaChatClient(new Uri(_endpoint), modelId: _model);
        AIAgent agent = chatClient.AsAIAgent(name: agentDefinition.Name, instructions: BuildInstructions(agentDefinition));
        var prompt = BuildPrompt(request);
        var rawResponse = await agent.RunAsync(prompt, cancellationToken: ct);
        var envelope = ParseAndNormalize(rawResponse?.ToString() ?? string.Empty, request);
        var normalizedJson = JsonSerializer.Serialize(envelope, JsonOptions);

        return new SolutionImplementationResult(
            envelope.Result!.Summary,
            envelope.Status,
            JsonSerializer.Serialize(envelope.Result.ImplementedChanges, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.FilesTouched, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.TestsExecuted, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedRequirements, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedOpenQuestions, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedDecisions, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.DocumentationUpdates, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.KnowledgeUpdates, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.RecommendedNextWorkflowCodes, JsonOptions),
            normalizedJson);
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
        sb.AppendLine("- Return JSON only.");
        sb.AppendLine("- Do not include markdown fences or commentary outside the JSON object.");
        sb.AppendLine("- Satisfy every required field in the schema.");
        sb.AppendLine("- Implement only the current backlog slice. Do not jump ahead to future backlog items.");
        return sb.ToString();
    }

    private static string BuildPrompt(SolutionImplementationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKFLOW RUN ID:");
        sb.AppendLine(request.WorkflowRunId.ToString());
        sb.AppendLine();
        sb.AppendLine("TARGET SOLUTION ID:");
        sb.AppendLine(request.TargetSolutionId.ToString());
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT ID:");
        sb.AppendLine(request.RequirementId.ToString());
        sb.AppendLine();
        sb.AppendLine("BACKLOG ITEM ID:");
        sb.AppendLine(request.BacklogItemId.ToString());
        sb.AppendLine();
        sb.AppendLine("PLAN WORKFLOW RUN ID:");
        sb.AppendLine(request.PlanWorkflowRunId.ToString());
        sb.AppendLine();
        sb.AppendLine("WORKFLOW:");
        sb.AppendLine($"{request.WorkflowCode} - {request.WorkflowName}");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW PURPOSE:");
        sb.AppendLine(request.WorkflowPurpose ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT DESCRIPTION:");
        sb.AppendLine(request.RequirementDescription ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("CURRENT BACKLOG TITLE:");
        sb.AppendLine(request.BacklogTitle ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("CURRENT BACKLOG DESCRIPTION:");
        sb.AppendLine(request.BacklogDescription ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("CURRENT BACKLOG ORDER:");
        sb.AppendLine(request.PlanningOrder.ToString());
        sb.AppendLine();
        sb.AppendLine("PLAN SUMMARY:");
        sb.AppendLine(request.PlanSummary ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("PLAN STATUS:");
        sb.AppendLine(request.PlanStatus ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("PLANNED BACKLOG ITEMS:");
        sb.AppendLine(request.GeneratedBacklogItemsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine("PLAN OPEN QUESTIONS:");
        sb.AppendLine(request.PlanOpenQuestionsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine("PLAN DECISIONS:");
        sb.AppendLine(request.PlanDecisionsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine("PROFILE SUMMARY:");
        sb.AppendLine(request.ProfileSummary ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("PROFILE RULES:");
        AppendDocuments(sb, request.ProfileRules);
        sb.AppendLine();
        sb.AppendLine("SOLUTION KNOWLEDGE DOCUMENTS:");
        AppendDocuments(sb, request.SolutionKnowledgeDocuments);
        sb.AppendLine();
        sb.AppendLine("WORKFLOW PRODUCED ARTIFACTS:");
        foreach (var artifact in request.ProducedArtifacts) sb.AppendLine($"- {artifact.Type}: {artifact.Name}");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW KNOWLEDGE UPDATES:");
        foreach (var update in request.KnowledgeUpdates) sb.AppendLine($"- {update}");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW EXECUTION RULES:");
        foreach (var rule in request.ExecutionRules) sb.AppendLine($"- {rule}");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW NEXT OPTIONS:");
        foreach (var workflowCode in request.NextWorkflowCodes) sb.AppendLine($"- {workflowCode}");
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

    private static void AppendDocuments(StringBuilder sb, IReadOnlyList<TextDocumentInput> documents)
    {
        if (documents.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var document in documents)
        {
            sb.AppendLine($"FILE: {document.Path}");
            sb.AppendLine(document.Content);
            sb.AppendLine();
        }
    }

    private static OutputEnvelope ParseAndNormalize(string raw, SolutionImplementationRequest request)
    {
        var cleaned = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<OutputEnvelope>(cleaned, JsonOptions)
            ?? throw new InvalidOperationException($"Agent returned an empty or invalid JSON payload. Raw response: {raw}");

        parsed.WorkflowCode = string.IsNullOrWhiteSpace(parsed.WorkflowCode) ? request.WorkflowCode : parsed.WorkflowCode.Trim();
        parsed.TargetSolutionId = string.IsNullOrWhiteSpace(parsed.TargetSolutionId) ? request.TargetSolutionId.ToString() : parsed.TargetSolutionId.Trim();
        parsed.WorkflowRunId = string.IsNullOrWhiteSpace(parsed.WorkflowRunId) ? request.WorkflowRunId.ToString() : parsed.WorkflowRunId.Trim();
        parsed.CompletedAtUtc = NormalizeCompletedAtUtc(parsed.CompletedAtUtc);
        parsed.Status = NormalizeStatus(parsed.Status, parsed.Result?.GeneratedOpenQuestions?.Count ?? 0);
        parsed.Result ??= new ResultPayload();
        parsed.Result.Summary = string.IsNullOrWhiteSpace(parsed.Result.Summary)
            ? $"Implementation completed for backlog '{request.BacklogTitle}'."
            : parsed.Result.Summary.Trim();
        parsed.Result.ImplementedChanges = NormalizeStringList(parsed.Result.ImplementedChanges, [$"Implement backlog item {request.BacklogTitle}"]);
        parsed.Result.FilesTouched = NormalizeStringList(parsed.Result.FilesTouched, []);
        parsed.Result.TestsExecuted = NormalizeStringList(parsed.Result.TestsExecuted, []);
        parsed.Result.GeneratedRequirements ??= [];
        parsed.Result.GeneratedOpenQuestions ??= [];
        parsed.Result.GeneratedDecisions ??= [];
        parsed.Result.DocumentationUpdates = NormalizeStringList(parsed.Result.DocumentationUpdates, request.KnowledgeUpdates);
        parsed.Result.KnowledgeUpdates = NormalizeStringList(parsed.Result.KnowledgeUpdates, request.KnowledgeUpdates);
        parsed.Result.RecommendedNextWorkflowCodes = NormalizeStringList(
            parsed.Result.RecommendedNextWorkflowCodes,
            request.NextWorkflowCodes.Count > 0 ? request.NextWorkflowCodes : [request.WorkflowCode]);

        return parsed;
    }

    private static string NormalizeCompletedAtUtc(string? value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.UtcDateTime.ToString("O")
            : DateTime.UtcNow.ToString("O");

    private static string NormalizeStatus(string? status, int openQuestionCount)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            return status.Trim().ToLowerInvariant();
        }

        return openQuestionCount > 0 ? "completed-with-open-questions" : "completed";
    }

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
            throw new InvalidOperationException($"Agent response does not contain a valid JSON object. Raw response: {raw}");
        }

        return raw[start..(end + 1)];
    }

    private sealed class OutputEnvelope
    {
        public string WorkflowCode { get; set; } = string.Empty;
        public string TargetSolutionId { get; set; } = string.Empty;
        public string WorkflowRunId { get; set; } = string.Empty;
        public string CompletedAtUtc { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public ResultPayload? Result { get; set; }
    }

    private sealed class ResultPayload
    {
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
        public string Status { get; set; } = "submitted";
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
