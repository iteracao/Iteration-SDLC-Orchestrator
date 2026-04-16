# Iteration SDLC Orchestrator Roadmap

## Current baseline

The platform now treats **requirements** as the canonical intake object and reserves **backlog items** for implementation work derived from planning.

## Immediate next steps

### 1. Backlog redesign as implementation planning
Backlog items should become explicit implementation work units linked to requirements.

Target direction:
- one requirement can produce many backlog items
- each backlog item represents one implementation slice, iteration, or work item
- planning workflows generate backlog items
- execution workflows run against backlog items

Suggested fields to keep or evolve:
- `RequirementId`
- `TargetSolutionId`
- `Title`
- `Description`
- `WorkflowCode`
- `Priority`
- `Status`
- optional future fields like `ParentBacklogItemId`, `IterationLabel`, `Estimate`

### 2. Planning workflow
Add a workflow such as `plan-requirement` or `design-solution-change`.

Input:
- `RequirementId`

Outputs:
- backlog items
- additional derived requirements if needed
- open questions
- decisions
- roadmap or implementation sequencing notes

### 3. Workflow root clarity
Keep workflow ownership explicit:
- solution-driven workflows: setup
- requirement-driven workflows: analyze, clarify, design, plan
- backlog-driven workflows: implement, test, review, deliver

### 4. Cockpit evolution
Add dedicated cockpit slices for:
- requirement detail
- workflow run detail
- backlog planning board
- requirement-to-backlog traceability
- decision and question review

### 5. Documentation automation
After each workflow:
- update solution history
- update requirement status/history
- persist generated documentation updates
- expose generated artifacts in cockpit

## Modeling rule that must remain stable

- **Requirement** is the source of truth for what the solution needs.
- **Backlog item** is a unit of implementation work created because of one or more requirements.
- **Backlog must not be the original intake object for user-submitted solution needs.**
