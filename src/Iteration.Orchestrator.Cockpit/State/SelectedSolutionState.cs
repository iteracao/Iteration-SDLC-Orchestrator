using Iteration.Orchestrator.Cockpit.Models;

namespace Iteration.Orchestrator.Cockpit.Services;

public sealed class SelectedSolutionState
{
    public SolutionSummary? Current { get; private set; }

    public event Action? Changed;

    public void SetCurrent(SolutionSummary? solution)
    {
        Current = solution;
        Changed?.Invoke();
    }
}
