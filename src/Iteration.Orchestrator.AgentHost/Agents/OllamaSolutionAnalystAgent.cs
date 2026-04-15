using System.Text.Json;
using Iteration.Orchestrator.Application.Abstractions;

namespace Iteration.Orchestrator.AgentHost.Agents;

public sealed class OllamaSolutionAnalystAgent : ISolutionAnalystAgent
{
    public Task<SolutionAnalysisResult> AnalyzeAsync(SolutionAnalysisRequest request, CancellationToken ct)
    {
        var result = new
        {
            summary = $"Analysis for backlog item '{request.BacklogTitle}'.",
            impactedAreas = new[]
            {
                new { area = "UI Shell", reason = "Backlog title indicates shell or UX drift.", confidence = "medium" },
                new { area = "Blazor Pages", reason = "Likely affected by user-facing slice behavior.", confidence = "medium" }
            },
            risks = new[]
            {
                "Actual impacted files may be broader than current search hits.",
                "UI drift may reflect backend contract drift as well."
            },
            assumptions = new[]
            {
                "Target solution follows the expected .NET web enterprise profile.",
                "Provided search hits are representative of the affected slice."
            },
            recommendedNextSteps = new[]
            {
                "Inspect slice-specific Razor pages and associated DTO/service endpoints.",
                "Confirm canonical shell behavior from solution overlay docs.",
                "Prepare an execution plan only after validating impacted contracts."
            }
        };

        var raw = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

        return Task.FromResult(new SolutionAnalysisResult(
            result.summary,
            [
                new ImpactedAreaResult("UI Shell", "Backlog title indicates shell or UX drift.", "medium"),
                new ImpactedAreaResult("Blazor Pages", "Likely affected by user-facing slice behavior.", "medium")
            ],
            result.risks,
            result.assumptions,
            result.recommendedNextSteps,
            raw));
    }
}
