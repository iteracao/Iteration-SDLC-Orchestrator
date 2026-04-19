using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly ISolutionBridge _solutionBridge;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(
        string endpoint,
        string model,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        ISolutionBridge solutionBridge)
    {
        _endpoint = endpoint;
        _model = model;
        _logs = logs;
        _payloadStore = payloadStore;
        _solutionBridge = solutionBridge;
    }

    public async Task<SolutionAnalysisResult> AnalyzeAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agentDefinition);
        ArgumentNullException.ThrowIfNull(target);

        var instructions = BuildInstructions(agentDefinition);
        var phases = BuildPhases(request);
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

        var discoveryTools = new FileAwareAgentRunner.RepositoryDiscoveryTools(
            (path, token) => _solutionBridge.ListRepositoryTreeAsync(target, path, token),
            (query, path, token) => _solutionBridge.SearchFilesAsync(target, query, path, token));

        await _logs.AppendLineAsync(request.WorkflowRunId, "Agent multi-step prompts prepared.", ct);
        for (var i = 0; i < phases.Count; i++)
        {
            await _logs.AppendBlockAsync(request.WorkflowRunId, $"Phase {i + 1} prompt", phases[i].Prompt, ct);
        }

        try
        {
            var rawText = await FileAwareAgentRunner.RunMultiStepAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                instructions,
                phases,
                request.RepositoryPath,
                allowedPaths,
                request.WorkflowRunId,
                _logs,
                _payloadStore,
                ct,
                requiredFrameworkPaths,
                requiredSolutionPaths,
                requireRepositoryEvidence: true,
                requireRepositoryDiscovery: true,
                discoveryTools: discoveryTools);

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
        sb.AppendLine("- Return one JSON tool call object at a time when using a tool.");
        sb.AppendLine("- Tool call objects must use property name 'action', not 'tool'.");
        sb.AppendLine("- You MUST call get_workflow_input first.");
        sb.AppendLine("- You MUST read required framework context before analysis.");
        sb.AppendLine("- You MUST read required solution context before analysis.");
        sb.AppendLine("- In discovery phases, use list_repo_tree and search_repo to locate relevant evidence before reading files.");
        sb.AppendLine("- Use get_file or read_file only after discovery narrowed the evidence.");
        sb.AppendLine("- Save output only in the last phase via save_workflow_output.");
        sb.AppendLine("- The workflowRunId used in save_workflow_output MUST match the active workflow run.");
        sb.AppendLine("- The output object MUST satisfy every required field in the schema.");
        sb.AppendLine("- Do not include markdown fences or commentary outside the requested output.");
        return sb.ToString();
    }

    private static List<FileAwareAgentRunner.AgentPhaseDefinition> BuildPhases(SolutionAnalysisRequest request)
    {
        return
        [
            new(
                "load-required-context",
                BuildLoadContextPrompt(request),
                RequiresSavedOutput: false,
                AllowRepositoryDiscovery: false,
                PurposeSummary: "Load the workflow input plus mandatory framework and solution knowledge before repository analysis."),
            new(
                "discover-repository",
                BuildRepositoryDiscoveryPrompt(request),
                RequiresSavedOutput: false,
                AllowRepositoryDiscovery: true,
                PurposeSummary: "Use repository discovery tools to find the most relevant implementation evidence for this requirement."),
            new(
                "inspect-evidence",
                BuildEvidenceInspectionPrompt(request),
                RequiresSavedOutput: false,
                AllowRepositoryDiscovery: true,
                PurposeSummary: "Read the strongest repository evidence and summarize concrete findings, risks, and gaps."),
            new(
                "save-analysis-output",
                BuildFinalOutputPrompt(request),
                RequiresSavedOutput: true,
                AllowRepositoryDiscovery: false,
                PurposeSummary: "Assemble the final analysis output and save it only after all mandatory context and evidence are loaded.")
        ];
    }

    private static string BuildLoadContextPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKFLOW RUN ID:");
        sb.AppendLine(request.WorkflowRunId.ToString());
        sb.AppendLine();
        sb.AppendLine("STEP GOAL:");
        sb.AppendLine("Load the structured workflow input, mandatory framework rules, and mandatory solution knowledge.");
        sb.AppendLine();
        sb.AppendLine("DO THIS NOW:");
        sb.AppendLine("1. Call get_workflow_input.");
        sb.AppendLine("2. Read every required framework file listed below.");
        sb.AppendLine("3. Read every required solution knowledge file listed below.");
        sb.AppendLine("4. Then return a short plain-text summary of what context is now loaded and which areas need repository evidence.");
        sb.AppendLine();
        sb.AppendLine("REQUIRED FRAMEWORK FILES:");
        sb.AppendLine("- AI/framework/rules/sdlc/analyze.md");
        sb.AppendLine("- AI/framework/agents/solution-analyst/prompt.md");
        foreach (var file in request.ProfileRuleFiles)
        {
            sb.AppendLine($"- {file.Path}");
        }

        sb.AppendLine();
        sb.AppendLine("REQUIRED SOLUTION KNOWLEDGE FILES:");
        foreach (var file in request.SolutionKnowledgeFiles)
        {
            sb.AppendLine($"- {file.Path}");
        }

        sb.AppendLine();
        sb.AppendLine("IMPORTANT:");
        sb.AppendLine("- Do not use repository discovery tools yet.");
        sb.AppendLine("- Do not save final output in this step.");
        return sb.ToString();
    }

    private static string BuildRepositoryDiscoveryPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("STEP GOAL:");
        sb.AppendLine("Discover which repository files are most relevant to the requirement before reading implementation evidence.");
        sb.AppendLine();
        sb.AppendLine("DO THIS NOW:");
        sb.AppendLine("1. Use list_repo_tree and/or search_repo to narrow candidate folders and files.");
        sb.AppendLine("2. Focus on the smallest, strongest set of files that can prove current behavior or impacted areas.");
        sb.AppendLine("3. Then return a short plain-text discovery summary naming the priority files to inspect next.");
        sb.AppendLine();
        sb.AppendLine("DISCOVERY RULES:");
        sb.AppendLine("- Prefer targeted searches tied to the requirement language and known workflow concepts.");
        sb.AppendLine("- Prefer specific folders over reading many unrelated files.");
        sb.AppendLine("- You may search multiple times if needed.");
        sb.AppendLine("- Do not save final output in this step.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle);
        return sb.ToString();
    }

    private static string BuildEvidenceInspectionPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("STEP GOAL:");
        sb.AppendLine("Inspect the discovered repository evidence and extract grounded findings.");
        sb.AppendLine();
        sb.AppendLine("DO THIS NOW:");
        sb.AppendLine("1. Read the most relevant repository files using get_file or read_file.");
        sb.AppendLine("2. Cross-check behavior against the loaded workflow rules and solution docs.");
        sb.AppendLine("3. Then return a short plain-text evidence summary covering impacted areas, risks, assumptions, and open gaps.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Read at most 4 repository files in this step.");
        sb.AppendLine("- Prefer files already identified in discovery.");
        sb.AppendLine("- Prefer smaller, high-signal files over large files.");
        sb.AppendLine("- Stop reading as soon as you have enough grounded evidence.");
        sb.AppendLine("- Do not search again unless a discovered file path is clearly insufficient.");
        sb.AppendLine("- Do not save final output in this step.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle);
        return sb.ToString();
    }

    private static string BuildFinalOutputPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("STEP GOAL:");
        sb.AppendLine("Save the final analysis payload from the already loaded context and repository evidence.");
        sb.AppendLine();
        sb.AppendLine("FINAL OUTPUT EXPECTATIONS:");
        sb.AppendLine("- Identify impacted areas.");
        sb.AppendLine("- Identify risks.");
        sb.AppendLine("- Identify assumptions.");
        sb.AppendLine("- Identify gaps and ambiguities.");
        sb.AppendLine("- Generate open questions when information is missing.");
        sb.AppendLine("- Do NOT design the solution.");
        sb.AppendLine("- Do NOT create backlog items.");
        sb.AppendLine("- Do NOT describe implementation steps.");
        sb.AppendLine();
        sb.AppendLine("HARD RULES:");
        sb.AppendLine("- Do not call get_workflow_input in this step.");
        sb.AppendLine("- Do not call get_file or read_file in this step.");
        sb.AppendLine("- Do not call list_repo_tree or search_repo in this step.");
        sb.AppendLine("- Do not gather new evidence in this step.");
        sb.AppendLine("- Use only the context and repository evidence already loaded in previous steps.");
        sb.AppendLine("- Return exactly one JSON object.");
        sb.AppendLine("- The JSON object must use property name 'action', not 'tool'.");
        sb.AppendLine("- The JSON object must contain action, workflowRunId, and output.");
        sb.AppendLine("- Do not output plain text before or after the JSON object.");
        sb.AppendLine();
        sb.AppendLine("ACTION:");
        sb.AppendLine("Return ONLY save_workflow_output with the final workflow output object.");
        sb.AppendLine($"Use workflowRunId '{request.WorkflowRunId}'.");
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

    private static List<string> NormalizeStringList(List<string>? values, IReadOnlyList<string> fallbackValues)
    {
        var normalized = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return fallbackValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeStatus(string? status, int openQuestionCount)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return openQuestionCount > 0 ? "NeedsClarification" : "Completed";
        }

        return status.Trim();
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
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

    private sealed class WorkflowOutputPayload
    {
        public string Status { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<ArtifactPayload>? Artifacts { get; set; }
        public List<GeneratedRequirementPayload>? GeneratedRequirements { get; set; }
        public List<GeneratedOpenQuestionPayload>? GeneratedOpenQuestions { get; set; }
        public List<GeneratedDecisionPayload>? GeneratedDecisions { get; set; }
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

    private sealed class GeneratedRequirementPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string? Priority { get; set; }
    }

    private sealed class GeneratedOpenQuestionPayload
    {
        public string Question { get; set; } = string.Empty;
        public string? Context { get; set; }
        public string? RelatedRequirementTitle { get; set; }
    }

    private sealed class GeneratedDecisionPayload
    {
        public string Title { get; set; } = string.Empty;
        public string Decision { get; set; } = string.Empty;
        public string? Rationale { get; set; }
        public string? RelatedRequirementTitle { get; set; }
    }
}
