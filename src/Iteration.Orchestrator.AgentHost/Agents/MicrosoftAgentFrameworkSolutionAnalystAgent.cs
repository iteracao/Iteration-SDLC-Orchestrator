using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Common;
using Iteration.Orchestrator.Domain.Solutions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class MicrosoftAgentFrameworkSolutionAnalystAgent : ISolutionAnalystAgent
{
    private const string RepositoryStateArtifactFileName = "repository-state.md";

    private readonly string _endpoint;
    private readonly string _model;
    private readonly IWorkflowRunLogStore _logs;
    private readonly IWorkflowPayloadStore _payloadStore;
    private readonly IArtifactStore _artifacts;
    private readonly int _maxModelResponseSeconds;

    public MicrosoftAgentFrameworkSolutionAnalystAgent(
        string endpoint,
        string model,
        IWorkflowRunLogStore logs,
        IWorkflowPayloadStore payloadStore,
        IArtifactStore artifacts,
        ISolutionBridge solutionBridge,
        IConfigCatalog config,
        int maxModelResponseSeconds)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:11434" : endpoint;
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5-coder:7b" : model;
        _logs = logs;
        _payloadStore = payloadStore;
        _artifacts = artifacts;
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
                    PurposeSummary: "Review the full repository documentation and source tree, then generate the saved repository-state Markdown.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireCompletionValidation: true,
                    SavedMarkdownArtifactFileName: RepositoryStateArtifactFileName,
                    InjectSavedMarkdownIntoNextPhase: true,
                    RequireAllAvailableFilesRead: true),
                new FileAwareAgentRunner.AgentPhaseDefinition(
                    Name: "Prompt 3",
                    Prompt: BuildFinalAnalysisPrompt(request),
                    RequiresSavedOutput: true,
                    AllowRepositoryDiscovery: false,
                    PurposeSummary: "Read relevant files by full physical path and produce the final Markdown analysis report.",
                    Mode: FileAwareAgentRunner.AgentPhaseMode.Interactive,
                    RequireWorkflowInput: false,
                    RequireCompletionValidation: true)
            };

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
                _artifacts,
                ct,
                requireRepositoryEvidence: true,
                requireRepositoryDiscovery: false,
                discoveryTools: null,
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
        sb.AppendLine("- Prompt 2 is the repository understanding step.");
        sb.AppendLine($"- Prompt 2 must review the full allowed repository context and produce Markdown that is saved as {RepositoryStateArtifactFileName}.");
        sb.AppendLine("- Prompt 3 is the terminal requirement analysis step and must use the repository understanding produced in Prompt 2.");
        sb.AppendLine("- Do not call get_workflow_input or save_workflow_output.");
        sb.AppendLine("- When using a tool, return exactly one JSON object with an 'action' property.");
        sb.AppendLine("- Allowed tool actions are find_available_files, get_next_file_batch, and get_file.");
        sb.AppendLine("- Always call find_available_files first. It returns the allowed full physical path list for this run.");
        sb.AppendLine("- In Prompt 2, repeatedly call get_next_file_batch until all repository context batches are reviewed.");
        sb.AppendLine("- Use get_file only with exact full physical paths from find_available_files when you need targeted follow-up evidence.");
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
        sb.AppendLine("Purpose: build the saved repository understanding for the current solution before requirement analysis.");
        sb.AppendLine();
        sb.AppendLine("This is a repository understanding step only.");
        sb.AppendLine("Do NOT analyze the requirement yet.");
        sb.AppendLine("Do NOT design or plan changes.");
        sb.AppendLine();
        sb.AppendLine("REPOSITORY CONTEXT RULES");
        sb.AppendLine("- The allowed repository context for this step is ALL `.md` files and ALL files under `src/`.");
        sb.AppendLine("- First call `find_available_files` to confirm the full allowed file set.");
        sb.AppendLine("- Then repeatedly call `get_next_file_batch` until the full allowed repository context has been reviewed.");
        sb.AppendLine("- Use `get_file` only for targeted follow-up reads when a specific file needs closer confirmation.");
        sb.AppendLine("- Your final response for this step must be Markdown only. The system will save it to disk as `repository-state.md` and pass it to Prompt 3.");
        sb.AppendLine();
        sb.AppendLine("Return Markdown using exactly this structure:");
        sb.AppendLine("# Repository State");
        sb.AppendLine("## Solution Overview");
        sb.AppendLine("## Business / Domain Summary");
        sb.AppendLine("## Solution Stack");
        sb.AppendLine("## Architecture / Structure");
        sb.AppendLine("## Implemented Functionalities");
        sb.AppendLine("## Important Rules / Constraints Identified");
        sb.AppendLine("## Relevant Files Reviewed");
        sb.AppendLine("## Known Gaps / Unclear Areas");
        sb.AppendLine();
        sb.AppendLine("The `Relevant Files Reviewed` section must contain short summaries of the files actually used to build this repository understanding.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildFinalAnalysisPrompt(SolutionAnalysisRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is Prompt 3 of 3 for analyze-request.");
        sb.AppendLine("Use the saved repository understanding from Prompt 2 as the baseline understanding of the current solution, then analyze the requirement and return the final analysis report in Markdown.");
        sb.AppendLine();
        sb.AppendLine("REQUIREMENT TO ANALYZE");
        sb.AppendLine($"- Title: {request.RequirementTitle}");
        sb.AppendLine("- Description:");
        sb.AppendLine(request.RequirementDescription);
        sb.AppendLine();
        sb.AppendLine("TOOL USAGE");
        sb.AppendLine("- Start from the requirement.");
        sb.AppendLine("- The repository-state Markdown produced in Prompt 2 is already available in your conversation context. Use it first.");
        sb.AppendLine("- Call `find_available_files` if you need to confirm or target specific follow-up evidence.");
        sb.AppendLine("- Use `get_file` only with an exact full physical path returned by `find_available_files`.");
        sb.AppendLine("- Use targeted follow-up reads only where the requirement analysis needs extra confirmation beyond the repository-state understanding.");
        sb.AppendLine("- After you finish reading evidence, end with the final analysis report as plain Markdown.");
        sb.AppendLine("- Do not call save_workflow_output. The final Markdown response is the workflow output for analysis.");
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
        var artifacts = new List<ArtifactRecord>
        {
            new("repository-state", "Repository State", RepositoryStateArtifactFileName)
        };

        if (producedArtifacts.Count > 0)
        {
            artifacts.AddRange(
                producedArtifacts
                    .Where(artifact => !string.IsNullOrWhiteSpace(artifact.Type) && !string.IsNullOrWhiteSpace(artifact.Name))
                    .Select(artifact => new ArtifactRecord(
                        artifact.Type.Trim(),
                        artifact.Name.Trim(),
                        "analysis-report.md")));
        }
        else
        {
            artifacts.Add(new ArtifactRecord("analysis-report", "Analysis Report", "analysis-report.md"));
        }

        return JsonSerializer.Serialize(
            artifacts
                .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            JsonOptions);
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
