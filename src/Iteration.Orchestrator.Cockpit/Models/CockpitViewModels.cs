namespace Iteration.Orchestrator.Cockpit.Models;

public sealed record RequirementStageCard(
    string LaneKey,
    string LaneTitle,
    string Title,
    string Description,
    string DisplayDate,
    string StatusLabel,
    VisualState State,
    RequirementRow Requirement,
    BacklogRow? BacklogItem,
    WorkflowRunRow? WorkflowRun);

public sealed record RequirementPipelineViewModel(
    RequirementRow Requirement,
    VisualState RequirementState,
    IReadOnlyList<RequirementPipelineStep> Steps);

public sealed record RequirementPipelineStep(
    string LaneKey,
    string LaneTitle,
    string Icon,
    RequirementStageCard Card,
    bool Disabled)
{
    public VisualState State => Card.State;
}

public sealed record VisualState(string Label, string Color, string CssClass);

public sealed class SetupSolutionModel
{
    public Guid? SolutionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MainSolutionFile { get; set; } = string.Empty;
    public string ProfileCode { get; set; } = string.Empty;
    public string TargetCode { get; set; } = string.Empty;
    public Guid? OverlayTargetId { get; set; }
    public string? RemoteRepositoryUrl { get; set; }
}

public sealed class CreateRequirementModel
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequirementType { get; set; } = "functional";
    public string Source { get; set; } = "user";
    public string Priority { get; set; } = "medium";
}

public sealed class OverlayOption(SolutionSummary solution, SolutionTargetSummary target)
{
    public SolutionSummary Solution { get; } = solution;
    public SolutionTargetSummary Target { get; } = target;
    public string Label => $"{Solution.Name} / {Target.DisplayName}";
}

public sealed class SolutionTargetOption
{
    public Guid TargetId { get; set; }
    public string Label { get; set; } = string.Empty;
}

public sealed class ApiErrorResponse
{
    public string Message { get; set; } = string.Empty;
}

public sealed class SolutionDocumentSummary
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public DateTime LastModifiedUtc { get; set; }
}

public sealed class SolutionDocumentContent
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
}

public sealed class WorkflowLogContent
{
    public Guid WorkflowRunId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> ArtifactFiles { get; set; } = [];
    public bool IsUnavailable { get; set; }
    public string? Message { get; set; }
}

public sealed class WorkflowArtifactContent
{
    public Guid WorkflowRunId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsUnavailable { get; set; }
    public string? Message { get; set; }
}

public sealed class WorkflowRunDetail
{
    public bool HasOutputPayload { get; set; }
    public List<string> ArtifactFiles { get; set; } = [];
    public WorkflowRunReport? Report { get; set; }
}

public sealed class WorkflowRunReport
{
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string DocumentationUpdatesJson { get; set; } = "[]";
    public string KnowledgeUpdatesJson { get; set; } = "[]";
}

public sealed class RequirementRow
{
    public Guid Id { get; set; }
    public Guid TargetSolutionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class BacklogRow
{
    public Guid Id { get; set; }
    public Guid? RequirementId { get; set; }
    public int PlanningOrder { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public sealed class WorkflowRunRow
{
    public Guid Id { get; set; }
    public Guid? RequirementId { get; set; }
    public Guid? BacklogItemId { get; set; }
    public string WorkflowCode { get; set; } = string.Empty;
    public int Status { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureReason { get; set; }
}
