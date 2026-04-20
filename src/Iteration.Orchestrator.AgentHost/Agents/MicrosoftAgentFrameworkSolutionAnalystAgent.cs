using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private const int MaxLoadedFileCharacters = 3000;
    private const int MaxSummaryCharacters = 260;
    private const int MaxSummaryFacts = 3;
    private const int MaxRelevantRepositoryPaths = 6;

    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly ISolutionBridge _solutionBridge;
    private readonly IConfigCatalog _config;
    private readonly int _maxModelResponseSeconds;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(
        string endpoint,
        string model,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        ISolutionBridge solutionBridge,
        IConfigCatalog config,
        int maxModelResponseSeconds)
    {
        _endpoint = endpoint;
        _model = model;
        _logs = logs;
        _payloadStore = payloadStore;
        _solutionBridge = solutionBridge;
        _config = config;
        _maxModelResponseSeconds = maxModelResponseSeconds;
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

        var reviewInstructions = BuildDirectReviewInstructions();
        var finalInstructions = BuildDirectFinalAnalysisInstructions(agentDefinition);
        var sourceSummaries = new List<LoadedSourceSummary>();

        await _logs.AppendLineAsync(request.WorkflowRunId, "Agent sequential review prepared.", ct);

        try
        {
            var workflowInput = await _payloadStore.GetInputAsync(request.WorkflowRunId, ct);
            var workflowInputSummary = await SummarizeWorkflowInputAsync(
                request,
                "workflow-input",
                workflowInput.InputPayloadJson,
                ct);
            sourceSummaries.Add(workflowInputSummary);

            await LoadCriticalGuidanceAsync(request, agentDefinition, target, sourceSummaries, ct);

            foreach (var path in GetRequiredSolutionPaths(request))
            {
                var summary = await SafeSummarizeSolutionKnowledgeAsync(
                    request,
                    target,
                    path,
                    ct);

                sourceSummaries.Add(summary);
            }

            foreach (var path in GetRelevantRepositoryPaths(request))
            {
                if (IsIgnoredFile(path))
                {
                    continue;
                }

                var summary = await SafeSummarizeRepositoryEvidenceAsync(
                    request,
                    target,
                    reviewInstructions,
                    path,
                    ct);

                sourceSummaries.Add(summary);
            }

            var finalPrompt = BuildFinalPrompt(request, sourceSummaries);
            var rawText = await FileAwareAgentRunner.RunPromptAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                finalInstructions,
                finalPrompt,
                request.WorkflowRunId,
                _logs,
                "final-analysis",
                ct,
                _maxModelResponseSeconds);

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
        ICollection<LoadedSourceSummary> sourceSummaries,
        CancellationToken ct)
    {
        var analyzeRules = await LoadRequiredFrameworkTextAsync(
            request.WorkflowRunId,
            "AI/framework/rules/sdlc/analyze.md",
            () => _config.ReadFrameworkTextAsync("rules/sdlc/analyze.md", ct),
            ct);

        sourceSummaries.Add(BuildDeterministicSummary(
            "AI/framework/rules/sdlc/analyze.md",
            "framework",
            "constraint",
            analyzeRules));

        var agentPrompt = await LoadRequiredFrameworkTextAsync(
            request.WorkflowRunId,
            "AI/framework/agents/solution-analyst/prompt.md",
            () => Task.FromResult(agentDefinition.PromptText),
            ct);

        sourceSummaries.Add(BuildDeterministicSummary(
            "AI/framework/agents/solution-analyst/prompt.md",
            "framework",
            "constraint",
            agentPrompt,
            "get_workflow_input",
            "save_workflow_output",
            "workflowRunId",
            "Return valid JSON tool calls only"));

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
            sourceSummaries.Add(BuildDeterministicSummary(
                rule.Path,
                "profile",
                "constraint",
                rule.Content));
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

    private async Task<LoadedSourceSummary> SummarizeWorkflowInputAsync(
        SolutionAnalysisRequest request,
        string sourceName,
        string sourceContent,
        CancellationToken ct)
    {
        var summary = BuildWorkflowInputSummary(request);
        await LogSyntheticSummaryAsync(request, sourceName, sourceContent, "Workflow input scope prepared in code.", summary, ct);
        return summary;
    }

    private async Task<LoadedSourceSummary> SafeSummarizeSolutionKnowledgeAsync(
        SolutionAnalysisRequest request,
        SolutionTarget target,
        string path,
        CancellationToken ct)
    {
        try
        {
            var content = await ReadRepositoryFileAsync(target, path, ct);
            if (TryBuildPlaceholderKnowledgeSummary(content, out var placeholderSummary))
            {
                placeholderSummary = placeholderSummary with { Source = path };
                await _logs.AppendLineAsync(request.WorkflowRunId, $"[PLACEHOLDER CONTEXT] {path} -> solution knowledge file is blank or template-only.", ct);
                await LogSyntheticSummaryAsync(request, path, content, "Solution knowledge placeholder detected in code.", placeholderSummary, ct);
                return placeholderSummary;
            }

            var summary = BuildDeterministicSummary(path, "solution-knowledge", "context", content);
            await LogSyntheticSummaryAsync(request, path, content, "Solution knowledge summarized in code.", summary, ct);
            return summary;
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, $"[SKIP] {path} -> {ex.Message}", ct);
            return CreateSkippedSummary(path, "solution-knowledge");
        }
    }

    private async Task<LoadedSourceSummary> SafeSummarizeRepositoryEvidenceAsync(
        SolutionAnalysisRequest request,
        SolutionTarget target,
        string reviewInstructions,
        string path,
        CancellationToken ct)
    {
        try
        {
            var content = await ReadRepositoryFileAsync(target, path, ct);
            return await ReviewRepositorySourceAsync(request, reviewInstructions, path, content, ct);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, $"[SKIP] {path} -> {ex.Message}", ct);
            return CreateSkippedSummary(path, "repository");
        }
    }

    private async Task<LoadedSourceSummary> ReviewRepositorySourceAsync(
        SolutionAnalysisRequest request,
        string instructions,
        string sourceName,
        string sourceContent,
        CancellationToken ct)
    {
        var prompt = BuildRepositoryReviewPrompt(request, sourceName, sourceContent);
        var rawText = await FileAwareAgentRunner.RunPromptAsync(
            _endpoint,
            _model,
            "SolutionAnalystSourceReview",
            instructions,
            prompt,
            request.WorkflowRunId,
            _logs,
            $"review-source:{sourceName}",
            ct,
            _maxModelResponseSeconds);

        return ParseRepositoryReview(request, sourceName, rawText);
    }

    private async Task LogSyntheticSummaryAsync(
        SolutionAnalysisRequest request,
        string sourceName,
        string sourceContent,
        string note,
        LoadedSourceSummary summary,
        CancellationToken ct)
    {
        var logTitle = $"review-source:{sourceName}";
        var prompt = BuildSyntheticSummaryPrompt(sourceName, sourceContent, note);
        await _logs.AppendSectionAsync(request.WorkflowRunId, logTitle, ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, $"Prompt: {logTitle}", prompt, ct);
        await _logs.AppendBlockAsync(request.WorkflowRunId, $"Response: {logTitle}", FormatLoadedSourceSummary(summary), ct);
    }

    private async Task<string> ReadRepositoryFileAsync(SolutionTarget target, string path, CancellationToken ct)
    {
        var content = await _solutionBridge.ReadFileAsync(target, path, ct);
        return content.Length <= MaxLoadedFileCharacters
            ? content
            : content[..MaxLoadedFileCharacters] + $"\n\n[TRUNCATED BY SERVER: file content exceeded {MaxLoadedFileCharacters} characters]";
    }

    private static string BuildDirectReviewInstructions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are performing one direct source review for an analyze-request workflow.");
        sb.AppendLine("This is not a tool loop.");
        sb.AppendLine("- Follow only the current prompt.");
        sb.AppendLine("- Return only the JSON object requested by the prompt.");
        sb.AppendLine("- Do not invent files, endpoints, or behaviors that are not present in the provided input.");
        sb.AppendLine("- Do not include markdown fences.");
        sb.AppendLine("- Keep the response compact and grounded in the provided source.");
        sb.AppendLine("- Facts must be observations from the source, not recommendations.");
        return sb.ToString();
    }

    private static string BuildDirectFinalAnalysisInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the Solution Analyst producing the final analysis payload for one direct model call.");
        sb.AppendLine("This is not a tool loop.");
        sb.AppendLine("Return only the final JSON object that satisfies this contract:");
        sb.AppendLine(agentDefinition.OutputSchemaJson.Trim());
        sb.AppendLine();
        sb.AppendLine("FINAL RULES:");
        sb.AppendLine("- Do not output tool calls, markdown fences, or commentary.");
        sb.AppendLine("- The 'summary' field must be a single string, not an object or array.");
        sb.AppendLine("- Stay in analysis mode only.");
        sb.AppendLine("- Do not design the solution.");
        sb.AppendLine("- Do not create backlog items.");
        sb.AppendLine("- Do not describe implementation steps.");
        sb.AppendLine("- Keep facts, assumptions, and unknowns clearly separated inside the summary string.");
        sb.AppendLine("- Use generatedOpenQuestions for genuine unknowns or missing information.");
        sb.AppendLine("- Do not invent requirements, questions, or decisions when evidence does not support them.");
        return sb.ToString();
    }

    private static IEnumerable<string> GetRequiredSolutionPaths(SolutionAnalysisRequest request)
        => request.SolutionKnowledgeFiles
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> GetRelevantRepositoryPaths(SolutionAnalysisRequest request)
    {
        var evidenceHitSet = request.RepositoryEvidenceFiles
            .Select(x => NormalizePath(x.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return request.RepositoryEvidenceFiles
            .Select(x => x.Path)
            .Concat(request.RepositoryFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                Path = path,
                Score = ScoreRepositoryPath(path, request, evidenceHitSet.Contains(NormalizePath(path)))
            })
            .Where(candidate => candidate.Score > 0 && !IsIgnoredFile(candidate.Path))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRelevantRepositoryPaths)
            .Select(candidate => candidate.Path);
    }

    private static bool IsIgnoredFile(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string BuildRepositoryReviewPrompt(
        SolutionAnalysisRequest request,
        string sourceName,
        string sourceContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TASK:");
        sb.AppendLine("Classify this repository source for analysis relevance and return a compact JSON review.");
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
        sb.AppendLine("SOURCE SELECTION HINTS:");
        foreach (var hint in BuildRepositorySelectionHints(sourceName, request))
        {
            sb.AppendLine($"- {hint}");
        }

        sb.AppendLine();
        sb.AppendLine("RETURN ONLY JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"relevance\": \"direct|pattern|shared|context|low\",");
        sb.AppendLine("  \"summary\": \"short explanation\",");
        sb.AppendLine("  \"facts\": [\"fact 1\", \"fact 2\"]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- direct = the file likely implements the requested area directly.");
        sb.AppendLine("- pattern = the file is a reference feature or example the new change should match.");
        sb.AppendLine("- shared = the file is shared layout/component/navigation/security/API context that shapes multiple features.");
        sb.AppendLine("- context = secondary background that still helps analysis.");
        sb.AppendLine("- low = mostly unrelated or too weak to rely on.");
        sb.AppendLine("- Shared CRUD/layout/navigation files must prefer shared, not direct.");
        sb.AppendLine("- If the requirement explicitly references another feature/page/module as the thing to match, classify files from that referenced feature as pattern, not low, even when the entity differs.");
        sb.AppendLine("- Different entity name alone must not downgrade relevance when the requirement says to match that referenced feature.");
        sb.AppendLine("- Facts must be concrete observations from this source only.");
        sb.AppendLine("- Never repeat SOURCE SELECTION HINTS as facts unless the source content itself supports them.");
        sb.AppendLine("- Keep the response compact.");
        return sb.ToString();
    }

    private static string BuildFinalPrompt(SolutionAnalysisRequest request, IReadOnlyList<LoadedSourceSummary> sourceSummaries)
    {
        var strongEvidence = sourceSummaries
            .Where(summary => !summary.IsWeakEvidence)
            .ToList();
        var weakEvidence = sourceSummaries
            .Where(summary => summary.IsWeakEvidence)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Produce the final analysis payload as a JSON object.");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW:");
        sb.AppendLine($"- code: {request.WorkflowCode}");
        sb.AppendLine($"- name: {request.WorkflowName}");
        sb.AppendLine($"- purpose: {NormalizeShortSummary(request.WorkflowPurpose)}");
        sb.AppendLine();
        sb.AppendLine("ALLOWED NEXT WORKFLOW CODES:");
        foreach (var workflowCode in request.NextWorkflowCodes.Count > 0 ? request.NextWorkflowCodes : [request.WorkflowCode])
        {
            sb.AppendLine($"- {workflowCode}");
        }

        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TITLE:");
        sb.AppendLine(request.RequirementTitle);
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT DESCRIPTION:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("PRIMARY EVIDENCE:");
        foreach (var summary in strongEvidence)
        {
            sb.AppendLine(FormatLoadedSourceSummary(summary));
        }

        if (weakEvidence.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WEAK OR PLACEHOLDER CONTEXT:");
            foreach (var summary in weakEvidence)
            {
                sb.AppendLine($"- [{summary.Category}/{summary.Relevance}] {summary.Source}: {summary.Summary}");
            }
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
        sb.AppendLine("- Base the analysis only on the evidence listed above.");
        sb.AppendLine("- Favor repository evidence over weak or placeholder context.");
        sb.AppendLine("- The 'summary' value must be a single string.");
        sb.AppendLine("- Format the summary string with explicit sections, for example: 'Facts: ... Assumptions: ... Unknowns: ...'.");
        sb.AppendLine("- recommendedNextWorkflowCodes may only contain values from ALLOWED NEXT WORKFLOW CODES.");
        sb.AppendLine("- Generate open questions only when the evidence shows a real ambiguity, contradiction, missing dependency, or missing requirement detail.");
        sb.AppendLine("- Do not generate generic auth, navigation, or permission questions unless the evidence explicitly raises that uncertainty.");
        sb.AppendLine("- Do not design the solution.");
        sb.AppendLine("- Do not create backlog items.");
        sb.AppendLine("- Do not describe implementation steps.");
        return sb.ToString();
    }

    private static LoadedSourceSummary BuildWorkflowInputSummary(SolutionAnalysisRequest request)
    {
        var description = NormalizeShortSummary(request.RequirementDescription);
        var impactedAreas = GetLikelyRepositoryAreas(request).ToList();
        var facts = new List<string>();

        var sb = new StringBuilder();
        sb.Append($"Requirement '{request.RequirementTitle}' requests: {description}");

        if (impactedAreas.Count > 0)
        {
            sb.Append(" Likely impacted repository areas: ");
            sb.Append(string.Join(", ", impactedAreas));
            sb.Append('.');
            facts.AddRange(impactedAreas.Select(area => $"Likely impacted area: {area}"));
        }
        else
        {
            sb.Append(" Likely impacted areas should be inferred from repository features, API controllers, layout/navigation, and shared CRUD/validation components.");
        }

        return new LoadedSourceSummary(
            "workflow-input",
            "workflow-input",
            "scope",
            NormalizeShortSummary(sb.ToString()),
            NormalizeFacts(facts),
            false);
    }

    private static IEnumerable<string> GetLikelyRepositoryAreas(SolutionAnalysisRequest request)
        => request.RepositoryFiles
            .Select(path => new { Path = path, Score = ScoreRepositoryPath(path, request, false) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => NormalizeArea(candidate.Path))
            .Where(area => !string.IsNullOrWhiteSpace(area))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

    private static int ScoreRepositoryPath(string path, SolutionAnalysisRequest request, bool isRepositoryEvidenceHit)
    {
        var normalizedPath = NormalizePath(path);
        var lowerPath = normalizedPath.ToLowerInvariant();

        if ((normalizedPath.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.StartsWith("AI/", StringComparison.OrdinalIgnoreCase)) is false ||
            lowerPath.Contains("/bin/", StringComparison.Ordinal) ||
            lowerPath.Contains("/obj/", StringComparison.Ordinal))
        {
            return 0;
        }

        var score = 0;
        var queryText = $"{request.RequirementTitle} {request.RequirementDescription}".ToLowerInvariant();

        if (isRepositoryEvidenceHit)
        {
            score += 12;
        }

        foreach (var token in Tokenize(queryText))
        {
            if (lowerPath.Contains(token, StringComparison.Ordinal))
            {
                score += 3;
            }
        }

        if (IsPatternMatchingRequirement(queryText))
        {
            if (lowerPath.Contains("/pages/", StringComparison.Ordinal) || lowerPath.EndsWith(".razor", StringComparison.Ordinal))
            {
                score += 4;
            }

            if (lowerPath.Contains("/components/", StringComparison.Ordinal) ||
                lowerPath.Contains("/forms/", StringComparison.Ordinal) ||
                lowerPath.Contains("/crud/", StringComparison.Ordinal) ||
                lowerPath.Contains("/shared/", StringComparison.Ordinal))
            {
                score += 4;
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

        if (queryText.Contains("crud", StringComparison.Ordinal) &&
            (lowerPath.Contains("/crud/", StringComparison.Ordinal) ||
             lowerPath.Contains("controller", StringComparison.Ordinal) ||
             lowerPath.Contains("form", StringComparison.Ordinal)))
        {
            score += 5;
        }

        if (queryText.Contains("layout", StringComparison.Ordinal) &&
            (lowerPath.Contains("layout", StringComparison.Ordinal) ||
             lowerPath.Contains("navigation", StringComparison.Ordinal) ||
             lowerPath.Contains("menu", StringComparison.Ordinal)))
        {
            score += 5;
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

    private static bool IsPatternMatchingRequirement(string queryText)
        => queryText.Contains("same as", StringComparison.Ordinal) ||
           queryText.Contains("same layout", StringComparison.Ordinal) ||
           queryText.Contains("same crud", StringComparison.Ordinal) ||
           queryText.Contains("identical", StringComparison.Ordinal) ||
           queryText.Contains("match", StringComparison.Ordinal) ||
           queryText.Contains("similar", StringComparison.Ordinal) ||
           queryText.Contains("mirror", StringComparison.Ordinal) ||
           queryText.Contains("like the", StringComparison.Ordinal);

    private static string NormalizeArea(string path)
    {
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(directory) ? path : directory;
    }

    private static bool TryBuildPlaceholderKnowledgeSummary(string content, out LoadedSourceSummary summary)
    {
        var substantiveLines = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        if (substantiveLines.Count == 0)
        {
            summary = new LoadedSourceSummary(
                "placeholder",
                "solution-knowledge",
                "low",
                "Placeholder solution knowledge file; no solution-specific facts are recorded here.",
                [],
                true);
            return true;
        }

        summary = default!;
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

    private static LoadedSourceSummary BuildDeterministicSummary(
        string sourceName,
        string category,
        string relevance,
        string content,
        params string[] excludedFragments)
    {
        var facts = ExtractMeaningfulLines(content, excludedFragments)
            .Take(MaxSummaryFacts)
            .ToArray();

        var summary = facts.Length > 0
            ? NormalizeShortSummary(string.Join("; ", facts))
            : "Low-value context.";

        return new LoadedSourceSummary(
            sourceName,
            category,
            relevance,
            summary,
            NormalizeFacts(facts),
            false);
    }

    private static IEnumerable<string> ExtractMeaningfulLines(string content, IReadOnlyList<string> excludedFragments)
    {
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = TrimListPrefix(trimmed);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (excludedFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return NormalizeShortSummary(normalized);
        }
    }

    private static string TrimListPrefix(string value)
    {
        var normalized = value.Trim();
        while (normalized.StartsWith("-", StringComparison.Ordinal) ||
               normalized.StartsWith("*", StringComparison.Ordinal) ||
               normalized.StartsWith("•", StringComparison.Ordinal))
        {
            normalized = normalized[1..].TrimStart();
        }

        return normalized;
    }

    private static LoadedSourceSummary CreateSkippedSummary(string sourceName, string category)
        => new(
            sourceName,
            category,
            "low",
            "Skipped because the source could not be loaded.",
            [],
            true);

    private static string BuildSyntheticSummaryPrompt(string sourceName, string sourceContent, string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DETERMINISTIC SUMMARY:");
        sb.AppendLine(note);
        sb.AppendLine();
        sb.AppendLine("SOURCE:");
        sb.AppendLine(sourceName);
        sb.AppendLine();
        sb.AppendLine("SOURCE CONTENT:");
        sb.AppendLine(sourceContent);
        return sb.ToString();
    }

    private static LoadedSourceSummary ParseRepositoryReview(SolutionAnalysisRequest request, string sourceName, string rawText)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SourceReviewPayload>(ExtractJsonObject(rawText), JsonOptions)
                ?? throw new InvalidOperationException("Repository review payload was empty.");

            var relevance = NormalizeRepositoryRelevance(payload.Relevance, sourceName, request);
            var facts = NormalizeFacts(payload.Facts);
            var summary = string.IsNullOrWhiteSpace(payload.Summary)
                ? (facts.Count > 0 ? NormalizeShortSummary(string.Join("; ", facts)) : "Repository source reviewed.")
                : NormalizeShortSummary(payload.Summary);

            return new LoadedSourceSummary(
                sourceName,
                "repository",
                relevance,
                summary,
                facts,
                string.Equals(relevance, "low", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return new LoadedSourceSummary(
                sourceName,
                "repository",
                "context",
                NormalizeShortSummary(rawText),
                [],
                false);
        }
    }

    private static string NormalizeRepositoryRelevance(string? relevance, string sourceName, SolutionAnalysisRequest request)
    {
        var normalized = relevance?.Trim().ToLowerInvariant() switch
        {
            "direct" => "direct",
            "pattern" => "pattern",
            "shared" => "shared",
            "context" => "context",
            "low" => "low",
            _ => "context"
        };

        var normalizedPath = NormalizePath(sourceName);
        var lowerPath = normalizedPath.ToLowerInvariant();
        var queryText = $"{request.RequirementTitle} {request.RequirementDescription}".ToLowerInvariant();
        var referenceFeatureTerms = ExtractReferenceFeatureTerms(queryText);
        var isSharedCrudOrLayout =
            lowerPath.Contains("/shared/", StringComparison.Ordinal) ||
            ((lowerPath.Contains("crud", StringComparison.Ordinal) ||
              lowerPath.Contains("layout", StringComparison.Ordinal) ||
              lowerPath.Contains("navigation", StringComparison.Ordinal) ||
              lowerPath.Contains("menu", StringComparison.Ordinal)) &&
             (lowerPath.Contains("/components/", StringComparison.Ordinal) ||
              lowerPath.Contains("/layouts/", StringComparison.Ordinal) ||
              lowerPath.Contains("/shared/", StringComparison.Ordinal)));
        var isReferencedFeaturePath = IsPatternMatchingRequirement(queryText) &&
            referenceFeatureTerms.Any(term => lowerPath.Contains(term, StringComparison.Ordinal));

        if (isSharedCrudOrLayout)
        {
            return "shared";
        }

        if (isReferencedFeaturePath)
        {
            return "pattern";
        }

        return normalized;
    }

    private static IReadOnlyList<string> BuildRepositorySelectionHints(string sourceName, SolutionAnalysisRequest request)
    {
        var hints = new List<string>();
        var normalizedPath = NormalizePath(sourceName);
        var lowerPath = normalizedPath.ToLowerInvariant();
        var queryText = $"{request.RequirementTitle} {request.RequirementDescription}".ToLowerInvariant();
        var evidenceHitPaths = request.RepositoryEvidenceFiles
            .Select(x => NormalizePath(x.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (evidenceHitPaths.Contains(normalizedPath))
        {
            hints.Add("Selected from repository search hits.");
        }

        if (IsPatternMatchingRequirement(queryText))
        {
            hints.Add("Requirement explicitly references matching an existing feature or behavior.");
        }

        foreach (var referenceFeature in ExtractReferenceFeatureTerms(queryText))
        {
            if (lowerPath.Contains(referenceFeature, StringComparison.Ordinal))
            {
                hints.Add($"Path matches explicitly referenced feature term '{referenceFeature}'.");
            }
        }

        if (lowerPath.Contains("/pages/", StringComparison.Ordinal) || lowerPath.EndsWith(".razor", StringComparison.Ordinal))
        {
            hints.Add("Path looks like UI page evidence.");
        }

        if (lowerPath.Contains("/components/", StringComparison.Ordinal) ||
            lowerPath.Contains("/forms/", StringComparison.Ordinal) ||
            lowerPath.Contains("/crud/", StringComparison.Ordinal) ||
            lowerPath.Contains("/shared/", StringComparison.Ordinal))
        {
            hints.Add("Path looks like shared UI/component/CRUD pattern evidence.");
        }

        if (lowerPath.Contains("/controllers/", StringComparison.Ordinal) || lowerPath.Contains("/api/", StringComparison.Ordinal))
        {
            hints.Add("Path looks like API/controller evidence.");
        }

        if (lowerPath.Contains("layout", StringComparison.Ordinal) ||
            lowerPath.Contains("navigation", StringComparison.Ordinal) ||
            lowerPath.Contains("menu", StringComparison.Ordinal))
        {
            hints.Add("Path looks like layout or navigation evidence.");
        }

        foreach (var token in Tokenize(queryText).Where(token => lowerPath.Contains(token, StringComparison.Ordinal)).Take(3))
        {
            hints.Add($"Path matches requirement token '{token}'.");
        }

        return hints.Count > 0 ? hints : ["Selected because the path likely overlaps the requested area."];
    }

    private static IReadOnlyList<string> NormalizeFacts(IEnumerable<string>? facts)
        => (facts ?? [])
            .Where(fact => !string.IsNullOrWhiteSpace(fact))
            .Select(fact => NormalizeShortSummary(fact))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSummaryFacts)
            .ToArray();

    private static IReadOnlyList<string> ExtractReferenceFeatureTerms(string queryText)
    {
        var tokens = Tokenize(queryText).ToList();
        var referenceTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = new[]
        {
            "same as",
            "identical to",
            "match",
            "like the",
            "same layout as",
            "same crud as"
        };

        foreach (var anchor in anchors)
        {
            var index = queryText.IndexOf(anchor, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var trailingText = queryText[(index + anchor.Length)..];
            foreach (var token in Tokenize(trailingText).Take(3))
            {
                if (token.Length >= 4)
                {
                    referenceTerms.Add(token);
                }
            }
        }

        foreach (var token in tokens.Where(token => token is "users" or "user"))
        {
            referenceTerms.Add(token);
        }

        return referenceTerms.ToArray();
    }

    private static string FormatLoadedSourceSummary(LoadedSourceSummary summary)
    {
        var sb = new StringBuilder();
        sb.Append($"- [{summary.Category}/{summary.Relevance}] {summary.Source}: {summary.Summary}");
        if (summary.Facts.Count > 0)
        {
            sb.Append(" Facts: ");
            sb.Append(string.Join("; ", summary.Facts));
        }

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
        parsed.RecommendedNextWorkflowCodes = NormalizeAllowedWorkflowCodes(
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

    private static List<string> NormalizeAllowedWorkflowCodes(List<string>? values, IReadOnlyList<string> allowedValues)
    {
        var allowedSet = allowedValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalized = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(allowedSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (normalized.Count > 0)
        {
            return normalized;
        }

        return allowedSet.ToList();
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
        var cleaned = StripMarkdownCodeFence(raw).Trim();
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Agent did not return a JSON object. Raw response: {raw}");
        }

        return cleaned[start..(end + 1)];
    }

    private static string StripMarkdownCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return trimmed;
        }

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewLine)
        {
            return trimmed;
        }

        return trimmed[(firstNewLine + 1)..lastFence];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record LoadedSourceSummary(
        string Source,
        string Category,
        string Relevance,
        string Summary,
        IReadOnlyList<string> Facts,
        bool IsWeakEvidence);

    private sealed class SourceReviewPayload
    {
        public string? Relevance { get; set; }
        public string? Summary { get; set; }
        public List<string>? Facts { get; set; }
    }

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
