# Workflow: Setup Documentation

## Purpose
- Bootstrap or refresh the canonical stable solution documentation set for a specific repository target.
- Compare stable documentation with repository reality and update only the managed solution documents when necessary.

## Inputs
- Target solution identifier and repository path.
- Active solution profile.
- Existing stable documentation, if present.
- Local repository documentation and repository source evidence visible to the workflow.

## Managed Documents
- `context/overview.md`
- `business/business-rules.md`
- `business/workflows.md`
- `architecture/architecture-overview.md`
- `architecture/module-map.md`

## Artifacts and Outputs
- Contract artifact defining boundaries and approved physical targets.
- Repository-state artifact summarizing reviewed repository evidence.
- Decision artifact defining mode and per-document actions.
- Final setup-documentation report and output payload.

## Expected Phase Model
- Step 1: load framework doctrine and produce the documentation contract artifact.
- Step 2A: load the full allowed repository evidence set.
- Step 2B: synthesize repository understanding into the repository-state artifact.
- Step 3: decide `ALIGNED`, `UPDATE`, or `BOOTSTRAP` and assign one action per managed document.
- Write steps: write only approved managed documents when the decision requires it.
- Final step: return the final workflow result after all required writes are complete.

## Allowed Tools by Phase
- Step 1 contract phase: no tools.
- Repository evidence phase: `get_next_file_batch` only.
- Repository-state synthesis phase: no tools.
- Decision phase: no tools.
- Per-document write phases: `write_file` only when the decision for that target is `CREATE` or `UPDATE`.
- Final report phase: no tools.

## Validation Expectations
- Framework doctrine required for setup-documentation must be present before Step 1 completes.
- Repository evidence acquisition must review all allowed context files before Step 2A completes.
- Decision output must contain exactly one action for each managed document.
- Write phases must use only approved physical targets and must not write any other files.
- Final reporting must not claim writes that did not already succeed.

## Final Report Expectations
- Report the final mode.
- Summarize the actions taken or intentionally skipped.
- Use logical managed-document paths in the visible report.
- Keep reasoning evidence-based and concise.

## Lifecycle Independence
- Setup Documentation runs independently from requirement workflows.
- It must not mutate requirement lifecycle state.
- It must not create delivery backlog or implementation tasks as workflow output.
