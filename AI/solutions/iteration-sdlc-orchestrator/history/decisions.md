# Decisions

## Active Decisions

### D-001 — The orchestrator is solution-centric
The platform manages work in the context of a registered target solution rather than as isolated prompts or generic tasks.

Rationale:
- current persistence and workflow models already bind work to `SolutionTarget`
- repository path, profile, and solution overlay are solution-scoped

### D-002 — The framework is workflow-driven, not freeform agent-driven
Work must run through named workflows with configured rules, inputs, outputs, and owning agents.

Rationale:
- `AI/framework/workflows/*.yaml` already models the intended execution backbone
- current application code reads workflow definitions before executing handlers

### D-003 — Agents are constrained by configuration, not allowed to improvise scope
Agents should be treated as controlled executors bounded by workflow type, domain, and skills.

Rationale:
- this is central to the orchestrator vision and prevents the system from degrading into prompt-based tasking

### D-004 — The SDLC knowledge workspace is a first-class part of the solution
Each managed solution must have a standard documentation workspace under the solution repository.

Rationale:
- `FileSystemSolutionSetupService` seeds the knowledge structure
- `StartAnalyzeSolutionRunHandler` reads those documents as workflow input

### D-005 — The first implemented vertical slice is setup + analysis
The current repository intentionally implements setup and request analysis before the later SDLC phases.

Rationale:
- reflected in the current handlers, endpoints, and persisted artifacts

### D-006 — Real code and documents are the source of truth for analysis
Analysis must inspect actual repository evidence and existing documentation rather than rely on naming assumptions.

Rationale:
- reflected in profile rules, solution analyst prompt, and the current solution bridge behavior

## Superseded Decisions

None recorded yet.
