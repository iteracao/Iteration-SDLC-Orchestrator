namespace Iteration.Orchestrator.Domain.Reports;

public sealed class ImplementationReport
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; private set; }
    public Guid RequirementId { get; private set; }
    public Guid BacklogItemId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = "completed";
    public string ImplementedChangesJson { get; private set; } = "[]";
    public string FilesTouchedJson { get; private set; } = "[]";
    public string TestsExecutedJson { get; private set; } = "[]";
    public string GeneratedRequirementsJson { get; private set; } = "[]";
    public string GeneratedOpenQuestionsJson { get; private set; } = "[]";
    public string GeneratedDecisionsJson { get; private set; } = "[]";
    public string DocumentationUpdatesJson { get; private set; } = "[]";
    public string KnowledgeUpdatesJson { get; private set; } = "[]";
    public string RecommendedNextWorkflowCodesJson { get; private set; } = "[]";
    public string RawOutputJson { get; private set; } = "{}";

    private ImplementationReport() { }

    public ImplementationReport(
        Guid workflowRunId,
        Guid requirementId,
        Guid backlogItemId,
        string summary,
        string status,
        string implementedChangesJson,
        string filesTouchedJson,
        string testsExecutedJson,
        string generatedRequirementsJson,
        string generatedOpenQuestionsJson,
        string generatedDecisionsJson,
        string documentationUpdatesJson,
        string knowledgeUpdatesJson,
        string recommendedNextWorkflowCodesJson,
        string rawOutputJson)
    {
        WorkflowRunId = workflowRunId;
        RequirementId = requirementId;
        BacklogItemId = backlogItemId;
        Summary = summary;
        Status = status;
        ImplementedChangesJson = implementedChangesJson;
        FilesTouchedJson = filesTouchedJson;
        TestsExecutedJson = testsExecutedJson;
        GeneratedRequirementsJson = generatedRequirementsJson;
        GeneratedOpenQuestionsJson = generatedOpenQuestionsJson;
        GeneratedDecisionsJson = generatedDecisionsJson;
        DocumentationUpdatesJson = documentationUpdatesJson;
        KnowledgeUpdatesJson = knowledgeUpdatesJson;
        RecommendedNextWorkflowCodesJson = recommendedNextWorkflowCodesJson;
        RawOutputJson = rawOutputJson;
    }
}
