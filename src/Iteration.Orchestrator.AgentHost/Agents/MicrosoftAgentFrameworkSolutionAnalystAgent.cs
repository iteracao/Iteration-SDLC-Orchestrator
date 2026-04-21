using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
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
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
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

        await _logs.AppendLineAsync(request.WorkflowRunId, "Analyze workflow prepared for prompt-driven execution.", ct);

        try
        {
            var workflowDefinitionYaml = await LoadRequiredFrameworkTextAsync(
                request.WorkflowRunId,
                "AI/framework/workflows/analyze-request/workflow.yaml",
                () => _config.ReadFrameworkTextAsync("workflows/analyze-request/workflow.yaml", ct),
                ct);

            var workflowRulesMarkdown = await LoadRequiredFrameworkTextAsync(
                request.WorkflowRunId,
                "AI/framework/rules/sdlc/analyze.md",
                () => _config.ReadFrameworkTextAsync("rules/sdlc/analyze.md", ct),
                ct);

            var agentDefinitionYaml = await LoadRequiredFrameworkTextAsync(
                request.WorkflowRunId,
                "AI/framework/agents/solution-analyst/agent.yaml",
                () => _config.ReadFrameworkTextAsync("agents/solution-analyst/agent.yaml", ct),
                ct);

            var agentRulesMarkdown = await LoadRequiredFrameworkTextAsync(
                request.WorkflowRunId,
                "AI/framework/agents/solution-analyst/prompt.md",
                () => Task.FromResult(agentDefinition.PromptText),
                ct);

            var profile = await LoadRequiredProfileAsync(target.ProfileCode, request.WorkflowRunId, ct);
            var profileRules = profile.Rules
                .Where(rule => request.ProfileRuleFiles.Count == 0
                               || request.ProfileRuleFiles.Any(file => PathsEqual(file.Path, rule.Path)))
                .OrderBy(rule => rule.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var domainContextDocuments = await LoadDomainContextDocumentsAsync(request, target, ct);
            var visibleRepositoryFiles = await RepositoryPromptInputDiscovery.LoadVisibleRepositoryFilesAsync(
                request.RepositoryPath,
                ct);
            var repositoryStructureFiles = RepositoryPromptInputDiscovery.FilterExcludedStructurePaths(visibleRepositoryFiles);
            var inspectableFiles = RepositoryPromptInputDiscovery.GetInspectableTextFiles(visibleRepositoryFiles);

            if (repositoryStructureFiles.Count == 0)
            {
                throw new InvalidOperationException("Repository structure enumeration returned no visible files for analysis.");
            }

            var repositoryTree = RepositoryPromptInputDiscovery.FormatRepositoryTree(repositoryStructureFiles);
            var phases = new[]
            {
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 1",
                    Prompt: BuildBootstrapPrompt(
                        request,
                        workflowDefinitionYaml,
                        workflowRulesMarkdown,
                        agentDefinitionYaml,
                        agentRulesMarkdown,
                        profileRules,
                        domainContextDocuments),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Bootstrap the analyst with the current SDLC semantics, agent behavior, workflow definitions, and solution context."),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 2",
                    Prompt: BuildRepositoryStructurePrompt(repositoryTree),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Build repository structure awareness from the real gitignored file system index only."),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 3",
                    Prompt: BuildFinalAnalysisPrompt(request),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: true,
                    PurposeSummary: "Use targeted repository discovery and file reads to produce the final Markdown analysis report.",
                    RequireWorkflowInput: false,
                    RequireCompletionValidation: true)
            };

            var discoveryTools = new FileAwareAgentRunner.RepositoryDiscoveryTools(
                (scope, token) => Task.FromResult<IReadOnlyList<RepositoryEntry>>(
                    RepositoryPromptInputDiscovery.ListVisibleRepositoryTreeEntries(repositoryStructureFiles, scope)),
                (query, scope, token) => RepositoryPromptInputDiscovery.SearchVisibleRepositoryFilesAsync(
                    request.RepositoryPath,
                    inspectableFiles,
                    query,
                    scope,
                    token));

            var instructions = BuildInstructions(agentDefinition);
            var reportMarkdown = await FileAwareAgentRunner.RunMultiStepAsync(
                _endpoint,
                _model,
                agentDefinition.Name,
                instructions,
                phases,
                request.RepositoryPath,
                inspectableFiles,
                request.WorkflowRunId,
                _logs,
                _payloadStore,
                ct,
                requireRepositoryEvidence: true,
                requireRepositoryDiscovery: true,
                discoveryTools: discoveryTools,
                maxModelResponseSeconds: _maxModelResponseSeconds);

            var normalizedReportMarkdown = NormalizeMarkdown(reportMarkdown);
            var summary = ExtractSummary(normalizedReportMarkdown, request.RequirementTitle);
            var artifactsJson = BuildArtifactsJson(request.ProducedArtifacts);
            var recommendedNextWorkflowCodesJson = JsonSerializer.Serialize(
                NormalizeFallbackList(request.NextWorkflowCodes),
                JsonOptions);
            var knowledgeUpdatesJson = JsonSerializer.Serialize(
                NormalizeFallbackList(request.KnowledgeUpdates),
                JsonOptions);

            await _logs.AppendLineAsync(request.WorkflowRunId, "Prompt-driven analysis completed successfully.", ct);

            return new SolutionAnalysisResult(
                summary,
                "completed",
                artifactsJson,
                "[]",
                "[]",
                "[]",
                knowledgeUpdatesJson,
                knowledgeUpdatesJson,
                recommendedNextWorkflowCodesJson,
                normalizedReportMarkdown);
        }
        catch (Exception ex)
        {
            await _logs.AppendLineAsync(request.WorkflowRunId, "Agent execution failed.", CancellationToken.None);
            await _logs.AppendBlockAsync(request.WorkflowRunId, "Exception", ex.ToString(), CancellationToken.None);
            throw;
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

    private async Task<IReadOnlyList<TextDocumentInput>> LoadDomainContextDocumentsAsync(
        SolutionAnalysisRequest request,
        SolutionTarget target,
        CancellationToken ct)
    {
        var documents = new List<TextDocumentInput>();

        foreach (var file in request.SolutionKnowledgeFiles
                     .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var content = await _solutionBridge.ReadFileAsync(target, file.Path, ct);
                documents.Add(new TextDocumentInput(file.Path, content));
            }
            catch (Exception ex)
            {
                await _logs.AppendLineAsync(request.WorkflowRunId, $"[SKIP] {file.Path} -> {ex.Message}", ct);
            }
        }

        return documents;
    }

    private static string BuildInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("Execution mode:");
        sb.AppendLine("- This analyze workflow runs as a three-prompt sequence.");
        sb.AppendLine("- Prompt 1 and Prompt 2 are context/bootstrap prompts and should receive concise markdown responses only.");
        sb.AppendLine("- Prompt 3 is the final evidence-gathering prompt.");
        sb.AppendLine("- In Prompt 3, use tool calls when you need repository evidence, then finish with a plain Markdown report.");
        sb.AppendLine("- Do not call get_workflow_input or save_workflow_output.");
        sb.AppendLine("- Do not return JSON contracts, schemas, envelopes, or code-like payloads.");
        sb.AppendLine("- When using a tool, return exactly one JSON object with an 'action' property.");
        sb.AppendLine("- Allowed tool actions are read_file, get_file, list_repo_tree, and search_repo.");
        sb.AppendLine("- The final Prompt 3 response must be plain Markdown suitable to save directly as analysis-report.md.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildBootstrapPrompt(
        SolutionAnalysisRequest request,
        string workflowDefinitionYaml,
        string workflowRulesMarkdown,
        string agentDefinitionYaml,
        string agentRulesMarkdown,
        IReadOnlyList<TextDocumentInput> profileRules,
        IReadOnlyList<TextDocumentInput> domainContextDocuments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 1 of 3 for analyze-request.");
        sb.AppendLine("Purpose: bootstrap the agent with SDLC meaning, workflow semantics, agent behavior, and current domain context.");
        sb.AppendLine("Do not inspect repository files yet.");
        sb.AppendLine("Do not analyze the requirement yet.");
        sb.AppendLine("Return a short Markdown note with these sections only:");
        sb.AppendLine("- `## Workflow Intent`");
        sb.AppendLine("- `## Boundaries To Preserve`");
        sb.AppendLine("- `## Domain Anchors`");
        sb.AppendLine();
        sb.AppendLine("WORKFLOW METADATA");
        sb.AppendLine($"- Code: {request.WorkflowCode}");
        sb.AppendLine($"- Name: {request.WorkflowName}");
        sb.AppendLine($"- Purpose: {request.WorkflowPurpose}");
        sb.AppendLine();
        AppendDocumentBlock(sb, "SDLC framework context", "AI/framework/workflows/analyze-request/workflow.yaml", workflowDefinitionYaml);
        AppendDocumentBlock(sb, "Current workflow rules", "AI/framework/rules/sdlc/analyze.md", workflowRulesMarkdown);
        AppendDocumentBlock(sb, "Current agent definition", "AI/framework/agents/solution-analyst/agent.yaml", agentDefinitionYaml);
        AppendDocumentBlock(sb, "Current agent rules", "AI/framework/agents/solution-analyst/prompt.md", agentRulesMarkdown);

        if (profileRules.Count > 0)
        {
            sb.AppendLine("PROFILE RULES");
            foreach (var rule in profileRules)
            {
                AppendDocumentBlock(sb, "Profile rule", rule.Path, rule.Content);
            }
        }

        if (domainContextDocuments.Count > 0)
        {
            sb.AppendLine("RELEVANT DOMAIN CONTEXT");
            foreach (var document in domainContextDocuments)
            {
                AppendDocumentBlock(sb, "Domain context", document.Path, document.Content);
            }
        }
        else
        {
            sb.AppendLine("RELEVANT DOMAIN CONTEXT");
            sb.AppendLine("- No solution knowledge documents were available.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildRepositoryStructurePrompt(string repositoryTree)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 2 of 3 for analyze-request.");
        sb.AppendLine("Purpose: provide structure awareness from the real repository file system shape only.");
        sb.AppendLine("This is a structure/tree/index prompt only.");
        sb.AppendLine("Do not infer code behavior from file names alone.");
        sb.AppendLine("Do not request file contents in this prompt.");
        sb.AppendLine("The tree below is a deterministic repository enumeration filtered by .gitignore.");
        sb.AppendLine("Additional exclusions applied for this structure prompt:");
        sb.AppendLine("- `AI/contracts/**`");
        sb.AppendLine("- `AI/framework/**`");
        sb.AppendLine();
        sb.AppendLine("Return a short Markdown note with these sections only:");
        sb.AppendLine("- `## Likely Areas To Inspect`");
        sb.AppendLine("- `## Search Starting Points`");
        sb.AppendLine("- `## Structure-Only Caveats`");
        sb.AppendLine();
        sb.AppendLine("REAL REPOSITORY STRUCTURE");
        sb.AppendLine("```text");
        sb.AppendLine(repositoryTree);
        sb.AppendLine("```");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalAnalysisPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 3 of 3 for analyze-request.");
        sb.AppendLine("Purpose: investigate the requirement with targeted repository evidence gathering and return the final Markdown analysis report.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TO ANALYZE");
        sb.AppendLine($"- Title: {request.RequirementTitle}");
        sb.AppendLine("- Description:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("TOOL USAGE INSTRUCTIONS");
        sb.AppendLine("- Start from the requirement.");
        sb.AppendLine("- Use the repository structure already provided to identify likely impacted areas.");
        sb.AppendLine("- Use repository tools to inspect relevant files before concluding.");
        sb.AppendLine("- Prefer targeted exploration, not random reading.");
        sb.AppendLine("- Do not try to read everything.");
        sb.AppendLine("- Use `search_repo` to narrow likely candidates first.");
        sb.AppendLine("- Use `list_repo_tree` when you need to inspect a specific folder more closely.");
        sb.AppendLine("- Use `read_file` or `get_file` to confirm implementation details in the most relevant files.");
        sb.AppendLine("- Do not invent code behavior without reading evidence.");
        sb.AppendLine("- Be explicit when evidence is incomplete, conflicting, or missing.");
        sb.AppendLine("- Stay in analysis mode only. Do not design the solution or plan implementation.");
        sb.AppendLine();
        sb.AppendLine("FINAL RESPONSE RULES");
        sb.AppendLine("- Return plain Markdown only.");
        sb.AppendLine("- Do not return JSON.");
        sb.AppendLine("- Do not wrap the report in markdown fences.");
        sb.AppendLine("- The final Markdown is saved directly as `analysis-report.md`.");
        sb.AppendLine("- Ground claims in the repository files and context already provided.");
        sb.AppendLine();
        sb.AppendLine("BOOTSTRAP MARKDOWN TEMPLATE");
        sb.AppendLine(BuildReportTemplate());
        return sb.ToString().TrimEnd();
    }

    private static string BuildReportTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Analysis Report");
        sb.AppendLine();
        sb.AppendLine("## Requirement");
        sb.AppendLine("- Title: <requirement title>");
        sb.AppendLine("- Description: <requirement description>");
        sb.AppendLine();
        sb.AppendLine("## Executive Summary");
        sb.AppendLine("<2-4 concise paragraphs summarizing what the requirement appears to mean in the current system and the most important findings.>");
        sb.AppendLine();
        sb.AppendLine("## Current System Evidence");
        sb.AppendLine("- <grounded observation with file path>");
        sb.AppendLine();
        sb.AppendLine("## Likely Impacted Areas");
        sb.AppendLine("- <area and why>");
        sb.AppendLine();
        sb.AppendLine("## Risks And Constraints");
        sb.AppendLine("- <risk or constraint>");
        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        sb.AppendLine("- <assumption>");
        sb.AppendLine();
        sb.AppendLine("## Unknowns And Evidence Gaps");
        sb.AppendLine("- <unknown or missing evidence>");
        sb.AppendLine();
        sb.AppendLine("## Recommended Next Workflow");
        sb.AppendLine("- <likely next workflow code and why>");
        sb.AppendLine();
        sb.AppendLine("## Evidence Reviewed");
        sb.AppendLine("- <file path>");
        return sb.ToString().TrimEnd();
    }

    private static void AppendDocumentBlock(StringBuilder sb, string title, string path, string content)
    {
        sb.AppendLine(title.ToUpperInvariant());
        sb.AppendLine($"Path: {path}");
        sb.AppendLine("```text");
        sb.AppendLine(content.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static string NormalizeMarkdown(string raw)
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

        return trimmed[(firstNewLine + 1)..lastFence].Trim();
    }

    private static string ExtractSummary(string reportMarkdown, string requirementTitle)
    {
        var executiveSummary = TryExtractSection(reportMarkdown, "Executive Summary");
        var normalizedExecutiveSummary = NormalizeParagraph(executiveSummary);
        if (!string.IsNullOrWhiteSpace(normalizedExecutiveSummary))
        {
            return normalizedExecutiveSummary;
        }

        var firstParagraph = reportMarkdown
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeParagraph)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
                                     && !value.StartsWith("#", StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(firstParagraph)
            ? $"Analysis completed for '{requirementTitle}'."
            : firstParagraph;
    }

    private static string TryExtractSection(string markdown, string heading)
    {
        var lines = markdown.Split(['\r', '\n'], StringSplitOptions.None);
        var sb = new StringBuilder();
        var inSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = line.Equals($"## {heading}", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString().Trim();
    }

    private static string NormalizeParagraph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildArtifactsJson(IReadOnlyList<WorkflowArtifactDefinition> producedArtifacts)
    {
        var artifacts = producedArtifacts.Count > 0
            ? producedArtifacts
                .Where(artifact => !string.IsNullOrWhiteSpace(artifact.Type) && !string.IsNullOrWhiteSpace(artifact.Name))
                .Select(artifact => new ArtifactRecord(
                    artifact.Type.Trim(),
                    artifact.Name.Trim(),
                    "analysis-report.md"))
                .ToArray()
            : [new ArtifactRecord("analysis-report", "Analysis Report", "analysis-report.md")];

        return JsonSerializer.Serialize(artifacts, JsonOptions);
    }

    private static IReadOnlyList<string> NormalizeFallbackList(IReadOnlyList<string> values)
    {
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0 ? normalized : Array.Empty<string>();
    }

    private static bool PathsEqual(string left, string right)
        => NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').Trim();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ArtifactRecord(string ArtifactType, string Name, string Path);
}
