# SDLC Contracts Layer

This package defines the standard structured contracts used by the Iteration SDLC Orchestrator.

These contracts are not solution requirements themselves. They are framework-level structures that govern:
- how requirements enter the orchestrator
- how workflows receive inputs
- how workflows return outputs
- how open questions and decisions are represented
- how traceability is preserved across the SDLC lifecycle

## Purpose

The contracts layer ensures that:
- users submit solution-related requirements only
- agents operate inside fixed workflow boundaries
- workflow inputs and outputs are strongly structured
- cockpit can manage requirements, questions, decisions, backlog work, and workflow history consistently
- documentation and knowledge updates can be attached to each workflow run in a standard way

## Core contracts

### 1. Requirement
Represents a user-submitted solution requirement.
A requirement may be business, functional, technical, or non-functional.

Defined in:
- `requirement.schema.json`

### 2. Backlog Item
Represents an implementation work item derived from one or more requirements during planning.
A backlog item is not the canonical intake object.

Defined later as planning/execution contracts mature.

### 3. Open Question
Represents an unresolved issue, ambiguity, dependency, or missing clarification discovered during a workflow.

Defined in:
- `open-question.schema.json`

### 4. Decision
Represents an architectural, business, technical, or process decision made during the requirement lifecycle.

Defined in:
- `decision.schema.json`

### 5. Workflow Input Envelope
Represents the governed input passed to a workflow execution.
It wraps the workflow metadata plus the entities the workflow is allowed to operate on.

Defined in:
- `workflow-input-envelope.schema.json`

### 6. Workflow Output Envelope
Represents the governed result produced by a workflow execution.
It contains the primary result plus any generated requirements, backlog work, questions, decisions, and documentation updates.

Defined in:
- `workflow-output-envelope.schema.json`

## Design rules

1. Users provide solution requirements, not agent instructions.
2. Requirements are the canonical intake object for solution needs.
3. Backlog items are implementation work items produced by planning and used by execution workflows.
4. Agents may only perform work allowed by:
   - workflow type
   - agent domain
   - agent skills
   - input/output contract
5. Every workflow must consume a standard input envelope.
6. Every workflow must produce a standard output envelope.
7. Workflows may generate:
   - new requirements
   - backlog items
   - open questions
   - decisions
   - documentation updates
   - knowledge updates
8. Cockpit must be able to persist and display all contract instances and their relationships.

## Lifecycle intent

A requirement can move through stages such as:
- submitted
- under-analysis
- analyzed
- planned
- in-execution
- in-testing
- ready-for-delivery
- delivered
- rejected
- blocked

A backlog item can move through stages such as:
- draft
- planned
- ready
- in-progress
- blocked
- done
- cancelled

The exact lifecycle and transition rules should be enforced by workflow orchestration rules, not by freeform agent behavior.

## Notes

These contracts are framework definitions.
They should later be aligned with:
- domain entities
- API contracts
- cockpit views
- workflow runtime persistence

## File reference rule

Workflow input JSON must carry file references and lightweight metadata only.
It must not embed raw file contents for repository files, markdown documents, JSON artifacts, logs, or source files.
Agents should inspect referenced files on demand through workflow tools such as `read_file`.
