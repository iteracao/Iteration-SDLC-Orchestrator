# Module Map

## Purpose

This map ties each project to the behavior it owns today.

## Current Modules

### `Iteration.Orchestrator.Api`

Responsibilities:

- hosts controllers for setup, workflow starts, solutions, requirements, backlog, questions, decisions, and workflow log/report lookup
- registers EF Core, config loading, artifact/log stores, solution bridge, agents, queue, and hosted background execution
- exposes target-scoped runtime endpoints for requirements, questions, decisions, backlog, and workflow runs
- still exposes solution-level document browsing endpoints that internally resolve the first target for a solution

### `Iteration.Orchestrator.Cockpit`

Responsibilities:

- Blazor Server operator UI using MudBlazor
- solution/target selection through `SelectedSolutionState`
- pipeline view that groups requirement state, workflow runs, active backlog slice, and documentation availability
- workflow log inspection from `api/workflow-runs/{id}/log`
- solution setup panel and solution-management page

Current limitation:

- runtime data uses the selected target, but document loading still uses the selected solution id

### `Iteration.Orchestrator.Application`

Responsibilities:

- setup command/handler and request validation/normalization
- workflow start handlers and background execution handlers
- persistence of workflow-linked reports, generated requirements, questions, decisions, and backlog slices
- prompt-input preparation through repository file discovery and solution-knowledge loading

Current limitation:

- there are handlers only for setup, analyze, design, plan, and implement

### `Iteration.Orchestrator.Domain`

Responsibilities:

- domain records for `Solution`, `SolutionTarget`, `Requirement`, `BacklogItem`, `WorkflowRun`, `AgentTaskRun`, reports, questions, and decisions
- explicit requirement lifecycle methods for analysis/design/planning/implementation transitions
- backlog lifecycle methods for implementation error, awaiting validation, validated, and canceled

Current limitation:

- the model contains validation-oriented states, but there is no first-class validation workflow handler updating them yet

### `Iteration.Orchestrator.Infrastructure`

Responsibilities:

- SQLite persistence and EF mappings
- filesystem artifact and workflow-log storage
- framework config catalog backed by `AI/framework`
- GitHub metadata lookup for setup validation
- filesystem setup/bootstrap service for target repositories

Current limitation:

- setup service supports repo creation and optional remote config in principle, but the current handler requires an existing repository folder and a GitHub URL before the service is called

### `Iteration.Orchestrator.AgentHost`

Responsibilities:

- Microsoft Agent Framework wrappers for analyst, designer, planner, and implementation workflows
- prompt assembly from framework prompt/schema plus hardcoded orchestration rules
- iterative file-aware execution with bounded `read_file` access
- normalization of model output into persisted JSON payloads

Current limitation:

- agent execution is read-only; there is no write/apply tool and no repository mutation path

### `Iteration.Orchestrator.SolutionBridge`

Responsibilities:

- safe file reads rooted at the target repository path
- basic repository-tree listing and text search
- lightweight solution snapshot generation

Current limitation:

- search is substring-based across a limited set of file types and capped result sets; it is not a semantic or compiler-aware code intelligence layer

## Planned Modules

The current code direction still points toward these next runtime slices:

- first-class validation/testing workflow execution
- first-class review workflow execution
- first-class delivery/history-update execution
- richer documentation/bootstrap maintenance instead of placeholder seeding
- real repository mutation support for implementation workflows

## Known Drift

- `AI/framework/workflows` defines more pipeline stages than the runtime currently executes
- setup scaffolds only the baseline docs and does not scaffold the `design/latest-design.md` and `delivery/latest-plan.md` files that later handlers know how to read
- document browsing is not yet aligned with the target-scoped runtime model
- one target per solution is still enforced by both the EF model and the update flow
