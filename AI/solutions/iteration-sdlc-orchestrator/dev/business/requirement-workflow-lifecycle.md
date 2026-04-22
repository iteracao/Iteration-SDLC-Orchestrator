# Requirement & Workflow Lifecycle

## Requirement & Workflow Lifecycle

This document is the authoritative lifecycle model for the current runtime implementation.

Current executable workflow stages:

- `Analyze`
- `Design`
- `Plan`
- `Implement`

Later framework workflow YAML files may exist, but they are not part of the current executable lifecycle.

### Separation of concerns

- `Requirement` is the business lifecycle for the full request.
- `WorkflowRun` is the execution lifecycle for a single workflow attempt.
- A requirement is never completed automatically.
- Validating the last executable workflow moves the requirement to a final decision state.
- Only an explicit requirement action can move that requirement to `Completed` or `Cancelled`.

### Requirement lifecycle

Persisted requirement states used by the current runtime:

- `Pending`
- `Analyze`
- `Design`
- `Plan`
- `Implement`
- `AwaitingDecision`
- `Completed`
- `Cancelled`

Text diagram:

`Pending -> Analyze -> Design -> Plan -> Implement -> AwaitingDecision -> Completed`

`Pending -> Analyze -> Design -> Plan -> Implement -> AwaitingDecision -> Cancelled`

`Analyze | Design | Plan | Implement -> Cancelled`

Rules:

- `Pending` is intake only.
- `Analyze`, `Design`, `Plan`, and `Implement` mean the requirement is currently in that business stage.
- `AwaitingDecision` means the last executable workflow has already been validated and the requirement now needs an explicit business decision.
- There is no auto-complete after workflow validation.

### Workflow lifecycle

Workflow run states:

- `Pending`
- `Running`
- `Completed`
- `Error`
- `Validated`
- `Cancelled`

Text diagram:

`Pending -> Running -> Completed -> Validated`

`Pending -> Cancelled`

`Running -> Error`

`Completed -> Cancelled`

`Error -> Run new attempt`

Rules:

- A workflow run is an execution record, not the business lifecycle.
- Validation is the transition point that advances requirement business state.
- Validating `Analyze` moves the requirement to `Design`.
- Validating `Design` moves the requirement to `Plan`.
- Validating `Plan` moves the requirement to `Implement`.
- Validating `Implement` moves the requirement to `AwaitingDecision`.

### Badge rules

- Requirement badges represent requirement state only.
- Workflow status is never used as the requirement badge.

Badge mapping:

| Requirement State | Badge |
|------------------|-------|
| `Pending` | `Pending` |
| `Analyze` | `Analyze` |
| `Design` | `Design` |
| `Plan` | `Plan` |
| `Implement` | `Implement` |
| `AwaitingDecision` | `Awaiting Decision` |
| `Completed` | `Completed` |
| `Cancelled` | `Cancelled` |

Forbidden requirement badges:

- `Validated`
- `Awaiting Validation`
- any workflow-status-derived label

### Action matrix

| Requirement State | Workflow State | Allowed Actions |
|------------------|---------------|-----------------|
| `Pending` | none | `Run` first workflow, optional `Delete` if untouched |
| `Analyze` / `Design` / `Plan` / `Implement` | none active | `Run` current stage, `Cancel Requirement` |
| any non-finalized requirement | `Pending` | `Cancel` |
| any non-finalized requirement | `Running` | `View Log`, `View Report` if available |
| any non-finalized requirement | `Completed` | `Validate`, `Cancel`, `View Log`, `View Report` |
| any non-finalized requirement | `Error` | `Run`, `View Log`, `View Report` |
| requirement advanced to next stage | previous workflow `Validated` | no action on the validated run itself; only the next valid current-stage action is shown |
| `AwaitingDecision` | last workflow `Validated` | `Validate Requirement`, `Cancel Requirement`, `View Log`, `View Report` |
| `Completed` | any | no lifecycle actions |
| `Cancelled` | any | no lifecycle actions |

### UI rules

- Use a single current-state action pattern.
- Show only actions valid for the current state.
- Do not show future workflow buttons.
- Requirement cancellation is not available while the requirement is still `Pending`.
- Workflow cancellation does not cancel the requirement unless the caller explicitly chooses to terminate the requirement lifecycle.
- Final decision actions are requirement actions, not workflow actions.

### Examples

New requirement:

- Requirement state: `Pending`
- Visible actions: `Run` first workflow
- Badge: `Pending`

Mid workflow:

- Requirement state: `Design`
- Latest design run state: `Completed`
- Visible actions for that run: `Validate`, `Cancel`, `View Log`, `View Report`
- Requirement badge: `Design`

Final validated workflow:

- Requirement state: `AwaitingDecision`
- Latest implementation run state: `Validated`
- Visible actions: `Validate Requirement`, `Cancel Requirement`, `View Log`, `View Report`
- Requirement badge: `Awaiting Decision`

Completed requirement:

- Requirement state: `Completed`
- Visible lifecycle actions: none
- Requirement badge: `Completed`
