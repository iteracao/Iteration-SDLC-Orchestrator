using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionDesignerAgent : ISolutionDesignerAgent
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;

    public MicrosoftAgentFrameworkSolutionDesignerAgent(string endpoint, string model, IWorkflowRunLogStore logs)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
    }

    public async Task<SolutionDesignResult> DesignAsync(
        SolutionDesignRequest request,
        AgentDefinition agentDefinition,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agentDefinition);

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

            return new SolutionDesignResult(
            envelope.Result!.Summary,
            envelope.Status,
            JsonSerializer.Serialize(envelope.Result.Artifacts, JsonOptions),
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
        sb.AppendLine("- Keep design outputs concrete, implementation-aware, and grounded in current solution knowledge.");
        return sb.ToString();
    }

    private static string BuildPrompt(SolutionDesignRequest request)
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

        sb.AppendLine("ANALYSIS WORKFLOW RUN ID:");
        sb.AppendLine(request.AnalysisWorkflowRunId.ToString());
        sb.AppendLine();

        sb.AppendLine("WORKFLOW:");
        sb.AppendLine($"{request.WorkflowCode} - {request.WorkflowName}");
        sb.AppendLine();

        sb.AppendLine("WORKFLOW PURPOSE:");
        sb.AppendLine(request.WorkflowPurpose ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("WORKFLOW DISCIPLINE:");
        sb.AppendLine("- This is a DESIGN workflow.");
        sb.AppendLine("- Build on the approved analysis and define the solution approach.");
        sb.AppendLine("- Do NOT produce implementation code or backlog execution details.");
        sb.AppendLine();

        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("REQUIREMENT DESCRIPTION:");
        sb.AppendLine(request.RequirementDescription ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("ANALYSIS SUMMARY:");
        sb.AppendLine(request.AnalysisSummary ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("ANALYSIS STATUS:");
        sb.AppendLine(request.AnalysisStatus ?? string.Empty);
        sb.AppendLine();

        sb.AppendLine("ANALYSIS ARTIFACTS:");
        sb.AppendLine(request.AnalysisArtifactsJson ?? "[]");
        sb.AppendLine();

        sb.AppendLine("ANALYSIS OPEN QUESTIONS:");
        sb.AppendLine(request.AnalysisOpenQuestionsJson ?? "[]");
        sb.AppendLine();

        sb.AppendLine("ANALYSIS DECISIONS:");
        sb.AppendLine(request.AnalysisDecisionsJson ?? "[]");
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
        foreach (var artifact in request.ProducedArtifacts)
        {
            sb.AppendLine($"- {artifact.Type}: {artifact.Name}");
        }
        sb.AppendLine();

        sb.AppendLine("WORKFLOW KNOWLEDGE UPDATES:");
        foreach (var update in request.KnowledgeUpdates)
        {
            sb.AppendLine($"- {update}");
        }
        sb.AppendLine();

        sb.AppendLine("WORKFLOW EXECUTION RULES:");
        foreach (var rule in request.ExecutionRules)
        {
            sb.AppendLine($"- {rule}");
        }
        sb.AppendLine();

        sb.AppendLine("WORKFLOW NEXT OPTIONS:");
        foreach (var workflowCode in request.NextWorkflowCodes)
        {
            sb.AppendLine($"- {workflowCode}");
        }
        sb.AppendLine();

        sb.AppendLine("INPUT USAGE RULES:");
        sb.AppendLine("- Repository files under src/ and the repository root are the primary evidence of actual implementation.");
        sb.AppendLine("- Solution documents under AI/solutions/... are curated knowledge and may be incomplete.");
        sb.AppendLine("- Framework documents are rules and constraints, not facts about the current implementation.");
        sb.AppendLine("- Start from high-level files, then drill into relevant files only.");
        sb.AppendLine("- Do not read files blindly; request only the files needed to complete this workflow.");
        sb.AppendLine();

        sb.AppendLine("REPOSITORY FILES (READ BY PATH WHEN NEEDED):");
        AppendPathList(sb, request.RepositoryFiles);
        sb.AppendLine();

        sb.AppendLine("REPOSITORY DOCUMENTATION FILES (READ BY PATH WHEN NEEDED):");
        AppendPathList(sb, request.RepositoryDocumentationFiles);
        sb.AppendLine();

        sb.AppendLine("SOLUTION SNAPSHOT:");
        sb.AppendLine(JsonSerializer.Serialize(request.Snapshot, JsonOptions));
        sb.AppendLine();

        sb.AppendLine("SEARCH HITS (HINTS ONLY):");
        sb.AppendLine(JsonSerializer.Serialize(request.SearchHits, JsonOptions));

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

    private static OutputEnvelope ParseAndNormalize(string raw, SolutionDesignRequest request)
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
            ? $"Design completed for '{request.RequirementTitle}'."
            : parsed.Result.Summary.Trim();
        parsed.Result.Artifacts = NormalizeArtifacts(parsed.Result.Artifacts, request.ProducedArtifacts);
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

    private static List<ArtifactPayload> NormalizeArtifacts(
        List<ArtifactPayload>? artifacts,
        IReadOnlyList<WorkflowArtifactDefinition> fallbackArtifacts)
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
            ? fallbackArtifacts.Select(x => new ArtifactPayload
            {
                ArtifactType = x.Type,
                Name = x.Name,
                Path = null
            }).ToList()
            : [new ArtifactPayload { ArtifactType = "design-report", Name = "Design Report" }];
    }

    private static List<string> NormalizeStringList(List<string>? values, IReadOnlyList<string> fallbackValues)
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

    private static string NormalizeCompletedAtUtc(string? completedAtUtc)
    {
        return DateTimeOffset.TryParse(completedAtUtc, out var parsed)
            ? parsed.UtcDateTime.ToString("O")
            : DateTime.UtcNow.ToString("O");
    }

    private static string NormalizeStatus(string? status, int openQuestionCount)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "completed-with-open-questions" => "completed-with-open-questions",
            "blocked" => "blocked",
            "failed" => "failed",
            _ => openQuestionCount > 0 ? "completed-with-open-questions" : "completed"
        };
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        if (start < 0 || end < start)
        {
            throw new InvalidOperationException($"Agent did not return a JSON object. Raw response: {raw}");
        }

        return raw[start..(end + 1)];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class OutputEnvelope
    {
        public string WorkflowCode { get; set; } = string.Empty;
        public string TargetSolutionId { get; set; } = string.Empty;
        public string? WorkflowRunId { get; set; }
        public string Status { get; set; } = "completed";
        public string? CompletedAtUtc { get; set; }
        public ResultPayload? Result { get; set; }
    }

    private sealed class ResultPayload
    {
        public string Summary { get; set; } = string.Empty;
        public List<ArtifactPayload>? Artifacts { get; set; }
        public List<object>? GeneratedRequirements { get; set; }
        public List<OpenQuestionPayload>? GeneratedOpenQuestions { get; set; }
        public List<DecisionPayload>? GeneratedDecisions { get; set; }
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

    private sealed class OpenQuestionPayload
    {
        public string? RequirementId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Status { get; set; }
        public string? ResolutionNotes { get; set; }
        public string? RaisedAtUtc { get; set; }
        public string? ResolvedAtUtc { get; set; }
    }

    private sealed class DecisionPayload
    {
        public string? RequirementId { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? DecisionType { get; set; }
        public string? Status { get; set; }
        public string? Rationale { get; set; }
        public List<string>? Consequences { get; set; }
        public List<string>? AlternativesConsidered { get; set; }
        public string? DecidedAtUtc { get; set; }
    }
}
