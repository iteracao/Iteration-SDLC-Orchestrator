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
            var visibleRepositoryFiles = await RepositoryPromptInputDiscovery.LoadVisibleRepositoryFilesAsync(
                request.RepositoryPath,
                ct);
            var repositoryStructureFiles = RepositoryPromptInputDiscovery.FilterExcludedStructurePaths(visibleRepositoryFiles);
            var inspectableFiles = RepositoryPromptInputDiscovery.GetInspectableTextFiles(visibleRepositoryFiles);
            var promptContextFiles = RepositoryPromptInputDiscovery.GetPromptContextFiles(
                request.RepositoryPath,
                target.Code,
                visibleRepositoryFiles);

            if (promptContextFiles.Count == 0)
            {
                throw new InvalidOperationException("Repository path enumeration returned no prompt context files for analysis.");
            }

            var phases = new[]
            {
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 1",
                    Prompt: BuildBootstrapPrompt(),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Establish analysis behavior and workflow intent only.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 2",
                    Prompt: BuildRepositoryStructurePrompt(),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Provide the deterministic full physical path list for source files and solution documentation.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.ContextOnly),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 3",
                    Prompt: BuildFinalAnalysisPrompt(request),
                    RequiresSavedOutput: false,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Read relevant files by full physical path and produce the final Markdown analysis report.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
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
                promptContextFiles,
                request.WorkflowRunId,
                _logs,
                _payloadStore,
                ct,
                requireRepositoryEvidence: true,
                requireRepositoryDiscovery: false,
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

    private static string BuildInstructions(AgentDefinition agentDefinition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agentDefinition.PromptText.Trim());
        sb.AppendLine();
        sb.AppendLine("Execution mode:");
        sb.AppendLine("- This analyze workflow runs as a three-prompt sequence.");
        sb.AppendLine("- Prompt 1 is behavior only.");
        sb.AppendLine("- Prompt 2 is context only.");
        sb.AppendLine("- Prompt 3 is the analysis step.");
        sb.AppendLine("- Do not call get_workflow_input or save_workflow_output.");
        sb.AppendLine("- When using a tool, return exactly one JSON object with an 'action' property.");
        sb.AppendLine("- Allowed tool actions are find_available_files and get_file.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildBootstrapPrompt()
        => """
This is Prompt 1 of 3 for analyze-request.

Purpose:
Establish analysis behavior and workflow intent.

You are the Solution Analyst.

This is an analysis workflow.
You must understand the requirement and the current system.
Do NOT design a solution.
Do NOT propose implementation steps.
Do NOT create backlog items.

You will:
- Use solution documentation as intended behavior/context
- Use repository source files as implementation evidence
- Prefer explicit unknowns over guessing
- Clearly separate facts, assumptions, and unknowns

Execution flow:
- Prompt 1: behavior only (this prompt)
- Prompt 2: repository and documentation awareness
- Prompt 3: requirement analysis and final report

For this prompt:
Return a very short Markdown note with:

## Workflow Intent
## Boundaries To Preserve
""";

    private static string BuildRepositoryStructurePrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 2 of 3 for analyze-request.");
        sb.AppendLine("Purpose: provide repository file access context for the next step.");
        sb.AppendLine();
        sb.AppendLine("Repository source files and target solution documentation are available through the tool `find_available_files`.");
        sb.AppendLine("That tool returns exact full physical paths for files available in this run.");
        sb.AppendLine("No response is required for this step.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalAnalysisPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 3 of 3 for analyze-request.");
        sb.AppendLine("Analyze the requirement against the repository and provided solution documentation, then return the final analysis report in Markdown.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TO ANALYZE");
        sb.AppendLine($"- Title: {request.RequirementTitle}");
        sb.AppendLine("- Description:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("TOOL USAGE");
        sb.AppendLine("- Start from the requirement.");
        sb.AppendLine("- Use `find_available_files` to obtain exact full physical paths available for this run.");
        sb.AppendLine("- Use `get_file` only with an exact full physical path returned by `find_available_files`.");
        sb.AppendLine("- Stay in analysis mode only. Do not design the solution or plan implementation.");
        return sb.ToString().TrimEnd();
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ArtifactRecord(string ArtifactType, string Name, string Path);
}
