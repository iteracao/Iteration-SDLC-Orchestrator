using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkImplementationPlannerAgent : ISolutionPlannerAgent
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;

    public MicrosoftAgentFrameworkImplementationPlannerAgent(string endpoint, string model, IWorkflowRunLogStore logs)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
    }

    public async Task<SolutionPlanResult> PlanAsync(SolutionPlanRequest request, AgentDefinition agentDefinition, CancellationToken ct)
    {
        var chatClient = new OllamaChatClient(new Uri(_endpoint), modelId: _model);
        AIAgent agent = chatClient.AsAIAgent(name: agentDefinition.Name, instructions: BuildInstructions(agentDefinition));
        var prompt = BuildPrompt(request);
        await _logs.AppendLineAsync(request.WorkflowRunId, "Agent prompt prepared.", ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, "Prompt", prompt, ct);

        try
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, $"Calling model '{_model}' at '{_endpoint}'.", ct);
            var rawResponse = await agent.RunAsync(prompt, cancellationToken: ct);
            var rawText = rawResponse?.ToString() ?? string.Empty;
            await _logs.AppendLineAsync(request.WorkflowRunId, "Raw agent response received.", ct);
            await _logs.AppendBlockAsync(request.WorkflowRunId, "Raw response", rawText, ct);

            var envelope = ParseAndNormalize(rawText, request);
            var normalizedJson = JsonSerializer.Serialize(envelope, JsonOptions);
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent response parsed successfully.", ct);

            return new SolutionPlanResult(
            envelope.Result!.Summary,
            envelope.Status,
            JsonSerializer.Serialize(envelope.Result.Artifacts, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedBacklogItems, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedOpenQuestions, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.GeneratedDecisions, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.DocumentationUpdates, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.KnowledgeUpdates, JsonOptions),
            JsonSerializer.Serialize(envelope.Result.RecommendedNextWorkflowCodes, JsonOptions),
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
        sb.AppendLine("- Return JSON only.");
        sb.AppendLine("- Do not include markdown fences or commentary outside the JSON object.");
        sb.AppendLine("- Satisfy every required field in the schema.");
        sb.AppendLine("- Always generate an ordered implementation plan with concrete backlog slices.");
        return sb.ToString();
    }

    private static string BuildPrompt(SolutionPlanRequest request)
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
        sb.AppendLine("DESIGN WORKFLOW RUN ID:");
        sb.AppendLine(request.DesignWorkflowRunId.ToString());
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
        sb.AppendLine("DESIGN SUMMARY:");
        sb.AppendLine(request.DesignSummary ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("DESIGN STATUS:");
        sb.AppendLine(request.DesignStatus ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("DESIGN ARTIFACTS:");
        sb.AppendLine(request.DesignArtifactsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine("DESIGN OPEN QUESTIONS:");
        sb.AppendLine(request.DesignOpenQuestionsJson ?? "[]");
        sb.AppendLine();
        sb.AppendLine("DESIGN DECISIONS:");
        sb.AppendLine(request.DesignDecisionsJson ?? "[]");
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

    private static OutputEnvelope ParseAndNormalize(string raw, SolutionPlanRequest request)
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
        parsed.Result.Summary = string.IsNullOrWhiteSpace(parsed.Result.Summary) ? $"Planning completed for '{request.RequirementTitle}'." : parsed.Result.Summary.Trim();
        parsed.Result.Artifacts = NormalizeArtifacts(parsed.Result.Artifacts, request.ProducedArtifacts);
        parsed.Result.GeneratedBacklogItems = NormalizeBacklogItems(parsed.Result.GeneratedBacklogItems);
        parsed.Result.GeneratedOpenQuestions ??= [];
        parsed.Result.GeneratedDecisions ??= [];
        parsed.Result.DocumentationUpdates = NormalizeStringList(parsed.Result.DocumentationUpdates, request.KnowledgeUpdates);
        parsed.Result.KnowledgeUpdates = NormalizeStringList(parsed.Result.KnowledgeUpdates, request.KnowledgeUpdates);
        parsed.Result.RecommendedNextWorkflowCodes = NormalizeStringList(parsed.Result.RecommendedNextWorkflowCodes, request.NextWorkflowCodes.Count > 0 ? request.NextWorkflowCodes : [request.WorkflowCode]);
        return parsed;
    }

    private static List<BacklogItemPayload> NormalizeBacklogItems(List<BacklogItemPayload>? items)
    {
        var normalized = items?.Where(x => !string.IsNullOrWhiteSpace(x.Title)).Select((x, index) => new BacklogItemPayload
        {
            Order = x.Order.GetValueOrDefault(index + 1) <= 0 ? index + 1 : x.Order,
            Title = x.Title!.Trim(),
            Description = string.IsNullOrWhiteSpace(x.Description) ? x.Title!.Trim() : x.Description.Trim(),
            WorkflowCode = string.IsNullOrWhiteSpace(x.WorkflowCode) ? "implement-solution-change" : x.WorkflowCode.Trim(),
            Priority = string.IsNullOrWhiteSpace(x.Priority) ? "medium" : x.Priority.Trim().ToLowerInvariant()
        }).OrderBy(x => x.Order).ToList() ?? [];

        return normalized.Count > 0 ? normalized : [new BacklogItemPayload { Order = 1, Title = "Implement solution change", Description = "Implement the designed solution change as a first delivery slice.", WorkflowCode = "implement-solution-change", Priority = "medium" }];
    }

    private static List<ArtifactPayload> NormalizeArtifacts(List<ArtifactPayload>? artifacts, IReadOnlyList<WorkflowArtifactDefinition> fallbackArtifacts)
        => artifacts?.Where(x => !string.IsNullOrWhiteSpace(x.ArtifactType) && !string.IsNullOrWhiteSpace(x.Name)).Select(x => new ArtifactPayload { ArtifactType = x.ArtifactType.Trim(), Name = x.Name.Trim(), Path = string.IsNullOrWhiteSpace(x.Path) ? null : x.Path.Trim() }).ToList()
           ?? (fallbackArtifacts.Count > 0 ? fallbackArtifacts.Select(x => new ArtifactPayload { ArtifactType = x.Type, Name = x.Name, Path = null }).ToList() : [new ArtifactPayload { ArtifactType = "implementation-plan", Name = "Implementation Plan" }]);

    private static List<string> NormalizeStringList(List<string>? values, IReadOnlyList<string> fallbackValues)
    {
        var normalized = values?.Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        return normalized.Count > 0 ? normalized : fallbackValues.Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeCompletedAtUtc(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime.ToString("O") : DateTime.UtcNow.ToString("O");

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
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private sealed class OutputEnvelope
    {
        public string WorkflowCode { get; set; } = string.Empty;
        public string TargetSolutionId { get; set; } = string.Empty;
        public string WorkflowRunId { get; set; } = string.Empty;
        public string Status { get; set; } = "completed";
        public string CompletedAtUtc { get; set; } = string.Empty;
        public ResultPayload? Result { get; set; }
    }

    private sealed class ResultPayload
    {
        public string Summary { get; set; } = string.Empty;
        public List<ArtifactPayload>? Artifacts { get; set; }
        public List<BacklogItemPayload>? GeneratedBacklogItems { get; set; }
        public List<OpenQuestionPayload>? GeneratedOpenQuestions { get; set; }
        public List<DecisionPayload>? GeneratedDecisions { get; set; }
        public List<string>? DocumentationUpdates { get; set; }
        public List<string>? KnowledgeUpdates { get; set; }
        public List<string>? RecommendedNextWorkflowCodes { get; set; }
    }

    private sealed class ArtifactPayload { public string ArtifactType { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string? Path { get; set; } }
    private sealed class BacklogItemPayload { public int? Order { get; set; } public string? Title { get; set; } public string? Description { get; set; } public string? WorkflowCode { get; set; } public string? Priority { get; set; } }
    private sealed class OpenQuestionPayload { public string? Title { get; set; } public string? Description { get; set; } public string? Category { get; set; } public string? Status { get; set; } public string? ResolutionNotes { get; set; } public string? RaisedBy { get; set; } public string? AssignedTo { get; set; } public string? RaisedAtUtc { get; set; } public string? ResolvedAtUtc { get; set; } }
    private sealed class DecisionPayload { public string? Title { get; set; } public string? Summary { get; set; } public string? DecisionType { get; set; } public string? Status { get; set; } public string? Rationale { get; set; } public List<string>? Consequences { get; set; } public List<string>? AlternativesConsidered { get; set; } public string? DecidedBy { get; set; } public string? DecidedAtUtc { get; set; } }
}
