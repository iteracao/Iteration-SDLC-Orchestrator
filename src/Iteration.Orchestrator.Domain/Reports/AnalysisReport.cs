namespace Iteration.Orchestrator.Domain.Reports;

public sealed class AnalysisReport
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string ImpactedAreasJson { get; private set; } = "[]";
    public string RisksJson { get; private set; } = "[]";
    public string AssumptionsJson { get; private set; } = "[]";
    public string RecommendedNextStepsJson { get; private set; } = "[]";
    public string RawOutputJson { get; private set; } = "{}";

    private AnalysisReport() { }

    public AnalysisReport(
        Guid workflowRunId,
        string summary,
        string impactedAreasJson,
        string risksJson,
        string assumptionsJson,
        string recommendedNextStepsJson,
        string rawOutputJson)
    {
        WorkflowRunId = workflowRunId;
        Summary = summary;
        ImpactedAreasJson = impactedAreasJson;
        RisksJson = risksJson;
        AssumptionsJson = assumptionsJson;
        RecommendedNextStepsJson = recommendedNextStepsJson;
        RawOutputJson = rawOutputJson;
    }
}
