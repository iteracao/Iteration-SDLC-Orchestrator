namespace Iteration.Orchestrator.Domain.Reports;

public sealed class AnalysisReport
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = "completed";
    public string ArtifactsJson { get; private set; } = "[]";
    public string GeneratedRequirementsJson { get; private set; } = "[]";
    public string GeneratedOpenQuestionsJson { get; private set; } = "[]";
    public string GeneratedDecisionsJson { get; private set; } = "[]";
    public string DocumentationUpdatesJson { get; private set; } = "[]";
    public string KnowledgeUpdatesJson { get; private set; } = "[]";
    public string RecommendedNextWorkflowCodesJson { get; private set; } = "[]";
    public string RawOutputJson { get; private set; } = "{}";

    private AnalysisReport() { }

    public AnalysisReport(
        Guid workflowRunId,
        string summary,
        string status,
        string artifactsJson,
        string generatedRequirementsJson,
        string generatedOpenQuestionsJson,
        string generatedDecisionsJson,
        string documentationUpdatesJson,
        string knowledgeUpdatesJson,
        string recommendedNextWorkflowCodesJson,
        string rawOutputJson)
    {
        WorkflowRunId = workflowRunId;
        Summary = summary;
        Status = status;
        ArtifactsJson = artifactsJson;
        GeneratedRequirementsJson = generatedRequirementsJson;
        GeneratedOpenQuestionsJson = generatedOpenQuestionsJson;
        GeneratedDecisionsJson = generatedDecisionsJson;
        DocumentationUpdatesJson = documentationUpdatesJson;
        KnowledgeUpdatesJson = knowledgeUpdatesJson;
        RecommendedNextWorkflowCodesJson = recommendedNextWorkflowCodesJson;
        RawOutputJson = rawOutputJson;
    }
}
