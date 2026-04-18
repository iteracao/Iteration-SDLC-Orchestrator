# Known Gaps

## Current Gaps

- The pipeline is not fully implemented end to end. Only setup, analyze, design, plan, and implement have runtime handlers and executor support.
- Validation is not a first-class workflow yet. The current code uses `AwaitingValidation` and `awaiting-implementation-validation` states plus a cockpit placeholder lane instead of a dedicated validation/test/review runtime slice.
- The current implementation workflow does not mutate repository files. `FileAwareAgentRunner` supports only bounded `read_file` requests, so implementation currently produces reports and state changes without applying code.
- Runtime state is target-scoped, but document browsing is still solution-based and resolves the first target for a solution.
- The EF model and update/setup flow still enforce one target per solution through a unique index on `SolutionTarget.SolutionId` and first-target update behavior.
- `setup-solution` workflow YAML says repository creation and optional remote URL are supported, but `SetupSolutionHandler` currently requires an existing repository folder and a GitHub URL before setup proceeds.
- Setup seeds only the baseline doc set and does not bootstrap docs from repository truth.
- Setup does not scaffold `design/latest-design.md` or `delivery/latest-plan.md`, even though later workflow handlers try to read them if present.
- Prompt construction is still partially hardcoded in the agent-host project. Framework prompts and schemas are not the whole prompt.
- Repository search/evidence gathering is intentionally simple and bounded; it is not a semantic code intelligence layer.
- Some recent architecture and UI changes were made directly in code outside the SDLC workflow loop, so workflow artifacts are not yet the full source of truth for the current state.
- Direct backlog creation API support still exists even though plan-generated backlog is the intended normal path.

## Confirmed Drift

- `AI/framework/workflows` describes test, review, deliver, and history-update stages that are not yet wired into `WorkflowRunExecutor`.
- The cockpit visually presents Test, Review, Deliver, and Documentation lanes even though only part of that pipeline is backed by runtime behavior.
- Stable target docs are meant to describe the current solution, but later workflow handlers also use a few "latest" docs as planning context, so there is still transitional overlap between documentation and workflow-summary roles.

## Risks

- Advancing backlog items to awaiting validation without applying repository changes can create a false sense of progress.
- The solution-level document browser will become actively misleading once multiple targets per solution are introduced.
- Weak or placeholder target docs reduce grounding quality for every downstream workflow.
- Hardcoded prompt layers can drift away from the framework YAML/schema contract if they are not documented and maintained together.
