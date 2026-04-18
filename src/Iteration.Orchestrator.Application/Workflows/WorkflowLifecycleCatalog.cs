using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;

namespace Iteration.Orchestrator.Application.Workflows;

public static class WorkflowLifecycleCatalog
{
    public static bool IsRequirementWorkflow(string workflowCode)
        => GetValidatedRequirementStatus(workflowCode) is not null;

    public static string? GetRequiredRequirementStatus(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => RequirementLifecycleStatus.Pending,
            "design-solution-change" => RequirementLifecycleStatus.Analyzed,
            "plan-implementation" => RequirementLifecycleStatus.Designed,
            "implement-solution-change" => RequirementLifecycleStatus.Planned,
            "test-solution-change" => RequirementLifecycleStatus.Implemented,
            "review-implementation" => RequirementLifecycleStatus.Tested,
            "deliver-solution-change" => RequirementLifecycleStatus.Reviewed,
            "update-solution-history" => RequirementLifecycleStatus.Delivered,
            _ => null
        };

    public static string? GetValidatedRequirementStatus(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => RequirementLifecycleStatus.Analyzed,
            "design-solution-change" => RequirementLifecycleStatus.Designed,
            "plan-implementation" => RequirementLifecycleStatus.Planned,
            "implement-solution-change" => RequirementLifecycleStatus.Implemented,
            "test-solution-change" => RequirementLifecycleStatus.Tested,
            "review-implementation" => RequirementLifecycleStatus.Reviewed,
            "deliver-solution-change" => RequirementLifecycleStatus.Delivered,
            "update-solution-history" => RequirementLifecycleStatus.PendingCommit,
            _ => null
        };

    public static bool IsBlockingStatus(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending
            or WorkflowRunStatus.Running
            or WorkflowRunStatus.CompletedAwaitingValidation;

    public static bool CanValidate(WorkflowRunStatus status)
        => status == WorkflowRunStatus.CompletedAwaitingValidation;

    public static bool CanCancel(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending
            or WorkflowRunStatus.CompletedAwaitingValidation
            or WorkflowRunStatus.Error;

    public static bool IsActive(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending or WorkflowRunStatus.Running;

    public static string BuildBlockingRunMessage(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => "Analysis already has a pending, running, or awaiting-validation run.",
            "design-solution-change" => "Design already has a pending, running, or awaiting-validation run.",
            "plan-implementation" => "Planning already has a pending, running, or awaiting-validation run.",
            "implement-solution-change" => "Implementation already has a pending, running, or awaiting-validation run for this backlog item.",
            _ => "A workflow run for this stage is already pending, running, or awaiting validation."
        };
}
