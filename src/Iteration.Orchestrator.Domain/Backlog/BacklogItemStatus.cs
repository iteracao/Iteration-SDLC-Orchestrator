namespace Iteration.Orchestrator.Domain.Backlog;

public enum BacklogItemStatus
{
    Draft = 1,
    Ready = 2,
    InAnalysis = 3,
    AnalysisCompleted = 4,
    Failed = 5
}
