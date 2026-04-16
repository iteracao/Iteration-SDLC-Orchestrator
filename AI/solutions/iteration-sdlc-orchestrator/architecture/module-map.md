# Module Map

## Purpose

This document maps the current repository modules to their responsibilities and indicates the most visible missing slices.

## Current Modules

### `Iteration.Orchestrator.Domain`
Purpose:
- core orchestration entities and enums

Current contents:
- backlog item and status
- priority enum
- workflow run and status
- agent task run and status
- solution target
- analysis report

### `Iteration.Orchestrator.Application`
Purpose:
- command handlers, abstractions, and workflow orchestration

Current contents:
- backlog item creation handler
- solution registration handler
- solution setup handler
- analyze-request handler
- config, bridge, artifact, setup, and analyst abstractions
- agent/request/output application contracts

### `Iteration.Orchestrator.Infrastructure`
Purpose:
- technical implementations for persistence, config, artifacts, AI connectivity, and setup services

Current contents:
- EF Core `AppDbContext`
- SQLite configuration in API startup
- filesystem config catalog
- filesystem artifact store
- filesystem solution setup service
- Ollama service and options

### `Iteration.Orchestrator.AgentHost`
Purpose:
- agent execution implementation used by the application layer

Current contents:
- `MicrosoftAgentFrameworkSolutionAnalystAgent`

### `Iteration.Orchestrator.SolutionBridge`
Purpose:
- controlled repository inspection

Current contents:
- local filesystem bridge for snapshot, file read, repository tree, and simple search

### `Iteration.Orchestrator.Api`
Purpose:
- public HTTP surface for orchestration actions

Current contents:
- solution registration/listing
- backlog item creation/listing
- setup-solution workflow start
- analyze-request workflow start
- workflow run lookup
- simple AI code analysis endpoint

### `Iteration.Orchestrator.Cockpit`
Purpose:
- operator-facing web UI

Current contents:
- starter layout and placeholder pages only

### `AI/framework`
Purpose:
- executable workflow/agent/profile configuration and framework documentation

Current contents:
- workflow YAML definitions for the intended SDLC flow
- profile rules
- agent definitions and schemas
- base template solution overlay

### `AI/solutions/iteration-sdlc-orchestrator`
Purpose:
- solution-specific documentation and continuity workspace

Current contents:
- now bootstrapped with real documentation grounded in the current codebase

## Planned Modules / Planned Capability Areas

Not yet implemented as separate code modules, but clearly part of the platform direction:
- requirement management richer than current backlog item handling
- decisions management
- open questions management
- documentation and knowledge update orchestration
- planning workflow execution
- implementation workflow execution
- testing and review workflow execution
- delivery and history management workflows
- full cockpit management views

## Known Drift

### Vision vs implemented domain
The current domain models a backlog item rather than a richer requirement entity. This is enough for a starter, but it does not yet match the intended requirement-centric operating model.

### Vision vs cockpit
The cockpit is currently a starter shell and does not yet manage workflow state, decisions, questions, documentation, or artifact inspection.

### Vision vs workflow engine
The framework already defines multiple workflows in YAML, but only `setup-solution` and the analysis path are wired into application execution today.

### Vision vs documentation continuity
The setup workflow seeds documentation files, and the analysis workflow reads them, but there is no implemented automatic documentation mutation/update pipeline yet.

### Filesystem path casing risk
The framework content lives under `AI/...`, while some code paths in setup and analysis reference `ai/...`. This may be harmless on Windows but is a real portability risk on case-sensitive filesystems.
