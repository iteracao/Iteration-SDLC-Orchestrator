namespace Iteration.Orchestrator.Domain.Reports;

public sealed class DesignReport
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; private set; }
    public Guid RequirementId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = "completed";
    public string ArtifactsJson { get; private set; } = "[]";
    public string GeneratedOpenQuestionsJson { get; private set; } = "[]";
    public string GeneratedDecisionsJson { get; private set; } = "[]";
    public string DocumentationUpdatesJson { get; private set; } = "[]";
    public string KnowledgeUpdatesJson { get; private set; } = "[]";
    public string RecommendedNextWorkflowCodesJson { get; private set; } = "[]";
    public string RawOutputJson { get; private set; } = "{}";

    private DesignReport() { }

    public DesignReport(
        Guid workflowRunId,
        Guid requirementId,
        string summary,
        string status,
        string artifactsJson,
        string generatedOpenQuestionsJson,
        string generatedDecisionsJson,
        string documentationUpdatesJson,
        string knowledgeUpdatesJson,
        string recommendedNextWorkflowCodesJson,
        string rawOutputJson)
    {
        WorkflowRunId = workflowRunId;
        RequirementId = requirementId;
        Summary = summary;
        Status = status;
        ArtifactsJson = artifactsJson;
        GeneratedOpenQuestionsJson = generatedOpenQuestionsJson;
        GeneratedDecisionsJson = generatedDecisionsJson;
        DocumentationUpdatesJson = documentationUpdatesJson;
        KnowledgeUpdatesJson = knowledgeUpdatesJson;
        RecommendedNextWorkflowCodesJson = recommendedNextWorkflowCodesJson;
        RawOutputJson = rawOutputJson;
    }
}
