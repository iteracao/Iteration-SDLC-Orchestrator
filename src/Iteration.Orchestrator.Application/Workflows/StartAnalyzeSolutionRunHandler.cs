using System.Text;
using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Domain.Solutions;
using Iteration.Orchestrator.Domain.Workflows;

namespace Iteration.Orchestrator.Application.Workflows;

public sealed class StartAnalyzeSolutionRunHandler
{
    private readonly IAppDbContext _db;
    private readonly IConfigCatalog _config;
    private readonly ISolutionBridge _bridge;
    private readonly ISolutionAnalystAgent _agent;
    private readonly IArtifactStore _artifacts;

    public StartAnalyzeSolutionRunHandler(
        IAppDbContext db,
        IConfigCatalog config,
        ISolutionBridge bridge,
        ISolutionAnalystAgent agent,
        IArtifactStore artifacts)
    {
        _db = db;
        _config = config;
        _bridge = bridge;
        _agent = agent;
        _artifacts = artifacts;
    }

    public async Task<Guid> HandleAsync(StartAnalyzeSolutionRunCommand command, CancellationToken ct)
    {
        var backlog = await _db.BacklogItems.FindAsync([command.BacklogItemId], ct)
            ?? throw new InvalidOperationException("Backlog item not found.");

        var solution = await _db.SolutionTargets.FindAsync([backlog.TargetSolutionId], ct)
            ?? throw new InvalidOperationException("Target solution not found.");

        var workflow = await _config.GetWorkflowAsync(backlog.WorkflowCode, ct);
        var profile = await _config.GetProfileAsync(solution.ProfileCode, ct);
        var agentDef = await _config.GetAgentAsync(workflow.PrimaryAgent, ct);

        var run = new WorkflowRun(backlog.Id, solution.Id, backlog.WorkflowCode, command.RequestedBy);
        run.Start("request-analysis");
        backlog.MarkInAnalysis();

        _db.WorkflowRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var snapshot = await _bridge.GetSolutionSnapshotAsync(solution, ct);
        var hits = await _bridge.SearchFilesAsync(solution, backlog.Title, ct);

        var sampleFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits.Take(5))
        {
            sampleFiles[hit.RelativePath] = await _bridge.ReadFileAsync(solution, hit.RelativePath, ct);
        }

        var solutionKnowledgeDocuments = await LoadSolutionKnowledgeDocumentsAsync(solution, ct);

        var request = new SolutionAnalysisRequest(
            run.Id,
            workflow.Code,
            workflow.Name,
            workflow.Purpose,
            backlog.Title,
            backlog.Description,
            BuildProfileSummary(profile),
            profile.Rules,
            solutionKnowledgeDocuments,
            workflow.ExecutionRules,
            snapshot,
            hits,
            sampleFiles);

        var inputJson = JsonSerializer.Serialize(request);
        var taskRun = new AgentTaskRun(run.Id, agentDef.Code, inputJson);
        taskRun.Start();
        _db.AgentTaskRuns.Add(taskRun);
        await _db.SaveChangesAsync(ct);

        try
        {
            var result = await _agent.AnalyzeAsync(request, ct);

            taskRun.Succeed(result.RawJson);

            var report = new Domain.Reports.AnalysisReport(
                run.Id,
                result.Summary,
                JsonSerializer.Serialize(result.ImpactedAreas),
                JsonSerializer.Serialize(result.Risks),
                JsonSerializer.Serialize(result.Assumptions),
                JsonSerializer.Serialize(result.RecommendedNextSteps),
                result.RawJson);

            _db.AnalysisReports.Add(report);

            run.Succeed("analysis-completed");
            backlog.MarkAnalysisCompleted();

            await _db.SaveChangesAsync(ct);

            await _artifacts.SaveTextAsync(run.Id, "analysis-request.input.json", inputJson, ct);
            await _artifacts.SaveTextAsync(run.Id, "analysis-report.json", result.RawJson, ct);
            return run.Id;
        }
        catch (Exception ex)
        {
            taskRun.Fail(ex.Message);
            run.Fail("request-analysis", ex.Message);
            backlog.MarkFailed();
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<IReadOnlyList<TextDocumentInput>> LoadSolutionKnowledgeDocumentsAsync(
        SolutionTarget solution,
        CancellationToken ct)
    {
        var relativePaths = new[]
        {
            $"ai/solutions/{solution.Code}/context/overview.md",
            $"ai/solutions/{solution.Code}/business/business-rules.md",
            $"ai/solutions/{solution.Code}/business/workflows.md",
            $"ai/solutions/{solution.Code}/architecture/architecture-overview.md",
            $"ai/solutions/{solution.Code}/architecture/module-map.md",
            $"ai/solutions/{solution.Code}/history/decisions.md",
            $"ai/solutions/{solution.Code}/history/open-questions.md",
            $"ai/solutions/{solution.Code}/history/known-gaps.md",
            $"ai/solutions/{solution.Code}/analysis/latest-analysis.md"
        };

        var docs = new List<TextDocumentInput>();

        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(
                solution.RepositoryPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                var content = await _bridge.ReadFileAsync(solution, relativePath, ct);
                docs.Add(new TextDocumentInput(relativePath, content));
            }
            catch
            {
                // ignore individual file failures so analysis can continue
            }
        }

        return docs;
    }

    private static string BuildProfileSummary(ProfileDefinition profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine(profile.Name);
        sb.AppendLine(profile.Description);

        if (profile.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Rules included:");
            foreach (var rule in profile.Rules)
            {
                sb.AppendLine($"- {rule.Path}");
            }
        }

        return sb.ToString().Trim();
    }
}