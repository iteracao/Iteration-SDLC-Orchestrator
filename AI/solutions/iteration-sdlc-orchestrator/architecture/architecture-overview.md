# Architecture Overview

## Purpose

This document describes the implemented architecture of the current Iteration SDLC Orchestrator repository and highlights the intended architectural direction where relevant.

## Main Components

### 1. Domain Layer
Project: `src/Iteration.Orchestrator.Domain`

Current domain entities:
- `SolutionTarget`
- `BacklogItem`
- `WorkflowRun`
- `AgentTaskRun`
- `AnalysisReport`

This layer currently models only the initial orchestration slice. It does not yet model decisions, open questions, documentation items, or a richer requirement lifecycle.

### 2. Application Layer
Project: `src/Iteration.Orchestrator.Application`

Responsibilities implemented today:
- application contracts and abstractions
- solution registration
- solution setup workflow orchestration
- backlog item creation
- analyze-request workflow orchestration
- interfaces for configuration, artifact storage, solution bridge, solution setup service, and analyst agent

The key current workflow handlers are:
- `SetupSolutionHandler`
- `StartAnalyzeSolutionRunHandler`

### 3. Infrastructure Layer
Project: `src/Iteration.Orchestrator.Infrastructure`

Responsibilities implemented today:
- EF Core SQLite persistence through `AppDbContext`
- artifact persistence to filesystem through `FileSystemArtifactStore`
- filesystem-backed YAML/markdown config loading through `FileSystemConfigCatalog`
- solution setup service through `FileSystemSolutionSetupService`
- Ollama integration through `OllamaService`

### 4. Agent Host
Project: `src/Iteration.Orchestrator.AgentHost`

Current responsibility:
- host the solution analyst implementation

`MicrosoftAgentFrameworkSolutionAnalystAgent` is the current solution analyst adapter used by the application. Despite the class name, the current behavior should be understood from the code itself: it builds a grounded prompt, calls an Ollama endpoint, and parses structured JSON output into `SolutionAnalysisResult`.

### 5. Solution Bridge
Project: `src/Iteration.Orchestrator.SolutionBridge`

Current responsibility:
- inspect a target solution on the local filesystem without mutating it

`LocalFileSystemSolutionBridge` currently provides:
- repository tree listing
- file reading
- naive text search across selected file types
- basic repository snapshot extraction

This is intentionally read-only in the current slice.

### 6. API Layer
Project: `src/Iteration.Orchestrator.Api`

Current responsibility:
- expose orchestration capabilities through HTTP endpoints

Current controllers:
- `SolutionsController`
- `BacklogController`
- `WorkflowRunsController`
- `AiController`

The API currently wires the main handlers, database context, config catalog, solution bridge, setup service, artifact store, and analyst implementation.

### 7. Cockpit UI
Project: `src/Iteration.Orchestrator.Cockpit`

Current responsibility:
- starter operator-facing UI

The cockpit is currently only a minimal starter with placeholder pages:
- `/` dashboard starter
- `/solutions`
- `/backlog`

The implemented UI does not yet expose the intended full operational model.

## Runtime Flow Today

### Setup Solution Flow
1. API receives a setup request.
2. `SetupSolutionHandler` loads workflow and profile config.
3. A `SolutionTarget` is created if missing.
4. A `WorkflowRun` and `AgentTaskRun` are created.
5. `FileSystemSolutionSetupService` ensures repository/git basics and seeds the SDLC knowledge workspace.
6. Result artifacts are written to the artifact store.
7. Workflow run is marked succeeded.

### Analyze Request Flow
1. API receives `StartAnalyzeRunRequest`.
2. `StartAnalyzeSolutionRunHandler` loads backlog item, solution, workflow, profile, and agent definitions.
3. A `WorkflowRun` and `AgentTaskRun` are created.
4. The target solution is inspected through `ISolutionBridge`.
5. Existing solution knowledge documents are loaded from `AI/solutions/<solutionCode>/...`.
6. A `SolutionAnalysisRequest` is built and passed to the analyst agent.
7. Structured output is persisted as `AnalysisReport` and saved as workflow artifacts.
8. Workflow and backlog statuses are updated.

## Key Constraints

- The configuration model is filesystem-based and currently loaded from `AI/framework`.
- The solution bridge is read-only.
- The knowledge workspace is markdown-file based.
- The current persistence model is intentionally small and only supports the setup + analysis slice.
- The cockpit does not yet represent the full managed solution state.

## Architectural Direction

The intended architecture is a contract-driven SDLC platform where:
- requirements are the central lifecycle objects
- workflows govern allowed work
- agents are constrained by domain and skills
- documentation and knowledge are updated continuously
- the cockpit manages requirements, decisions, questions, documentation, history, and workflow state

The current codebase is the first slice toward that target, not the completed architecture.
