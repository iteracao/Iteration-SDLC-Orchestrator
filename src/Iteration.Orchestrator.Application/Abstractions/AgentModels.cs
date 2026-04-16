namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record SolutionAnalysisRequest(
    Guid WorkflowRunId,
    Guid TargetSolutionId,
    string WorkflowCode,
    string WorkflowName,
    string WorkflowPurpose,
    string RequirementTitle,
    string RequirementDescription,
    string ProfileSummary,
    IReadOnlyList<TextDocumentInput> ProfileRules,
    IReadOnlyList<TextDocumentInput> SolutionKnowledgeDocuments,
    IReadOnlyList<WorkflowArtifactDefinition> ProducedArtifacts,
    IReadOnlyList<string> KnowledgeUpdates,
    IReadOnlyList<string> ExecutionRules,
    IReadOnlyList<string> NextWorkflowCodes,
    SolutionSnapshot Snapshot,
    IReadOnlyList<FileSearchHit> SearchHits,
    IReadOnlyDictionary<string, string> SampleFiles);

public sealed record SolutionAnalysisResult(
    string Summary,
    string Status,
    string ArtifactsJson,
    string GeneratedRequirementsJson,
    string GeneratedOpenQuestionsJson,
    string GeneratedDecisionsJson,
    string DocumentationUpdatesJson,
    string KnowledgeUpdatesJson,
    string RecommendedNextWorkflowCodesJson,
    string RawJson);
