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
    private readonly IArtifactStore _artifacts;

    public MicrosoftAgentFrameworkSolutionDesignerAgent(string endpoint, string model, IWorkflowRunLogStore logs, IArtifactStore artifacts)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
        _artifacts = artifacts;
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
        await _logs.AppendKeyValuesAsync(request.WorkflowRunId, "Prompt summary", new Dictionary<string, string?>
        {
            ["Model"] = _model,
            ["Repository files available"] = request.RepositoryFiles.Count.ToString(),
            ["Framework docs available"] = request.ProfileRules.Count.ToString(),
            ["Solution docs available"] = request.SolutionKnowledgeDocuments.Count.ToString()
        }, ct);
        await _artifacts.SaveTextAsync(request.WorkflowRunId, "prompt.txt", prompt, ct);

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
            await _artifacts.SaveTextAsync(request.WorkflowRunId, "agent-response.raw.txt", rawText, ct);

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
            await _logs.AppendKeyValuesAsync(request.WorkflowRunId, "Error", new Dictionary<string, string?>
            {
                ["Type"] = ex.GetType().Name,
                ["Message"] = ex.Message
            }, CancellationToken.None);
            await _artifacts.SaveTextAsync(request.WorkflowRunId, "agent-exception.txt", ex.ToString(), CancellationToken.None);
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
        var likelyFiles = PromptFormatting.PickLikelyRelevantFiles(
            request.RepositoryFiles,
            request.SearchHits.Select(x => x.RelativePath),
            "Pages/Index.razor",
            "Workflows",
            "Requirements");

        return PromptFormatting.BuildPrompt(
            request.WorkflowCode,
            request.WorkflowName,
            request.WorkflowPurpose ?? string.Empty,
            [
                "This is a DESIGN workflow.",
                "Build on the approved analysis and define the solution approach.",
                "Do NOT produce implementation code or backlog execution details."
            ],
            new Dictionary<string, string?>
            {
                ["Workflow run id"] = request.WorkflowRunId.ToString(),
                ["Target solution id"] = request.TargetSolutionId.ToString(),
                ["Requirement id"] = request.RequirementId.ToString(),
                ["Analysis workflow run id"] = request.AnalysisWorkflowRunId.ToString(),
                ["Requirement title"] = request.RequirementTitle,
                ["Requirement description"] = request.RequirementDescription,
                ["Analysis summary"] = request.AnalysisSummary,
                ["Analysis status"] = request.AnalysisStatus
            },
            request.ProfileSummary ?? string.Empty,
            request.ProfileRules,
            request.SolutionKnowledgeDocuments,
            request.RepositoryFiles,
            likelyFiles,
            request.ExecutionRules);
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
