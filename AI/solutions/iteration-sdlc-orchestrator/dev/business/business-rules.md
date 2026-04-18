# Business Rules

## Purpose

This document captures the business and modeling rules that are actually enforced, or clearly implied, by the current codebase.

## Canonical Rules

### Solution and target are separate concepts

- `Solution` is the logical definition and selector grouping
- `SolutionTarget` is the concrete runtime/repository target
- runtime work is attached to the target, not directly to the solution

### Runtime scope is target-scoped

The current model and cockpit treat these records as target-scoped:

- requirements
- backlog items
- workflow runs
- open questions
- decisions

### Requirement is the primary intake object

Requirements are still the canonical intake for change work. The main workflow pipeline starts from a requirement and moves it through analysis, design, planning, and implementation states.

### Backlog is downstream planning output

Backlog items are intended to be generated from planning and then implemented in order. The implementation handler explicitly blocks later slices until earlier slices are validated or canceled.

### Workflow run is the execution record

Every workflow attempt creates a `WorkflowRun` with:

- target association
- optional requirement link
- optional backlog-item link
- stage, status, timestamps, requested-by, and failure reason

### Agent task run is the agent call record

Each workflow execution currently creates one `AgentTaskRun` that stores:

- agent code
- input payload JSON
- output payload JSON on success
- failure reason on failure

### Reports are persisted workflow outputs

The current pipeline persists structured reports:

- `AnalysisReport`
- `DesignReport`
- `PlanReport`
- `ImplementationReport`

These reports are distinct from stable target-solution docs. They are runtime outputs tied to a workflow run.

### Stable docs, framework docs, and runtime artifacts are different things

- framework docs in `AI/framework` define generic workflows, prompts, schemas, and profile rules
- target docs in `AI/solutions/<target-storage-code>/` describe the current solution and its local decisions
- runtime artifacts in `data/runs/...` and `data/workflow-logs/...` capture execution evidence for a specific run

The current code already moves in this direction, but there is still overlap because workflow handlers read stable target docs such as `analysis/latest-analysis.md` as part of later phases.

### Managed target docs live inside the target repository

Setup creates the documentation workspace under `AI/solutions/<target-storage-code>/`, not under a solution-only path.

## Exceptions

- the cockpit document browser is still called with a solution id and resolves the first target for that solution
- the current EF model and update flow still allow only one target per solution
- the API still allows direct backlog creation even though the architectural direction is plan-generated backlog
- the implementation workflow records implementation output and advances statuses, but the current agent runtime does not write repository files

## Open Clarifications

- when validation becomes first-class, should it be a single workflow or split across test/review phases
- how should stable target docs be updated from report artifacts after each workflow
- should direct backlog creation remain as an operator escape hatch once planning is the normal path everywhere
