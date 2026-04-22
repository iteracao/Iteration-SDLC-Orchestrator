using Iteration.Orchestrator.Domain.Requirements;
using Iteration.Orchestrator.Domain.Workflows;

namespace Iteration.Orchestrator.Application.Workflows;

public static class WorkflowLifecycleCatalog
{
    public static bool IsRequirementWorkflow(string workflowCode)
        => GetWorkflowRequirementState(workflowCode) is not null;

    public static string? GetWorkflowRequirementState(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => RequirementLifecycleStatus.Analyze,
            "design-solution-change" => RequirementLifecycleStatus.Design,
            "plan-implementation" => RequirementLifecycleStatus.Plan,
            "implement-solution-change" => RequirementLifecycleStatus.Implement,
            "test-solution-change" => RequirementLifecycleStatus.Test,
            "review-implementation" => RequirementLifecycleStatus.Review,
            "deliver-solution-change" => RequirementLifecycleStatus.Deliver,
            "update-solution-history" => RequirementLifecycleStatus.Documentation,
            _ => null
        };

    public static string? GetRequiredRequirementStatus(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => RequirementLifecycleStatus.Pending,
            "design-solution-change" => RequirementLifecycleStatus.Design,
            "plan-implementation" => RequirementLifecycleStatus.Plan,
            "implement-solution-change" => RequirementLifecycleStatus.Implement,
            "test-solution-change" => RequirementLifecycleStatus.Test,
            "review-implementation" => RequirementLifecycleStatus.Review,
            "deliver-solution-change" => RequirementLifecycleStatus.Deliver,
            "update-solution-history" => RequirementLifecycleStatus.Documentation,
            _ => null
        };

    public static bool CanStartWorkflow(string workflowCode, string requirementStatus)
    {
        var normalized = RequirementLifecycleStatus.Normalize(requirementStatus);
        return workflowCode switch
        {
            "analyze-request" => normalized is RequirementLifecycleStatus.Pending or RequirementLifecycleStatus.Analyze,
            _ => string.Equals(normalized, GetRequiredRequirementStatus(workflowCode), StringComparison.OrdinalIgnoreCase)
        };
    }

    public static string? GetNextRequirementStatusAfterValidation(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => RequirementLifecycleStatus.Design,
            "design-solution-change" => RequirementLifecycleStatus.Plan,
            "plan-implementation" => RequirementLifecycleStatus.Implement,
            "implement-solution-change" => RequirementLifecycleStatus.AwaitingDecision,
            _ => null
        };

    public static bool IsBlockingStatus(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending
            or WorkflowRunStatus.Running
            or WorkflowRunStatus.Completed;

    public static bool CanValidate(WorkflowRunStatus status)
        => status == WorkflowRunStatus.Completed;

    public static bool CanCancel(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending
            or WorkflowRunStatus.Completed
            or WorkflowRunStatus.Error;

    public static bool IsActive(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Pending or WorkflowRunStatus.Running;

    public static bool IsRunning(WorkflowRunStatus status)
        => status == WorkflowRunStatus.Running;

    public static string BuildBlockingRunMessage(string workflowCode)
        => workflowCode switch
        {
            "analyze-request" => "Analysis already has a pending, running, or completed run awaiting a decision.",
            "design-solution-change" => "Design already has a pending, running, or completed run awaiting a decision.",
            "plan-implementation" => "Planning already has a pending, running, or completed run awaiting a decision.",
            "implement-solution-change" => "Implementation already has a pending, running, or completed run awaiting a decision for this backlog item.",
            _ => "A workflow run for this stage is already pending, running, or completed and awaiting a decision."
        };
}
