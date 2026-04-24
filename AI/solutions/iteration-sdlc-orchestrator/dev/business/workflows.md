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

- runs as a deterministic system workflow; it does not invoke an AI agent
- validates repository path access, main solution file format, target code normalization, and GitHub URL shape
- looks up GitHub metadata, including default branch and private/public access
- calls `FileSystemSolutionSetupService`
- prepares setup artifacts
- commits `Solution`, `SolutionTarget`, `WorkflowRun`, and `AgentTaskRun` only after preparation succeeds
- returns `NextWorkflowCode = "update-target-documentation"` as a legacy handoff hint even though the runnable follow-up workflow is now `setup-documentation`

Filesystem/bootstrap behavior:

- copies overlay/source target files into the new repository root when `OverlayTargetId` is supplied
- skips `.git`, `.vs`, `bin`, `obj`, and a few junk files during overlay copy
- runs `git init` if `.git` is missing
- adds `origin` if missing
- creates the main `.sln` file if missing
- creates baseline target docs if missing

Persisted outputs and state changes:

- deterministic `WorkflowRun` and succeeded `AgentTaskRun`
- setup artifacts under `data/runs/<workflowRunId>/`
- baseline docs under `AI/solutions/<target-storage-code>/`
- updated `Solution` and `SolutionTarget`

Workflow state result:

- setup is completed and validated in one deterministic pass because it is a system workflow, not a human-reviewed requirement workflow

Corrected execution order:

- phase 1: validate inputs, resolve metadata, run filesystem/bootstrap preparation, and save setup artifacts
- phase 2: persist `Solution`, `SolutionTarget`, `WorkflowRun`, and `AgentTaskRun` inside one explicit DB transaction

Failure behavior:

- if validation, metadata lookup, bootstrap, or artifact writing fails, no DB state is committed
- if DB persistence fails after preparation, the DB transaction is rolled back and setup artifacts for the run are cleaned up
- repository/bootstrap changes are intentionally not rolled back automatically; setup is expected to be safe to rerun against the prepared repository

Important current limitations:

- update/setup still resolves only one target per solution
- setup does not bootstrap docs from repository truth; it only seeds missing templates
- setup does not scaffold `design/latest-design.md` or `delivery/latest-plan.md`
- setup does not generate solution knowledge or perform lifecycle normalization
- source-grounded documentation understanding/update now happens through the separate `setup-documentation` workflow rather than inside `setup-solution`
- the legacy `update-target-documentation` handoff value no longer matches the implemented follow-up workflow name

### 2. Setup Documentation

Source:

- API: `POST api/workflow-runs/setup-documentation`
- runtime handler: `SetupDocumentationHandler`

Trigger and actual inputs:

- `TargetSolutionId`
- `RequestedBy`

What the current code does:

- creates and queues a target-scoped `WorkflowRun`
- loads the canonical managed doc set under `AI/solutions/<target-storage-code>/`
- gathers allowed repository/context files for the selected target
- runs a phased documentation agent that first establishes the doc contract, then loads repository evidence, then synthesizes the repository state, then decides per-document actions
- allows `write_file` only for the canonical managed doc paths for that target
- persists input, output, visible report, and workflow log artifacts

Persisted outputs:

- `setup-documentation.input.json`
- `setup-documentation.output.json`
- `repository-state.md`
- `setup-documentation-report.md`
- `workflow-report.md`

Workflow state result:

- the run moves from `Pending` -> `Running` -> `Completed` on success
- because this is target-scoped and not requirement-scoped, validating the run does not advance any requirement lifecycle

Current notes:

- this workflow is the implemented path for bootstrapping or refreshing stable target docs from repository evidence
- it can write only the managed target docs, not arbitrary repository files
- document browsing in the cockpit is still solution-based even though the workflow itself is target-scoped

### 3. Analyze Request

Source:

- API: `POST api/workflow-runs/analyze-request`
- runtime handler: `StartAnalyzeSolutionRunHandler`

Trigger and actual inputs:

- `RequirementId`
- `RequestedBy`

Execution context gathered before the agent call:

- prompt 1 bootstrap context from the current workflow definition, workflow rules, agent definition, agent rules, profile rules, and loaded solution knowledge docs
- prompt 2 repository structure built from the real repository tree, filtered by `.gitignore`
- prompt 3 requirement details plus repository inspection instructions and the final Markdown report template
- target docs from `context`, `business`, `architecture`, `history`, and `analysis/latest-analysis.md`
- framework workflow metadata and profile rules

