using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private const int MaxLoadedFileCharacters = 4000;
    private const int MaxSummaryCharacters = 400;

    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly ISolutionBridge _solutionBridge;
    private readonly IConfigCatalog _config;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(
        string endpoint,
        string model,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        ISolutionBridge solutionBridge,
        IConfigCatalog config)
    {
        _endpoint = endpoint;
        _model = model;
        _logs = logs;
        _payloadStore = payloadStore;
        _solutionBridge = solutionBridge;
        _config = config;
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
        var sourceSummaries = new List<LoadedSourceSummary>();

        await _logs.AppendLineAsync(request.WorkflowRunId, "Agent sequential review prepared.", ct);

        try
        {
            var workflowInput = await _payloadStore.GetInputAsync(request.WorkflowRunId, ct);
            sourceSummaries.Add(new LoadedSourceSummary(
                "workflow-input",
                await SummarizeWorkflowInputAsync(
                    request,
                    agentDefinition,
                    "workflow-input",
                    workflowInput.InputPayloadJson,
                    ct)));

            await LoadCriticalGuidanceAsync(request, agentDefinition, target, instructions, sourceSummaries, ct);

            foreach (var path in GetRequiredSolutionPaths(request))
            {
                var summary = await SafeSummarizeSolutionKnowledgeAsync(
                    request,
                    agentDefinition,
                    target,
                    instructions,
                    path,
                    "Read this solution knowledge file and return a short plain-text summary focused on current solution truth and constraints relevant to this requirement.",
                    ct);

                sourceSummaries.Add(new LoadedSourceSummary(path, summary));
            }

            foreach (var path in GetRelevantRepositoryPaths(request))
            {
                if (IsIgnoredFile(path))
                {
                    continue;
                }

                var summary = await SafeSummarizeRepositoryEvidenceAsync(
                    request,
                    agentDefinition,
                    target,
                    instructions,
                    path,
                    "Read this repository file and return a short plain-text summary. Say whether it is relevant to the requirement and why.",
                    ct);

                sourceSummaries.Add(new LoadedSourceSummary(path, summary));
            }

            var finalPrompt = BuildFinalPrompt(request, sourceSummaries);
            var rawText = await FileAwareAgentRunner.RunPromptAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                instructions,
                finalPrompt,
                request.WorkflowRunId,
                _logs,
                "final-analysis",
                ct);

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

    private async Task LoadCriticalGuidanceAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        string instructions,
        ICollection<LoadedSourceSummary> sourceSummaries,
        CancellationToken ct)
    {
        var analyzeRules = await LoadRequiredFrameworkTextAsync(
            request.WorkflowRunId,
            "AI/framework/rules/sdlc/analyze.md",
            () => _config.ReadFrameworkTextAsync("rules/sdlc/analyze.md", ct),
            ct);

        sourceSummaries.Add(new LoadedSourceSummary(
            "AI/framework/rules/sdlc/analyze.md",
            await SummarizeSourceAsync(
                request,
                agentDefinition,
                instructions,
                "AI/framework/rules/sdlc/analyze.md",
                analyzeRules,
                "Read this framework rule file and return a short plain-text summary focused on analysis discipline, evidence standards, and what must not be skipped.",
                ct)));

        var agentPrompt = await LoadRequiredFrameworkTextAsync(
            request.WorkflowRunId,
            "AI/framework/agents/solution-analyst/prompt.md",
            () => Task.FromResult(agentDefinition.PromptText),
            ct);

        sourceSummaries.Add(new LoadedSourceSummary(
            "AI/framework/agents/solution-analyst/prompt.md",
            await SummarizeSourceAsync(
                request,
                agentDefinition,
                instructions,
                "AI/framework/agents/solution-analyst/prompt.md",
                agentPrompt,
                "Read this analyst agent prompt and return a short plain-text summary focused on what analysis output is expected and how the requirement should be examined.",
                ct)));

        var profile = await LoadRequiredProfileAsync(target.ProfileCode, request.WorkflowRunId, ct);
        var requiredProfilePaths = request.ProfileRuleFiles
            .Select(x => NormalizePath(x.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loadedProfileRules = profile.Rules
            .Where(rule => requiredProfilePaths.Count == 0 || requiredProfilePaths.Contains(NormalizePath(rule.Path), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var missingProfileRules = requiredProfilePaths
            .Where(requiredPath => !loadedProfileRules.Any(rule => NormalizePath(rule.Path).Equals(requiredPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missingProfileRules.Count > 0)
        {
            var message = $"Required profile rules could not be loaded from the framework profile source. Missing: {string.Join(", ", missingProfileRules)}";
            await _logs.AppendLineAsync(request.WorkflowRunId, $"[MISSING REQUIRED CONTEXT] {message}", ct);
            throw new InvalidOperationException(message);
        }

        foreach (var rule in loadedProfileRules)
        {
            var summary = await SummarizeSourceAsync(
                request,
                agentDefinition,
                instructions,
                rule.Path,
                rule.Content,
                "Read this profile rule file and return a short plain-text summary focused on engineering constraints that matter for this requirement.",
                ct);

            sourceSummaries.Add(new LoadedSourceSummary(rule.Path, summary));
        }
    }

    private async Task<ProfileDefinition> LoadRequiredProfileAsync(string profileCode, Guid workflowRunId, CancellationToken ct)
    {
        try
        {
            return await _config.GetProfileAsync(profileCode, ct);
        }
        catch (Exception ex)
        {
            var message = $"Required profile guidance could not be loaded for profile '{profileCode}'. {ex.Message}";
            await _logs.AppendLineAsync(workflowRunId, $"[MISSING REQUIRED CONTEXT] profile '{profileCode}' -> {ex.Message}", ct);
            throw new InvalidOperationException(message, ex);
        }
    }

    private async Task<string> LoadRequiredFrameworkTextAsync(
        Guid workflowRunId,
        string displayPath,
        Func<Task<string>> loadAsync,
        CancellationToken ct)
    {
        try
        {
            var content = await loadAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("Content is empty.");
            }

            return content;
        }
        catch (Exception ex)
        {
            var message = $"Required guidance could not be loaded: {displayPath}. {ex.Message}";
            await _logs.AppendLineAsync(workflowRunId, $"[MISSING REQUIRED CONTEXT] {displayPath} -> {ex.Message}", ct);
            throw new InvalidOperationException(message, ex);
        }
    }

    private async Task<string> SummarizeWorkflowInputAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        string sourceName,
        string sourceContent,
        CancellationToken ct)
    {
        const string taskInstruction = "Review the workflow input and return a short plain-text summary focused on analysis scope and what repository areas matter most.";
        var summary = BuildWorkflowInputSummary(request);
        await LogSyntheticSummaryAsync(
            request,
            agentDefinition,
            sourceName,
            sourceContent,
            taskInstruction,
            summary,
            ct);
        return summary;
    }

    private async Task<string> SafeSummarizeSolutionKnowledgeAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        string instructions,
        string path,
        string taskInstruction,
        CancellationToken ct)
    {
        try
        {
            var content = await ReadRepositoryFileAsync(target, path, ct);
            if (TryBuildPlaceholderKnowledgeSummary(content, out var placeholderSummary))
            {
                await _logs.AppendLineAsync(request.WorkflowRunId, $"[PLACEHOLDER CONTEXT] {path} -> solution knowledge file is blank or template-only.", ct);
                await LogSyntheticSummaryAsync(request, agentDefinition, path, content, taskInstruction, placeholderSummary, ct);
                return placeholderSummary;
            }

            return await SummarizeSourceAsync(request, agentDefinition, instructions, path, content, taskInstruction, ct);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, $"[SKIP] {path} -> {ex.Message}", ct);
            return $"Skipped (not found): {path}";
        }
    }

    private async Task<string> SafeSummarizeRepositoryEvidenceAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        SolutionTarget target,
        string instructions,
        string path,
        string taskInstruction,
        CancellationToken ct)
    {
        try
        {
            var content = await ReadRepositoryFileAsync(target, path, ct);
            return await SummarizeSourceAsync(request, agentDefinition, instructions, path, content, taskInstruction, ct);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, $"[SKIP] {path} -> {ex.Message}", ct);
            return $"Skipped (not found): {path}";
        }
    }

    private async Task<string> SummarizeSourceAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        string instructions,
        string sourceName,
        string sourceContent,
        string taskInstruction,
        CancellationToken ct)
    {
        var prompt = BuildSourceSummaryPrompt(request, sourceName, sourceContent, taskInstruction);
        var rawText = await FileAwareAgentRunner.RunPromptAsync(
            _endpoint,
            _model,
            agentDefinition.Name,
            instructions,
            prompt,
            request.WorkflowRunId,
            _logs,
            $"review-source:{sourceName}",
            ct);

        return NormalizeShortSummary(rawText);
    }

    private async Task LogSyntheticSummaryAsync(
        SolutionAnalysisRequest request,
        AgentDefinition agentDefinition,
        string sourceName,
        string sourceContent,
        string taskInstruction,
        string summary,
        CancellationToken ct)
    {
        var logTitle = $"review-source:{sourceName}";
        var prompt = BuildSourceSummaryPrompt(request, sourceName, sourceContent, taskInstruction);
        await _logs.AppendSectionAsync(request.WorkflowRunId, logTitle, ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, $"Prompt: {logTitle}", prompt, ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, $"Response: {logTitle}", summary, ct);
    }

    private async Task<string> ReadRepositoryFileAsync(SolutionTarget target, string path, CancellationToken ct)
    {
        var content = await _solutionBridge.ReadFileAsync(target, path, ct);
        return content.Length <= MaxLoadedFileCharacters
            ? content
            : content[..MaxLoadedFileCharacters] + $"\n\n[TRUNCATED BY SERVER: file content exceeded {MaxLoadedFileCharacters} characters]";
    }

    private static string BuildInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("OUTPUT CONTRACT:");
        sb.AppendLine(agentDefinition.OutputSchemaJson.Trim());
        sb.AppendLine();
        sb.AppendLine("GENERAL RULES:");
        sb.AppendLine("- Follow the exact task for the current prompt only.");
        sb.AppendLine("- When asked for a short plain-text summary, return only that short plain-text summary.");
        sb.AppendLine("- Do not invent files, endpoints, or behaviors that are not present in the provided input.");
        sb.AppendLine("- Do not include markdown fences.");
        sb.AppendLine("- Keep summaries concrete and grounded in the provided source.");
        sb.AppendLine("- If a source is blank or only a scaffold/template, call that out and treat it as low-value context.");
        return sb.ToString();
    }

    private static IEnumerable<string> GetRequiredSolutionPaths(SolutionAnalysisRequest request)
        => request.SolutionKnowledgeFiles
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> GetRelevantRepositoryPaths(SolutionAnalysisRequest request)
        => request.RepositoryFiles
            .Select(path => new { Path = path, Score = ScoreRepositoryPath(path, request) })
            .Where(candidate => candidate.Score > 0 && !IsIgnoredFile(candidate.Path))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20);

    private static bool IsIgnoredFile(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string BuildSourceSummaryPrompt(
        SolutionAnalysisRequest request,
        string sourceName,
        string sourceContent,
        string taskInstruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TASK:");
        sb.AppendLine(taskInstruction);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT DESCRIPTION:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("SOURCE:");
        sb.AppendLine(sourceName);
        sb.AppendLine();
        sb.AppendLine("SOURCE CONTENT:");
        sb.AppendLine(sourceContent);
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Return only a short plain-text summary.");
        sb.AppendLine("- Mention direct relevance to the requirement when applicable.");
        sb.AppendLine("- If the source is not relevant, say so briefly.");
        sb.AppendLine("- If the source is blank or only a heading/template scaffold, say it is placeholder knowledge and low-value context.");
        sb.AppendLine("- Do not output JSON.");
        return sb.ToString();
    }

    private static string BuildFinalPrompt(SolutionAnalysisRequest request, IReadOnlyList<LoadedSourceSummary> sourceSummaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produce the final analysis payload as a JSON object.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT DESCRIPTION:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("LOADED SOURCE SUMMARIES:");
        foreach (var summary in sourceSummaries)
        {
            sb.AppendLine($"- {summary.Source}: {summary.Summary}");
        }

        sb.AppendLine();
        sb.AppendLine("RETURN ONLY A JSON OBJECT WITH:");
        sb.AppendLine("- status");
        sb.AppendLine("- summary");
        sb.AppendLine("- artifacts");
        sb.AppendLine("- generatedRequirements");
        sb.AppendLine("- generatedOpenQuestions");
        sb.AppendLine("- generatedDecisions");
        sb.AppendLine("- documentationUpdates");
        sb.AppendLine("- knowledgeUpdates");
        sb.AppendLine("- recommendedNextWorkflowCodes");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Do not output markdown fences.");
        sb.AppendLine("- Do not output any text before or after the JSON object.");
        sb.AppendLine("- Base the analysis only on the loaded source summaries.");
        sb.AppendLine("- Treat summaries that say placeholder, blank, or low-value as weak evidence and favor repository evidence in those cases.");
        sb.AppendLine("- Do not design the solution.");
        sb.AppendLine("- Do not create backlog items.");
        sb.AppendLine("- Do not describe implementation steps.");
        return sb.ToString();
    }

    private static string BuildWorkflowInputSummary(SolutionAnalysisRequest request)
    {
        var description = NormalizeShortSummary(request.RequirementDescription);
        var impactedAreas = GetLikelyRepositoryAreas(request).ToList();

        var sb = new StringBuilder();
        sb.Append($"Requirement '{request.RequirementTitle}' requests: {description}");

        if (impactedAreas.Count > 0)
        {
            sb.Append(" Likely impacted repository areas: ");
            sb.Append(string.Join(", ", impactedAreas));
            sb.Append('.');
        }
        else
        {
            sb.Append(" Likely impacted areas should be inferred from repository features, API controllers, layout/navigation, and shared CRUD/validation components.");
        }

        return NormalizeShortSummary(sb.ToString());
    }

    private static IEnumerable<string> GetLikelyRepositoryAreas(SolutionAnalysisRequest request)
        => request.RepositoryFiles
            .Select(path => new { Path = path, Score = ScoreRepositoryPath(path, request) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => NormalizeArea(candidate.Path))
            .Where(area => !string.IsNullOrWhiteSpace(area))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

    private static int ScoreRepositoryPath(string path, SolutionAnalysisRequest request)
    {
        if (!path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var score = 0;
        var lowerPath = path.ToLowerInvariant();
        var queryText = $"{request.RequirementTitle} {request.RequirementDescription}".ToLowerInvariant();

        foreach (var token in Tokenize(queryText))
        {
            if (lowerPath.Contains(token, StringComparison.Ordinal))
            {
                score += 3;
            }
        }

        if ((queryText.Contains("navigation", StringComparison.Ordinal) || queryText.Contains("menu", StringComparison.Ordinal)) &&
            (lowerPath.Contains("navigation", StringComparison.Ordinal) || lowerPath.Contains("layout", StringComparison.Ordinal)))
        {
            score += 6;
        }

        if ((queryText.Contains("user", StringComparison.Ordinal) || queryText.Contains("users", StringComparison.Ordinal)) &&
            (lowerPath.Contains("/users/", StringComparison.Ordinal) || lowerPath.Contains("userscontroller", StringComparison.Ordinal)))
        {
            score += 8;
        }

        if (lowerPath.Contains("/pages/", StringComparison.Ordinal) || lowerPath.EndsWith("page.razor", StringComparison.Ordinal))
        {
            score += 3;
        }

        if (lowerPath.Contains("/components/", StringComparison.Ordinal) ||
            lowerPath.Contains("/forms/", StringComparison.Ordinal) ||
            lowerPath.Contains("/crud/", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (lowerPath.Contains("/controllers/", StringComparison.Ordinal) || lowerPath.Contains("/api/", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (lowerPath.Contains("/models/", StringComparison.Ordinal) || lowerPath.Contains("/validation/", StringComparison.Ordinal))
        {
            score += 1;
        }

        return score;
    }

    private static IEnumerable<string> Tokenize(string value)
        => value
            .Split([' ', '\r', '\n', '\t', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeArea(string path)
    {
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(directory) ? path : directory;
    }

    private static bool TryBuildPlaceholderKnowledgeSummary(string content, out string summary)
    {
        var substantiveLines = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        if (substantiveLines.Count == 0)
        {
            summary = "Placeholder solution knowledge file; no solution-specific facts are recorded here. Treat this as low-value context and rely on repository evidence.";
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim();

    private static string NormalizeShortSummary(string raw)
    {
        var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (cleaned.Length <= MaxSummaryCharacters)
        {
            return cleaned;
        }

        return cleaned[..MaxSummaryCharacters].TrimEnd() + "...";
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

    private sealed record LoadedSourceSummary(string Source, string Summary);

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
