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
    private readonly IWorkflowRunLogStore _logs;

    public MicrosoftAgentFrameworkSolutionImplementationAgent(string endpoint, string model, IWorkflowRunLogStore logs)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
    }

    public async Task<SolutionImplementationResult> ImplementAsync(SolutionImplementationRequest request, AgentDefinition agentDefinition, CancellationToken ct)
    {
        var instructions = BuildInstructions(agentDefinition);
        var prompt = BuildPrompt(request);
        var allowedPaths = request.RepositoryFiles
            .Concat(request.RepositoryDocumentationFiles)
            .Concat(request.SolutionKnowledgeDocuments.Select(x => x.Path))
            .Concat(request.ProfileRules.Select(x => x.Path))
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
                request.Snapshot.RepositoryPath,
                allowedPaths,
                request.WorkflowRunId,
                _logs,
                ct);

            var envelope = ParseAndNormalize(rawText, request);
            var normalizedJson = JsonSerializer.Serialize(envelope, JsonOptions);
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent response parsed successfully.", ct);

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
        sb.AppendLine("- You may inspect files by returning ONLY a JSON object with this exact schema: {\"action\":\"read_file\",\"path\":\"relative/path\"}.");
        sb.AppendLine("- Request files only from the advertised repository/documentation lists.");
        sb.AppendLine("- Do not assume file contents without reading them first when they are needed.");
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

        sb.AppendLine("WORKFLOW DISCIPLINE:");
        sb.AppendLine("- This is an IMPLEMENTATION workflow.");
        sb.AppendLine("- Implement only the current backlog item.");
        sb.AppendLine("- Do NOT redesign unrelated parts of the system.");
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
        sb.AppendLine("FRAMEWORK DOCUMENTS (READ BY PATH WHEN NEEDED):");
        AppendDocumentPaths(sb, request.ProfileRules);
        sb.AppendLine();
        sb.AppendLine("SOLUTION DOCUMENTS (READ BY PATH WHEN NEEDED):");
        AppendDocumentPaths(sb, request.SolutionKnowledgeDocuments);
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

    private static void AppendDocumentPaths(StringBuilder sb, IReadOnlyList<TextDocumentInput> documents)
    {
        if (documents.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var document in documents)
        {
            sb.AppendLine($"- {document.Path}");
        }
    }

    private static void AppendPathList(StringBuilder sb, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var path in paths)
        {
            sb.AppendLine($"- {path}");
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