Persisted outputs:

- `AnalysisReport`
- saved artifact: `analysis-report.md`
- workflow log content in `data/workflow-logs/<workflowRunId>.log`

Requirement state changes:

- run start: requirement becomes `Analyze`
- run success: workflow run becomes `Completed`
- validation: requirement becomes `Design`
- run failure: workflow run becomes `Error`; requirement stays `Pending`

Current notes:

- execution is queued/background
- analysis is requirement-driven
- the final workflow result is a plain Markdown report, not a JSON workflow output envelope
- prompts and responses for all three phases are logged, but only the final report is saved as a run artifact

### 4. Design Solution Change

Source:

- API: `POST api/workflow-runs/design-solution-change`
- runtime handler: `StartDesignSolutionRunHandler`

Precondition:

- requirement must already be in `Design`

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

- run start: requirement stays `Design`
- run success: workflow run becomes `Completed`
- validation: requirement becomes `Plan`
- run failure: workflow run becomes `Error`; requirement stays `Design`

Current notes:

- design is correctly blocked on an analyzed requirement
- the report is persisted, but no stable `design/latest-design.md` maintenance path is implemented automatically

### 5. Plan Implementation

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

- run start: requirement stays `Plan`
- run success: workflow run becomes `Completed`
- validation: requirement becomes `Implement`
- run failure: workflow run becomes `Error`; requirement stays `Plan`

Current notes:

- planning is requirement-driven and downstream from design
- generated backlog slices are ordered by `PlanningOrder`
- backlog creation through planning is the main architectural direction, but direct backlog creation API support still exists

### 6. Implement Solution Change

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

- requirement start: requirement stays `Implement`
- backlog success: `AwaitingValidation`
- workflow success: run becomes `Completed`
- validation: backlog becomes `Validated`, requirement becomes `AwaitingDecision`
- backlog failure: `ImplementationError`
- workflow failure: run becomes `Error`; requirement stays `Implement`

Important current gap:

- the current implementation agent is read-only and cannot edit repository files
- the workflow therefore records an implementation result and advances statuses, but it does not actually apply code changes to the target repository yet

## Shared Lifecycle Rules

- The authoritative lifecycle model lives in `dev/business/requirement-workflow-lifecycle.md`.
- Current runtime workflow run states are `Pending`, `Running`, `Completed`, `Error`, `Validated`, and `Cancelled`.
- Current runtime requirement states are `Pending`, `Analyze`, `Design`, `Plan`, `Implement`, `AwaitingDecision`, `Completed`, and `Cancelled`.
- Requirement progression happens only when the corresponding workflow run is explicitly validated.
- Validating the last executable workflow moves the requirement to `AwaitingDecision`; it does not auto-complete the requirement.
- A stage stays blocked while the latest run for that stage is `Pending`, `Running`, or `Completed`.
- Generic validation and cancellation API actions exist for workflow runs, and a workflow cancellation only terminates the requirement lifecycle when explicitly requested.

## Cockpit Refresh

- The cockpit refreshes immediately after run, validate, and cancel actions.
- Polling remains active while the selected target has runs in `Pending`, `Running`, or `Completed`.
- Polling is snapshot-based for requirement, backlog, and workflow-run status fields, so unchanged polls do not trigger UI updates.

## Intended Later Stages

The framework and cockpit clearly point toward the final direction:

- `test-solution-change`
- `review-implementation`
- `deliver-solution-change`
- `update-solution-history`

Current runtime status:

- framework YAML exists for these later stages
- cockpit shows placeholder Test, Review, Deliver, and Documentation lanes
- current executable lifecycle still ends at implementation validation and then enters `AwaitingDecision`
- there are no application handlers, executor branches, or first-class persistence/report flows for these stages yet

## Exceptions

- validation is not yet a first-class workflow stage; it is represented today by backlog/requirement statuses and a cockpit placeholder lane
- document browsing is still solution-based while runtime execution is target-based
- setup and update flows are still effectively single-target flows
- recent architecture changes were made directly in code and UI, so the managed docs have to be manually realigned to the codebase

## Open Clarifications

- when implementation becomes truly file-mutating, should the current workflow keep the same name and state progression or be split into prepare/apply/validate slices
- should setup eventually create the full doc set expected by later handlers, including `design/latest-design.md` and `delivery/latest-plan.md`
