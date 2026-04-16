# Solution Overview

## Purpose

Iteration SDLC Orchestrator is a solution-centric orchestration platform intended to manage the lifecycle of software requirements through governed workflows executed by constrained AI agents.

The platform is meant to act as a central cockpit for:
- solution registration
- requirement intake
- workflow execution
- structured analysis and other workflow outputs
- long-term documentation and knowledge continuity

## Scope

The current repository covers the first vertical slice of that vision:
- registering a target solution
- bootstrapping a standard SDLC knowledge workspace for that solution
- creating backlog items
- executing an analysis workflow
- persisting workflow run history and structured analysis output
- exposing this data through a starter API and a minimal cockpit UI

## Current State

The current implementation is an early but real working starter.

Implemented today:
- SQLite-backed persistence using `AppDbContext`
- domain entities for solutions, backlog items, workflow runs, agent task runs, and analysis reports
- configuration loading from `AI/framework`
- setup workflow execution through `SetupSolutionHandler`
- analysis workflow execution through `StartAnalyzeSolutionRunHandler`
- read-only filesystem solution bridge for repository scanning and file reading
- Microsoft Agent Framework-named analyst host currently backed by an Ollama-driven JSON generation implementation
- API controllers for solutions, backlog items, workflow runs, and a simple AI endpoint
- starter MudBlazor cockpit pages for dashboard, solutions, and backlog

Not implemented yet:
- full requirement lifecycle beyond a basic backlog item
- planning, implementation, testing, review, and delivery workflow execution in application code
- first-class decisions, open questions, and documentation update tracking in persistence
- real cockpit management views for workflow artifacts and knowledge entities
- automatic documentation update execution after every workflow

## Notes

The `AI/solutions/iteration-sdlc-orchestrator/` workspace is currently the canonical documentation root for this solution.

The codebase already assumes this knowledge structure exists. For example, the analysis workflow explicitly reads solution knowledge documents from this location before calling the analyst agent.
