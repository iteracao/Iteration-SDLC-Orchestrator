# Known Gaps

## Current Gaps

- The pipeline is not fully implemented end to end. Only setup, analyze, design, plan, and implement have runtime handlers and executor support.
- Validation now exists as a generic workflow-run action, but it is not a first-class executor-backed workflow yet. Test/review/deliver/history stages still do not have their own runtime handlers.
- The current implementation workflow does not mutate repository files. `FileAwareAgentRunner` supports only bounded `read_file` requests, so implementation currently produces reports and state changes without applying code.
- Runtime state is target-scoped, but document browsing is still solution-based and resolves the first target for a solution.
- The EF model and update/setup flow still enforce one target per solution through a unique index on `SolutionTarget.SolutionId` and first-target update behavior.
- Requirement lifecycle is normalized now, but backward compatibility for older persisted requirement strings and workflow run values is handled in code rather than through a completed EF migration.
- `setup-solution` is now deterministic and transaction-safe, but it still requires an existing repository folder and a GitHub repository URL.
- Setup seeds only the baseline doc set and does not bootstrap docs from repository truth.
- Setup does not scaffold `design/latest-design.md` or `delivery/latest-plan.md`, even though later workflow handlers try to read them if present.
- Setup intentionally defers real repository understanding and stable documentation updates to the next workflow, `update-target-documentation`.
- Setup prepares artifacts before DB persistence and cleans them up on DB persistence failure, but successful repository/bootstrap filesystem changes are not automatically rolled back if the later DB phase fails.
- Setup returns `NextWorkflowCode = "update-target-documentation"` as a handoff signal, but that follow-up workflow is not implemented yet.
- Prompt construction is still partially hardcoded in the agent-host project. Framework prompts and schemas are not the whole prompt.
- Repository search/evidence gathering is intentionally simple and bounded; it is not a semantic code intelligence layer.
- Some recent architecture and UI changes were made directly in code outside the SDLC workflow loop, so workflow artifacts are not yet the full source of truth for the current state.
- Direct backlog creation API support still exists even though plan-generated backlog is the intended normal path.

## Confirmed Drift

- `AI/framework/workflows` describes test, review, deliver, and history-update stages that are not yet wired into `WorkflowRunExecutor`.
- The cockpit visually presents Test, Review, Deliver, and Documentation lanes even though only part of that pipeline is backed by runtime behavior.
- Stable target docs are meant to describe the current solution, but later workflow handlers also use a few "latest" docs as planning context, so there is still transitional overlap between documentation and workflow-summary roles.
- The cockpit poller is lighter and snapshot-based now, but it is still HTTP polling rather than push-based updates.

## Risks

- Advancing backlog items to awaiting validation without applying repository changes can create a false sense of progress.
- The solution-level document browser will become actively misleading once multiple targets per solution are introduced.
- Weak or placeholder target docs reduce grounding quality for every downstream workflow.
- Hardcoded prompt layers can drift away from the framework YAML/schema contract if they are not documented and maintained together.
