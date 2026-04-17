# Architecture Overview

## Purpose

This document summarizes the current technical architecture implemented in the Iteration SDLC Orchestrator repository.

## Main Components

### Solution structure

The solution file is `Iteration.SDLC.Orchestrator.sln` and currently includes these main projects:

- `Iteration.Orchestrator.Api` - ASP.NET Core API, dependency registration, controllers, Swagger, EF Core startup, and background worker registration
- `Iteration.Orchestrator.Cockpit` - Blazor Server operator UI using MudBlazor
- `Iteration.Orchestrator.Application` - application commands, handlers, workflow start/execution logic, prompt building helpers, and abstractions
- `Iteration.Orchestrator.Domain` - core domain entities for solutions, requirements, backlog, workflows, reports, questions, and decisions
- `Iteration.Orchestrator.Infrastructure` - persistence, config catalog, Ollama service, artifact store, and file-system setup services
- `Iteration.Orchestrator.AgentHost` - Microsoft Agent Framework based adapters for solution analyst/designer/planner/implementation agents
- `Iteration.Orchestrator.SolutionBridge` - read-only access to repository tree, file search, file read, and solution snapshot data

### Execution model

The API is now the orchestration entry point, but long-running workflow execution is not performed inline in HTTP requests. Instead:

- start handlers create workflow runs and queue them
- `IWorkflowExecutionQueue` and `InMemoryWorkflowExecutionQueue` hold queued run ids
- `WorkflowExecutionBackgroundService` dequeues and executes runs in the background
- `WorkflowRunExecutor` dispatches the actual workflow execution path

This is the correct architectural boundary for model latency and workflow reliability.

### Persistence

`AppDbContext` in Infrastructure persists the platform state with SQLite. The domain includes first-class entities for:

- `Solution` and `SolutionTarget`
- `Requirement`
- `BacklogItem`
- `WorkflowRun` and `AgentTaskRun`
- `AnalysisReport`, `DesignReport`, `PlanReport`, `ImplementationReport`
- `OpenQuestion`
- `Decision`

### AI integration

The orchestrator uses:

- Microsoft Agent Framework adapters in `Iteration.Orchestrator.AgentHost`
- Ollama configuration from appsettings/config options
- per-workflow agents such as solution analyst, designer, planner, and implementation agent

### Cockpit UI

The cockpit is a Blazor Server app with MudBlazor. Current pages include solution selection, cockpit workflow view, and requirements/backlog management. The UI now supports background workflow visibility and per-run workflow logs.

## Key Constraints

- Solution bridge is read-only; it analyzes repository content but does not mutate source directly.
- Workflow and profile definitions under `AI/framework` are part of the system behavior contract.
- Managed knowledge lives in markdown files under the target repository, but bootstrap quality is currently limited by placeholder content.
- Current live refresh uses polling; finer-grained delta updates are still a refinement area.

## Notes

- `Program.cs` in the API is currently the main composition root for controllers, EF Core, agents, queue, and hosted background worker.
- The current architecture is a modular monolith, not a distributed system.
- The repository already contains enough domain/application/infrastructure separation to support continued structured growth.
