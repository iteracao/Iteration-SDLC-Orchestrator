namespace Iteration.Orchestrator.Application.Abstractions;

public sealed record SolutionAnalysisRequest(
    Guid WorkflowRunId,
    string WorkflowCode,
    string WorkflowName,
    string WorkflowPurpose,
    string BacklogTitle,
    string BacklogDescription,
    string ProfileSummary,
    IReadOnlyList<TextDocumentInput> ProfileRules,
    IReadOnlyList<TextDocumentInput> SolutionKnowledgeDocuments,
    IReadOnlyList<string> ExecutionRules,
    SolutionSnapshot Snapshot,
    IReadOnlyList<FileSearchHit> SearchHits,
    IReadOnlyDictionary<string, string> SampleFiles);

public sealed record SolutionAnalysisResult(
    string Summary,
    IReadOnlyList<ImpactedAreaResult> ImpactedAreas,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> RecommendedNextSteps,
    string RawJson);

public sealed record ImpactedAreaResult(
    string Area,
    string Reason,
    string Confidence);
