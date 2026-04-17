# Solution Overview

## Purpose

Iteration SDLC Orchestrator is a solution-centric SDLC orchestration platform. Users submit solution requirements, workflows run AI-assisted analysis and downstream phases inside fixed workflow boundaries, and the cockpit tracks requirements, workflow runs, reports, questions, decisions, backlog items, and generated knowledge.

## Scope

Current source code shows these active capabilities:

- set up a solution/target and seed an AI workspace under `AI/solutions/<solutionCode>`
- manage solutions, targets, requirements, backlog items, open questions, decisions, and workflow runs through the API
- run requirement-driven workflows such as `analyze-request`, `design-solution-change`, `plan-implementation`, and `implement-solution-change`
- execute AI agents through Microsoft Agent Framework with Ollama-backed local models
- view and operate the platform through a Blazor Server cockpit UI
- persist orchestration state in SQLite using EF Core migrations

## Current State

The repository is a working starter rather than an empty skeleton. The solution contains these main projects:

- `Iteration.Orchestrator.Api` - ASP.NET Core Web API and composition root
- `Iteration.Orchestrator.Cockpit` - Blazor Server/MudBlazor operator UI
- `Iteration.Orchestrator.Application` - commands, handlers, workflow orchestration logic, and abstractions
- `Iteration.Orchestrator.Domain` - core entities and workflow/report models
- `Iteration.Orchestrator.Infrastructure` - EF Core, config, artifacts, Ollama service, and setup services
- `Iteration.Orchestrator.AgentHost` - Microsoft Agent Framework adapters for analyst/designer/planner/implementation agents
- `Iteration.Orchestrator.SolutionBridge` - read-only bridge over the target repository

Workflow execution now runs in the background through a queue and hosted service instead of blocking HTTP requests. Workflow logs are written per run and exposed in the cockpit.

## Notes

- The `AI/framework` folder is part of the source of truth for workflow, profile, and agent behavior.
- Solution-specific knowledge lives under `AI/solutions/<solutionCode>` inside the target repository.
- The repository currently reflects a requirement-first orchestration model: requirements are canonical intake; backlog is downstream implementation work.
- Existing solution knowledge templates are still mostly empty placeholders and need bootstrap from repository `.md` files and selected source files.
