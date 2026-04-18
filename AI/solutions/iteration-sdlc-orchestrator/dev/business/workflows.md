# Business Workflows

## Purpose

This document describes the workflow pipeline that is implemented today, the intended later stages, and the gaps between the two.

## Main Workflows

### 1. Setup Solution

Source:

- API: `POST api/workflow-runs/setup-solution`
- framework definition: `AI/framework/workflows/setup-solution/workflow.yaml`

Trigger and actual inputs:

- `SolutionId` for update-or-create
- `Name`, `Description`
- `RepositoryPath`
- `MainSolutionFile`
- `ProfileCode`
- `TargetCode`
- `OverlayTargetId`
- `RemoteRepositoryUrl`
- `RequestedBy`

What the current code does:

- validates repository path access, main solution file format, target code normalization, and GitHub URL shape
- looks up GitHub metadata, including default branch and private/public access
- creates or updates `Solution`
- creates or updates the single `SolutionTarget`
- creates a real `WorkflowRun` and `AgentTaskRun`
- calls `FileSystemSolutionSetupService`
- saves `setup-solution.input.json` and `solution-setup-result.json` artifacts

Filesystem/bootstrap behavior:

- copies overlay/source target files into the new repository root when `OverlayTargetId` is supplied
- skips `.git`, `.vs`, `bin`, `obj`, and a few junk files during overlay copy
- runs `git init` if `.git` is missing
- adds `origin` if missing
- creates the main `.sln` file if missing
- creates baseline target docs if missing

Persisted outputs and state changes:

- `WorkflowRun` and `AgentTaskRun`
- setup artifacts under `data/runs/<workflowRunId>/`
- baseline docs under `AI/solutions/<target-storage-code>/`
- updated `Solution` and `SolutionTarget`

Important current limitations:

- the handler requires `RepositoryPath` to already exist, even though the workflow YAML and setup service still describe repo creation when missing
- the handler currently requires `RemoteRepositoryUrl`, even though the workflow YAML marks it optional
- update/setup still resolves only one target per solution
- setup does not bootstrap docs from repository truth; it only seeds missing templates
- setup does not scaffold `design/latest-design.md` or `delivery/latest-plan.md`

### 2. Analyze Request

Source:

- API: `POST api/workflow-runs/analyze-request`
- runtime handler: `StartAnalyzeSolutionRunHandler`

Trigger and actual inputs:

- `RequirementId`
- `RequestedBy`

Execution context gathered before the agent call:

- target repository snapshot
- repository file list from `src/`
- repository markdown docs outside `AI/solutions/...`
- substring search hits for the requirement title
- up to five sampled file contents from search hits
- target docs from `context`, `business`, `architecture`, `history`, and `analysis/latest-analysis.md`
- framework workflow metadata and profile rules

Persisted outputs:

- `AnalysisReport`
- generated derived requirements
- generated open questions
- generated decisions
- saved artifacts: `analysis-request.input.json`, `analysis-report.json`
- workflow log content in `data/workflow-logs/<workflowRunId>.log`

Requirement state changes:

- start: `under-analysis`
- success: `analyzed`
- failure: `analysis-failed`

Current notes:

- execution is queued/background
- analysis is requirement-driven
- output can include generated requirements, questions, decisions, documentation updates, and recommended next workflows

### 3. Design Solution Change

Source:

- API: `POST api/workflow-runs/design-solution-change`
- runtime handler: `StartDesignSolutionRunHandler`

Precondition:

- requirement must already be `analyzed`

Trigger and actual inputs:

- `RequirementId`
- `RequestedBy`

Execution context:

- latest analysis report for the requirement
- repository snapshot, file list, docs list, search hits, sampled files
- target docs plus `analysis/latest-analysis.md`
- framework workflow metadata and profile rules

Persisted outputs:

- `DesignReport`
- generated open questions
- generated decisions
- saved artifacts: `design-request.input.json`, `design-report.json`

Requirement state changes:

- start: `under-design`
- success: `designed`
- failure: `design-failed`

Current notes:

- design is correctly blocked on an analyzed requirement
- the report is persisted, but no stable `design/latest-design.md` maintenance path is implemented automatically

### 4. Plan Implementation

Source:

- API: `POST api/workflow-runs/plan-implementation`
- runtime handler: `StartPlanImplementationRunHandler`

Precondition:

- requirement must already be `designed`

Trigger and actual inputs:

- `RequirementId`
- `RequestedBy`

Execution context:

- latest design report for the requirement
- repository snapshot, file list, docs list, search hits, sampled files
- target docs plus `analysis/latest-analysis.md` and optional `design/latest-design.md`
- framework workflow metadata and profile rules

Persisted outputs:

- `PlanReport`
- generated backlog items linked to the plan workflow run
- generated open questions
- generated decisions
- saved artifacts: `plan-request.input.json`, `implementation-plan.json`

Requirement state changes:

- start: `under-planning`
- success: `planned`
- failure: `planning-failed`

Current notes:

- planning is requirement-driven and downstream from design
- generated backlog slices are ordered by `PlanningOrder`
- backlog creation through planning is the main architectural direction, but direct backlog creation API support still exists

### 5. Implement Solution Change

Source:

- API: `POST api/workflow-runs/implement-solution-change`
- runtime handler: `StartImplementSolutionChangeRunHandler`

Preconditions:

- backlog item must exist
- backlog item must be linked to a requirement
- backlog item must be linked to a plan workflow run
- backlog item status must be `NotImplemented` or `ImplementationError`
- all earlier backlog slices in the same plan must already be `Validated` or `Canceled`

Trigger and actual inputs:

- `BacklogItemId`
- `RequestedBy`

Execution context:

- plan report for the backlog item plan run
- repository snapshot, file list, docs list, search hits, sampled files
- target docs plus optional `design/latest-design.md` and `delivery/latest-plan.md`
- framework workflow metadata and profile rules

Persisted outputs:

- `ImplementationReport`
- generated requirements
- generated open questions
- generated decisions
- saved artifacts: `implementation-request.input.json`, `implementation-result.json`

Backlog and requirement state changes:

- requirement start: `under-implementation`
- backlog success: `AwaitingValidation`
- requirement success: `awaiting-implementation-validation`
- backlog failure: `ImplementationError`
- requirement failure: `implementation-failed`

Important current gap:

- the current implementation agent is read-only and cannot edit repository files
- the workflow therefore records an implementation result and advances statuses, but it does not actually apply code changes to the target repository yet

## Intended Later Stages

The framework and cockpit clearly point toward the final direction:

- `test-solution-change`
- `review-implementation`
- `deliver-solution-change`
- `update-solution-history`

Current runtime status:

- framework YAML exists for these later stages
- cockpit shows placeholder Test, Review, Deliver, and Documentation lanes
- there are no application handlers, executor branches, or first-class persistence/report flows for these stages yet

## Exceptions

- validation is not yet a first-class workflow stage; it is represented today by backlog/requirement statuses and a cockpit placeholder lane
- document browsing is still solution-based while runtime execution is target-based
- setup and update flows are still effectively single-target flows
- recent architecture changes were made directly in code and UI, so the managed docs have to be manually realigned to the codebase

## Open Clarifications

- when implementation becomes truly file-mutating, should the current workflow keep the same name and state progression or be split into prepare/apply/validate slices
- should setup eventually create the full doc set expected by later handlers, including `design/latest-design.md` and `delivery/latest-plan.md`
