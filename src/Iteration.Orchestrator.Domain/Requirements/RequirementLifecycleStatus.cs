namespace Iteration.Orchestrator.Domain.Requirements;

public static class RequirementLifecycleStatus
{
    public const string Pending = "Pending";
    public const string Analyze = "Analyze";
    public const string Design = "Design";
    public const string Plan = "Plan";
    public const string Implement = "Implement";
    public const string Test = "Test";
    public const string Review = "Review";
    public const string Deliver = "Deliver";
    public const string Documentation = "Documentation";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

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
            var current when current.Equals("under-analysis", StringComparison.OrdinalIgnoreCase) => Analyze,
            var current when current.Equals("analysis-failed", StringComparison.OrdinalIgnoreCase) => Analyze,
            var current when current.Equals("analyzed", StringComparison.OrdinalIgnoreCase) => Design,
            var current when current.Equals("analyze", StringComparison.OrdinalIgnoreCase) => Analyze,
            var current when current.Equals("under-design", StringComparison.OrdinalIgnoreCase) => Design,
            var current when current.Equals("design-failed", StringComparison.OrdinalIgnoreCase) => Design,
            var current when current.Equals("designed", StringComparison.OrdinalIgnoreCase) => Plan,
            var current when current.Equals("design", StringComparison.OrdinalIgnoreCase) => Design,
            var current when current.Equals("under-planning", StringComparison.OrdinalIgnoreCase) => Plan,
            var current when current.Equals("planning-failed", StringComparison.OrdinalIgnoreCase) => Plan,
            var current when current.Equals("planned", StringComparison.OrdinalIgnoreCase) => Implement,
            var current when current.Equals("plan", StringComparison.OrdinalIgnoreCase) => Plan,
            var current when current.Equals("under-implementation", StringComparison.OrdinalIgnoreCase) => Implement,
            var current when current.Equals("implementation-failed", StringComparison.OrdinalIgnoreCase) => Implement,
            var current when current.Equals("implemented", StringComparison.OrdinalIgnoreCase) => Test,
            var current when current.Equals("implement", StringComparison.OrdinalIgnoreCase) => Implement,
            var current when current.Equals("tested", StringComparison.OrdinalIgnoreCase) => Review,
            var current when current.Equals("test", StringComparison.OrdinalIgnoreCase) => Test,
            var current when current.Equals("reviewed", StringComparison.OrdinalIgnoreCase) => Deliver,
            var current when current.Equals("review", StringComparison.OrdinalIgnoreCase) => Review,
            var current when current.Equals("delivered", StringComparison.OrdinalIgnoreCase) => Documentation,
            var current when current.Equals("deliver", StringComparison.OrdinalIgnoreCase) => Deliver,
            var current when current.Equals("documented", StringComparison.OrdinalIgnoreCase) => Completed,
            var current when current.Equals("documentation", StringComparison.OrdinalIgnoreCase) => Documentation,
            var current when current.Equals("validatedcommitted", StringComparison.OrdinalIgnoreCase) => Completed,
            var current when current.Equals("pendingcommit", StringComparison.OrdinalIgnoreCase) => Documentation,
            var current when current.Equals("cancelledrolledback", StringComparison.OrdinalIgnoreCase) => Cancelled,
            var current when current.Equals("canceled", StringComparison.OrdinalIgnoreCase) => Cancelled,
            var current when current.Equals("cancelled", StringComparison.OrdinalIgnoreCase) => Cancelled,
            var current when current.Equals(Pending, StringComparison.OrdinalIgnoreCase) => Pending,
            var current when current.Equals(Analyze, StringComparison.OrdinalIgnoreCase) => Analyze,
            var current when current.Equals(Design, StringComparison.OrdinalIgnoreCase) => Design,
            var current when current.Equals(Plan, StringComparison.OrdinalIgnoreCase) => Plan,
            var current when current.Equals(Implement, StringComparison.OrdinalIgnoreCase) => Implement,
            var current when current.Equals(Test, StringComparison.OrdinalIgnoreCase) => Test,
            var current when current.Equals(Review, StringComparison.OrdinalIgnoreCase) => Review,
            var current when current.Equals(Deliver, StringComparison.OrdinalIgnoreCase) => Deliver,
            var current when current.Equals(Documentation, StringComparison.OrdinalIgnoreCase) => Documentation,
            var current when current.Equals(Completed, StringComparison.OrdinalIgnoreCase) => Completed,
            var current when current.Equals(Cancelled, StringComparison.OrdinalIgnoreCase) => Cancelled,
            _ => Pending
        };
    }
}
