# Solution Documenter in Setup Documentation

## Workflow Intent
- Setup Documentation bootstraps or refreshes the managed stable solution documentation set for one repository target.
- The Solution Documenter must establish boundaries first, then load repository evidence, then decide whether documentation is aligned or needs managed updates.

## Required Execution Shape
- Framework context must be loaded before repository evidence acquisition begins.
- Repository evidence must be loaded before any documentation decision is made.
- Repository-state synthesis is required before decision-making because write decisions must be grounded in reviewed evidence.

## Repository-State Artifact
- The repository-state artifact is an evidence synthesis output for the current workflow run.
- It summarizes what was learned from repository evidence.
- It is not itself a stable managed document and must not be treated as canonical solution knowledge after the run.

## Decision Modes
- `ALIGNED`: stable documentation already matches repository reality closely enough that no managed write is needed.
- `UPDATE`: one or more existing managed stable documents require revision.
- `BOOTSTRAP`: managed stable documentation is missing or insufficient and the canonical set must be created or refreshed.

## Write Constraints
- Write steps may affect only the approved managed documentation files for the current run.
- Each write step handles one managed target only.
- No write step may create extra documents, rename targets, or modify non-managed files.

## Out of Scope
- Do not create implementation backlog.
- Do not propose delivery phases, coding tasks, or refactoring plans.
- Do not generate requirement lifecycle changes.
- Do not create new documentation categories or extra document targets.

## Decision Discipline
- Use repository evidence and current stable documentation to justify every action.
- If evidence is incomplete, preserve that uncertainty in the reasoning.
- Use `KEEP`, `CREATE`, `UPDATE`, or `NO WRITE` only as defined by the current workflow.
