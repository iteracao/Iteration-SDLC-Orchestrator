# Business Rules

## Purpose

These rules describe how the Iteration SDLC Orchestrator is intended to operate as a managed SDLC platform. Where a rule is already enforced in code, that is noted. Where it is still target behavior, that is also stated clearly.

## Canonical Rules

### 1. The platform is solution-centric
All managed work belongs to a registered target solution.

Current implementation evidence:
- `BacklogItem` belongs to `TargetSolutionId`
- `WorkflowRun` carries `TargetSolutionId`
- `SolutionTarget` stores repository path, profile, and overlay configuration

### 2. Users provide solution requirements, not freeform agent instructions
The intended operating model is requirement-driven. Users should submit business, functional, technical, or non-functional requirements for a solution.

Current implementation status:
- partially represented by `BacklogItem`
- not yet fully enforced as a rich requirement contract

### 3. Work is executed through workflows
A requirement or backlog item is processed by a named workflow.

Current implementation evidence:
- `BacklogItem.WorkflowCode`
- workflow definitions loaded from `AI/framework/workflows`
- API endpoints for `setup-solution` and `analyze-request`

### 4. Agents are constrained by workflow and configuration
Agents may only perform configured work according to their domain, tools, and workflow role.

Current implementation evidence:
- workflow definition specifies `primaryAgent`
- agent definition specifies allowed tools, prompt, and output schema

Current limitation:
- domain and skill boundaries are documented and configured, but not yet deeply enforced in runtime policy checks

### 5. Every workflow must use governed contracts
Inputs and outputs must follow standard structures so that workflow outputs can be persisted, inspected, and consumed by later workflows.

Current implementation evidence:
- setup workflow returns a structured `SolutionSetupResult`
- analysis workflow builds `SolutionAnalysisRequest`
- analyst returns structured `SolutionAnalysisResult`
- `AnalysisReport` persists structured fields plus raw JSON

### 6. Documentation and knowledge must be part of the process
The orchestrator must maintain solution continuity, not only execute isolated runs.

Current implementation evidence:
- setup workflow creates the standard knowledge workspace
- analysis workflow reads existing solution documentation before analysis

Current limitation:
- automatic update of documentation, decisions, open questions, and gaps after every workflow is not yet implemented

### 7. Cockpit must manage the evolving solution state
The cockpit should expose not only runs, but also requirements, questions, decisions, documentation, and knowledge status.

Current implementation status:
- target rule only
- cockpit is still a placeholder shell

### 8. Workflows may generate new managed items
A workflow may produce:
- new requirements
- open questions
- decisions
- known gaps
- documentation updates

Current implementation status:
- target rule only
- no first-class persistence for these items yet

### 9. History must be preserved
The platform should preserve traceability across iterations instead of relying on ephemeral prompts or unstored outputs.

Current implementation evidence:
- workflow runs are persisted
- agent task runs are persisted
- analysis report is persisted
- artifacts are saved to the artifact store

## Exceptions

### V1 simplification: backlog item as requirement surrogate
The current implementation uses `BacklogItem` as the starting work item. This is acceptable for the initial vertical slice but does not yet match the richer long-term requirement model.

### V1 simplification: setup and analysis are the only wired workflows
The framework defines a broader SDLC flow, but the application currently only executes the setup and analysis slice.

## Open Clarifications

1. Should `BacklogItem` evolve into `Requirement`, or should requirement become a richer aggregate while backlog remains a planning projection?
2. What exact statuses and state transitions should the requirement lifecycle support?
3. Which workflow outputs should automatically create decisions, open questions, and knowledge deltas?
4. Should documentation updates happen as part of each workflow or as a dedicated follow-up workflow triggered after each run?
