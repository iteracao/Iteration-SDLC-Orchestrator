# Business Rules

## Purpose

This document captures the business and modeling rules currently implemented in the Iteration SDLC Orchestrator codebase.

## Canonical Rules

### Requirement is the primary intake object

The repository documentation and code treat **Requirement** as the canonical user/stakeholder need. Requirements are created first and then moved through analysis, design, planning, and implementation-related workflows.

### Backlog is downstream implementation work

Backlog items are not the original intake object for solution change requests. They are intended to be generated downstream from planning and represent implementation slices or work units.

### Workflow run is the execution record

A **WorkflowRun** represents the execution of a workflow against a solution target, requirement, or backlog item. Workflow runs carry status, stage, timing, and failure information.

### Requirement lifecycle is explicit

The `Requirement` domain model contains status transitions such as:

- `submitted`
- `under-analysis` / `analyzed` / `analysis-failed`
- `under-design` / `designed` / `design-failed`
- `under-planning` / `planned` / `planning-failed`
- implementation-related states downstream

This indicates the platform expects each workflow phase to update requirement lifecycle state explicitly.

### Reports are first-class workflow outputs

Each major workflow phase has a dedicated report model in the domain:

- `AnalysisReport`
- `DesignReport`
- `PlanReport`
- `ImplementationReport`

These reports are linked to `WorkflowRunId` and persist structured workflow outputs.

### Open questions and decisions are persistent SDLC objects

Analysis/design/planning can generate `OpenQuestion` and `Decision` records. These are stored in the database and are intended to become part of the cockpit review and solution knowledge history.

### Solution knowledge is managed inside the target repository

The setup process creates `AI/solutions/<solutionCode>` in the target repository and seeds markdown documents for context, business, architecture, history, and analysis. These files are intended to become managed solution knowledge.

## Exceptions

- The current setup workflow seeds documentation structure but does not yet perform a strong source-grounded bootstrap of those documents.
- The current analyze workflow can complete successfully even when the seeded knowledge documents are still placeholders, which weakens analysis quality.
- Existing repository documentation outside the SDLC-managed folder is not yet being fully used as bootstrap input, though it should be for existing solutions.

## Open Clarifications

- Which requirement statuses should be treated as final versus retryable across all workflows?
- How should generated documentation updates be applied back into the managed knowledge base after each workflow?
- Should backlog sequencing be purely ordered or fully dependency-driven from the beginning?
- What is the exact governance rule for when a workflow may create new derived requirements versus only raising open questions?
