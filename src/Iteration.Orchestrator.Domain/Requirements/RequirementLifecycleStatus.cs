namespace Iteration.Orchestrator.Domain.Requirements;

public static class RequirementLifecycleStatus
{
    public const string Pending = "Pending";
    public const string Analyzed = "Analyzed";
    public const string Designed = "Designed";
    public const string Planned = "Planned";
    public const string Implemented = "Implemented";
    public const string Tested = "Tested";
    public const string Reviewed = "Reviewed";
    public const string Delivered = "Delivered";
    public const string Documented = "Documented";
    public const string ValidatedCommitted = "ValidatedCommitted";
    public const string CancelledRolledBack = "CancelledRolledBack";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Pending;
        }

        return normalized switch
        {
            var current when current.Equals("submitted", StringComparison.OrdinalIgnoreCase) => Pending,
            var current when current.Equals("under-analysis", StringComparison.OrdinalIgnoreCase) => Pending,
            var current when current.Equals("analysis-failed", StringComparison.OrdinalIgnoreCase) => Pending,
            var current when current.Equals("analyzed", StringComparison.OrdinalIgnoreCase) => Analyzed,
            var current when current.Equals("under-design", StringComparison.OrdinalIgnoreCase) => Analyzed,
            var current when current.Equals("design-failed", StringComparison.OrdinalIgnoreCase) => Analyzed,
            var current when current.Equals("designed", StringComparison.OrdinalIgnoreCase) => Designed,
            var current when current.Equals("under-planning", StringComparison.OrdinalIgnoreCase) => Designed,
            var current when current.Equals("planning-failed", StringComparison.OrdinalIgnoreCase) => Designed,
            var current when current.Equals("planned", StringComparison.OrdinalIgnoreCase) => Planned,
            var current when current.Equals("under-implementation", StringComparison.OrdinalIgnoreCase) => Planned,
            var current when current.Equals("implementation-failed", StringComparison.OrdinalIgnoreCase) => Planned,
            var current when current.Equals("awaiting-implementation-validation", StringComparison.OrdinalIgnoreCase) => Implemented,
            var current when current.Equals("canceled", StringComparison.OrdinalIgnoreCase) => CancelledRolledBack,
            var current when current.Equals("cancelled", StringComparison.OrdinalIgnoreCase) => CancelledRolledBack,
            var current when current.Equals(Pending, StringComparison.OrdinalIgnoreCase) => Pending,
            var current when current.Equals(Analyzed, StringComparison.OrdinalIgnoreCase) => Analyzed,
            var current when current.Equals(Designed, StringComparison.OrdinalIgnoreCase) => Designed,
            var current when current.Equals(Planned, StringComparison.OrdinalIgnoreCase) => Planned,
            var current when current.Equals(Implemented, StringComparison.OrdinalIgnoreCase) => Implemented,
            var current when current.Equals(Tested, StringComparison.OrdinalIgnoreCase) => Tested,
            var current when current.Equals(Reviewed, StringComparison.OrdinalIgnoreCase) => Reviewed,
            var current when current.Equals(Delivered, StringComparison.OrdinalIgnoreCase) => Delivered,
            var current when current.Equals(Documented, StringComparison.OrdinalIgnoreCase) => Documented,
            var current when current.Equals(ValidatedCommitted, StringComparison.OrdinalIgnoreCase) => ValidatedCommitted,
            var current when current.Equals(CancelledRolledBack, StringComparison.OrdinalIgnoreCase) => CancelledRolledBack,
            _ => Pending
        };
    }
}
