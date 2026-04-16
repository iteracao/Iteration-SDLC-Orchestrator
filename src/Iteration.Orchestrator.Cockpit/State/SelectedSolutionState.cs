using Iteration.Orchestrator.Cockpit.Models;

namespace Iteration.Orchestrator.Cockpit.Services;

public sealed class SelectedSolutionState
{
    public SolutionSummary? Current { get; private set; }
    public SolutionTargetSummary? CurrentTarget { get; private set; }

    public event Action? Changed;

    public void SetCurrent(SolutionSummary? solution, Guid? targetId = null)
    {
        Current = solution;
        CurrentTarget = solution is null
            ? null
            : ResolveTarget(solution, targetId);

        Changed?.Invoke();
    }

    public void SetCurrentTarget(Guid? targetId)
    {
        CurrentTarget = Current is null ? null : ResolveTarget(Current, targetId);
        Changed?.Invoke();
    }

    private static SolutionTargetSummary? ResolveTarget(SolutionSummary solution, Guid? targetId)
        => targetId.HasValue
            ? solution.Targets.FirstOrDefault(x => x.Id == targetId.Value)
                ?? solution.Targets.FirstOrDefault()
            : solution.Targets.Count == 1
                ? solution.Targets[0]
                : solution.Targets.FirstOrDefault();
}
