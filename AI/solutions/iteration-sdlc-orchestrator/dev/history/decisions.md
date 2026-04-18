# Decisions

## Active Decisions

- Runtime SDLC execution is target-scoped. Requirements, backlog items, workflow runs, open questions, and decisions all attach to `SolutionTarget`.
- `Solution` remains the logical grouping and cockpit selector parent; `SolutionTarget` is the concrete repository/runtime target.
- Requirements are the canonical intake object. Backlog is downstream planning output.
- Workflow execution runs in the background through `IWorkflowExecutionQueue` and `WorkflowExecutionBackgroundService`, not inline in the API request path.
- Setup is now a real persisted workflow run with `WorkflowRun`, `AgentTaskRun`, saved artifacts, and bootstrap output.
- Workflow logs are append-only filesystem logs per run and are surfaced through the cockpit.
- Stable framework docs, stable target docs, and runtime workflow artifacts are separate categories and should not be treated as the same thing.
- The cockpit shell now keeps MudBlazor providers centralized in `App.razor` instead of duplicating providers across the shell/layout.

## Transitional Decisions In Effect

- Document browsing still uses solution-level endpoints and resolves the first target found for the solution. This is a temporary mismatch with the target-scoped runtime model.
- Setup/update still operate like one-target-per-solution flows. This is reinforced by the EF unique index on `SolutionTarget.SolutionId`.
- The current implementation workflow advances backlog/requirement status to awaiting validation even though the agent runtime is still read-only and does not modify repository files.
- Prompt assembly is intentionally layered: framework prompt plus output schema plus runtime context plus hardcoded orchestration rules in C#.
- Direct code and structure changes made outside the workflow pipeline are accepted reality and must be reflected in stable target docs manually.

## Superseded Decisions

- Synchronous long-running workflow execution in the HTTP request path has been superseded by background execution.
- Solution-only runtime scoping has been superseded by target-scoped runtime records.
- Backlog-first intake as the primary change model has been superseded by requirement-first intake.
