# Solution Overview

## Purpose

Iteration SDLC Orchestrator is a modular monolith that manages SDLC work around a target repository. It persists solutions, targets, requirements, backlog items, workflow runs, reports, questions, decisions, artifacts, and workflow logs, then exposes them through an API and a Blazor cockpit.

## Scope

Current code and files confirm these active capabilities:

- implemented: setup persists `Solution`, `SolutionTarget`, `WorkflowRun`, `AgentTaskRun`, and setup artifacts/logs
- implemented: `setup-documentation` can bootstrap or refresh the stable target docs under `AI/solutions/<target-storage-code>/`
- implemented: requirements, backlog, open questions, decisions, and workflow runs are loaded and filtered by `targetSolutionId`
- implemented: requirement-driven workflows exist for `analyze-request`, `design-solution-change`, `plan-implementation`, and `implement-solution-change`
- implemented: workflow execution is queued and completed in a background hosted service
- implemented: workflow reports are persisted as `AnalysisReport`, `DesignReport`, `PlanReport`, and `ImplementationReport`
- implemented: cockpit shows requirement cards, stage cards, run status, failure state, and per-run log inspection
- partially implemented: setup creates the managed knowledge workspace under `AI/solutions/<target-storage-code>/`
- partially implemented: solution knowledge is read by later workflows, but setup only seeds baseline files and does not bootstrap them from repository truth
- planned: test, review, deliver, and solution-history update workflows exist in `AI/framework` but do not have application/runtime handlers yet

## Current State

The repository has moved from solution-only runtime scoping to target-scoped runtime scoping.

- `Solution` is the logical record: name, description, profile
- `SolutionTarget` is the concrete runtime/repository record: target storage code, repository path, main solution file, overlay metadata
- requirements, backlog items, open questions, decisions, and workflow runs now belong to a target via `TargetSolutionId`
- cockpit selection state now tracks both the selected solution and the selected target
- cockpit runtime loading uses target-scoped endpoints such as `api/solution-targets/{targetSolutionId}/requirements` and `api/workflow-runs?targetSolutionId=...`

Current behavior is still transitional in one important place: document browsing remains solution-based. The cockpit calls `api/solutions/{solutionId}/documents`, and the controller resolves the first target found for that solution. That means runtime state is target-scoped, but document browsing is still effectively solution-scoped / first-target-resolved.

## Notes

- Stable framework guidance lives under `AI/framework`. That is where workflow definitions, agent prompts, output schemas, and profile rules are loaded from at runtime.
- Stable target-solution knowledge lives under `AI/solutions/<target-storage-code>/` in the repository being orchestrated. For this repository, that workspace is `AI/solutions/iteration-sdlc-orchestrator/dev/`.
- Workflow artifacts under `data/runs/<workflowRunId>/` and workflow logs under `data/workflow-logs/` are runtime evidence, not the stable target-solution documentation set.
- Recent architectural changes were made directly in code and structure, not exclusively through workflow-generated documentation. The target docs therefore need to be maintained as a manual reality check.
- The current implementation workflow produces persisted implementation reports, but it still does not mutate repository files yet. The runner now supports bounded writes for `setup-documentation`, not for implementation code changes. This remains the most important implementation/runtime gap to keep in mind when reading the rest of the docs.
