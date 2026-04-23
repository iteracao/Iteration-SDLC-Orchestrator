namespace Iteration.Orchestrator.Cockpit;

internal static class CockpitLifecycleRules
{
    public const string Pending = "Pending";
    public const string Analyze = "Analyze";
    public const string Design = "Design";
    public const string Plan = "Plan";
    public const string Implement = "Implement";
    public const string AwaitingDecision = "AwaitingDecision";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static string NormalizeRequirementStatus(string? status)
    {
        var normalized = status?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Pending;
        }

        return normalized switch
        {
            var value when value.Equals("canceled", StringComparison.OrdinalIgnoreCase) => Cancelled,
            var value when value.Equals("readytocommit", StringComparison.OrdinalIgnoreCase) => AwaitingDecision,
            var value when value.Equals(AwaitingDecision, StringComparison.OrdinalIgnoreCase) => AwaitingDecision,
            var value when value.Equals(Completed, StringComparison.OrdinalIgnoreCase) => Completed,
            var value when value.Equals(Cancelled, StringComparison.OrdinalIgnoreCase) => Cancelled,
            var value when value.Equals(Pending, StringComparison.OrdinalIgnoreCase) => Pending,
            var value when value.Equals(Analyze, StringComparison.OrdinalIgnoreCase) => Analyze,
            var value when value.Equals(Design, StringComparison.OrdinalIgnoreCase) => Design,
            var value when value.Equals(Plan, StringComparison.OrdinalIgnoreCase) => Plan,
            var value when value.Equals(Implement, StringComparison.OrdinalIgnoreCase) => Implement,
            _ => normalized
        };
    }

    public static bool IsAwaitingDecision(string? requirementStatus)
        => string.Equals(NormalizeRequirementStatus(requirementStatus), AwaitingDecision, StringComparison.OrdinalIgnoreCase);

    public static bool CanCommitRequirement(string? requirementStatus)
        => IsAwaitingDecision(requirementStatus);

    public static bool CanCancelRequirement(string? requirementStatus, bool hasRunningRun)
    {
        var normalizedStatus = NormalizeRequirementStatus(requirementStatus);
        return !hasRunningRun
            && !string.Equals(normalizedStatus, Pending, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedStatus, Completed, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedStatus, Cancelled, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanDeleteRequirement(string? requirementStatus, bool hasWorkflowRuns, bool hasBacklogItems)
        => string.Equals(NormalizeRequirementStatus(requirementStatus), Pending, StringComparison.OrdinalIgnoreCase)
            && !hasWorkflowRuns
            && !hasBacklogItems;

    public static bool CanValidateWorkflowRun(int status)
        => status == 6;

    public static bool CanCancelWorkflowRun(int status)
        => status is 1 or 4 or 6;

    public static bool IsBlockingWorkflowStatus(int status)
        => status is 1 or 2 or 6;

    public static bool IsRunningWorkflowStatus(int status)
        => status == 2;

    public static bool CanViewWorkflowArtifacts(int status, bool isFinalDecisionRun)
        => status is 2 or 3 or 4 or 6 || isFinalDecisionRun;

    public static string GetRequirementBadgeLabel(string? requirementStatus)
    {
        var normalizedStatus = NormalizeRequirementStatus(requirementStatus);
        return normalizedStatus switch
        {
            AwaitingDecision => "Awaiting Decision",
            _ => HumanizeRequirementStatus(normalizedStatus)
        };
    }

    public static string HumanizeRequirementStatus(string? requirementStatus)
    {
        var normalizedStatus = NormalizeRequirementStatus(requirementStatus);
        return normalizedStatus switch
        {
            AwaitingDecision => "Awaiting Decision",
            _ => string.Concat(normalizedStatus.Select((ch, index) => index > 0 && char.IsUpper(ch) ? $" {ch}" : ch.ToString())).Trim()
        };
    }
}
