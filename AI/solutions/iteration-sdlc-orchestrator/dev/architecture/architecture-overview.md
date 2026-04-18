# Architecture Overview

## Purpose

This document summarizes the architecture that is actually implemented in the repository today, including the current transitional seams.

## Main Components

### Solution and target model

- `Solution` is the logical definition shown in the cockpit selector and used for grouping
- `SolutionTarget` is the runtime unit used by setup, requirements, backlog, questions, decisions, workflow runs, and reports
- the target storage code is `<technical-solution-name>/<targetCode>` and is also the managed-doc root under `AI/solutions/...`
- the EF model currently enforces one `SolutionTarget` per `Solution` with a unique index on `SolutionId`

### API and background execution

- `Iteration.Orchestrator.Api` is the composition root and orchestration entry point
- workflow start endpoints create `WorkflowRun` rows and queue them through `IWorkflowExecutionQueue`
- `WorkflowExecutionBackgroundService` dequeues run ids and invokes `WorkflowRunExecutor`
- `WorkflowRunExecutor` dispatches by workflow code to the analyze, design, plan, or implement handler

### Persistence and runtime evidence

SQLite via `AppDbContext` persists:

- `Solution` and `SolutionTarget`
- `Requirement` and `BacklogItem`
- `WorkflowRun` and `AgentTaskRun`
- `AnalysisReport`, `DesignReport`, `PlanReport`, `ImplementationReport`
- `OpenQuestion` and `Decision`

Runtime evidence is split across three stores:

- database rows for workflow/report/domain state
- `data/runs/<workflowRunId>/...` for saved input/output artifacts
- `data/workflow-logs/<workflowRunId>.log` for append-only execution logs

### Cockpit architecture

- the cockpit is a Blazor Server app using MudBlazor
- `SelectedSolutionState` tracks both `Current` solution and `CurrentTarget`
- the main cockpit page renders a stage-based pipeline with lanes for Requirement, Analysis, Design, Planning, Implementation, Test, Review, Deliver, and Documentation
- only Requirement, Analysis, Design, Planning, Implementation, and a limited validation placeholder are backed by current runtime state
- the drawer can show either a workflow snapshot, a workflow log, or a solution document

The MudBlazor provider setup is currently centralized in `App.razor` with `MudThemeProvider`, `MudDialogProvider`, and `MudSnackbarProvider`. `MainLayout.razor` is now only a simple body container, so the earlier provider-duplication issue is no longer present in the current shell.

### Documentation access

- workflow handlers read solution knowledge from target-relative paths such as `AI/solutions/<target-storage-code>/context/overview.md`
- document browsing in the cockpit still goes through `SolutionDocumentsController`, which resolves the first target for a solution instead of the selected target
- that means stable docs are physically target-rooted, but the browsing endpoint still behaves like a solution-level shortcut

### Setup architecture

`SetupSolutionHandler` now performs real setup persistence before filesystem bootstrap:

- validate and normalize request values
- create or update `Solution`
- create or update the single `SolutionTarget`
- create `WorkflowRun` and `AgentTaskRun`
- call `ISolutionSetupService`
- persist setup artifacts

`FileSystemSolutionSetupService` then:

- optionally copies an overlay/source target into the destination repository
- initializes Git when `.git` is missing
- adds remote `origin` when missing
- creates the `.sln` file when missing
- creates baseline target-solution docs when missing

### Agent execution architecture

The current workflow agents run through Microsoft Agent Framework wrappers over Ollama.

- framework prompt text and output schema are loaded from `AI/framework/agents/...`
- workflow metadata and profile rules are loaded from `AI/framework/workflows/...` and `AI/framework/profiles/...`
- each agent class adds hardcoded orchestration rules and a hardcoded prompt frame in C#
- the runtime then supplies structured request context, repository file lists, repository documentation file lists, solution knowledge documents, snapshot data, search hits, and sample files

`FileAwareAgentRunner` is the active execution loop:

- only `read_file` tool requests are supported
- file access is bounded to advertised relative paths
- file reads are limited to 20,000 characters per file
- the loop is capped at 12 tool calls

This means the current workflow agents are evidence-gathering, read-only agents. They can inspect files and produce structured JSON, but they cannot modify repository files.

## Key Constraints

- runtime scoping is target-based, but document browsing is still solution-based / first-target-resolved
- the current setup/update flow is still effectively one-target-per-solution
- planning and implementation handlers optionally read `design/latest-design.md` and `delivery/latest-plan.md`, but setup does not scaffold those files
- test/review/deliver are represented in framework config and cockpit placeholders, not in the current application runtime
- the implementation workflow name overstates current behavior: it records implementation intent/results but does not apply code changes to the target repository

## Notes

- the architecture is a modular monolith, not a distributed workflow system
- direct code changes have recently moved faster than the managed docs; this document is meant to track the codebase as it is now, not the ideal final pipeline
