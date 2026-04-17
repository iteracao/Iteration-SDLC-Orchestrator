# Module Map

## Purpose

This document maps the current modules/projects to their primary responsibility in the solution.

## Current Modules

### `Iteration.Orchestrator.Api`

Responsibilities:
- ASP.NET Core host
- controller endpoints for solutions, requirements, backlog, workflow runs, documents, questions, decisions, and AI endpoints
- dependency injection registration
- EF Core startup and SQLite connection configuration
- hosted background workflow execution wiring

### `Iteration.Orchestrator.Cockpit`

Responsibilities:
- Blazor Server operator UI
- solution/target selection
- cockpit page for workflow visibility and actions
- requirements page for requirement intake and listing
- workflow log viewing

### `Iteration.Orchestrator.Application`

Responsibilities:
- application commands and handlers
- workflow start logic
- workflow execution coordination
- abstractions for agents, config catalog, solution bridge, artifact store, and setup services
- prompt-building helpers and orchestration services

### `Iteration.Orchestrator.Domain`

Responsibilities:
- business/domain models for solutions, targets, requirements, backlog items, workflow runs, reports, decisions, and questions
- status models such as `WorkflowRunStatus`
- lifecycle methods on core entities

### `Iteration.Orchestrator.Infrastructure`

Responsibilities:
- `AppDbContext` and migrations
- file-system artifact persistence
- file-system solution setup/bootstrap scaffolding
- config catalog from `AI/framework`
- Ollama options/service
- GitHub repository metadata service

### `Iteration.Orchestrator.AgentHost`

Responsibilities:
- Microsoft Agent Framework wrappers around configured local LLM access
- specialized agents for analysis, design, planning, and implementation workflows
- response parsing and JSON extraction logic

### `Iteration.Orchestrator.SolutionBridge`

Responsibilities:
- list repository tree
- read repository files safely
- search across selected file types
- produce solution snapshot data for workflows

## Planned Modules

Based on roadmap and existing code direction, likely growth areas include:

- stronger documentation/bootstrap workflow support
- richer planning/backlog dependency handling
- review/test/deliver workflow slices
- more explicit workflow artifact/log models beyond flat file logs

## Known Drift

- Setup currently seeds documentation structure but does not yet populate it from repository truth.
- Analyze workflow contracts are not yet strict enough to keep analysis separated from design/proposal behavior in all runs.
- Some managed knowledge paths referenced by workflows are aspirational rather than fully maintained today.
- Polling refresh behavior in the cockpit still needs finer-grained update handling to avoid full-page flicker.
