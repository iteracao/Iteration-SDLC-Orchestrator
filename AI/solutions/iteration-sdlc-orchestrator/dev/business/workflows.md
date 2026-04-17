# Business Workflows

## Purpose

This document describes the main SDLC workflows currently present in the repository and the intended business flow between them.

## Main Workflows

### 1. Setup Solution

Source: `AI/framework/workflows/setup-solution/workflow.yaml`

Purpose:
- initialize or validate a solution workspace
- prepare the local repository folder
- seed the SDLC knowledge structure under `AI/solutions/<solutionCode>`
- optionally configure Git metadata and remote origin

Current implementation notes:
- seeds baseline markdown files if missing
- preserves existing documents rather than overwriting blindly
- currently focuses on scaffolding, not deep documentation bootstrap

### 2. Analyze Request

Source: `AI/framework/workflows/analyze-request/workflow.yaml`

Purpose:
- analyze a requirement against solution knowledge, repository evidence, and framework rules
- produce an analysis report and downstream recommendations

Expected outputs:
- analysis report
- impacted areas
- risks
- assumptions
- open questions
- recommended next workflows

Current implementation notes:
- runs in background through the workflow queue/hosted service
- writes workflow logs per run
- quality still depends heavily on prompt/output contract discipline and quality of solution knowledge inputs

### 3. Design Solution Change

Source: `AI/framework/workflows/design-solution-change/workflow.yaml`

Purpose:
- transform an analyzed request into a proposed design aligned with architecture, business rules, and known decisions

Expected outputs:
- design report
- contract/data/UI/integration impact summaries
- design-level open questions

### 4. Plan Implementation

Purpose:
- convert approved design intent into implementation slices/backlog-oriented work
- produce planning outputs and generated backlog items

Current repository direction:
- backlog is intended to be derived from planning rather than created as the original intake mechanism

### 5. Implement Solution Change

Purpose:
- execute implementation work against backlog/requirement context using the configured implementation agent

## Exceptions

- The current setup workflow does not yet perform a proper onboarding/bootstrap pass that reads existing repository `.md` files and selected source files to populate the managed solution knowledge.
- The current analyze workflow can drift into design-like outputs if the prompt contract is weak or the solution knowledge is mostly placeholders.
- Cockpit polling currently refreshes more state than necessary; targeted card updates are still a refinement item.

## Open Clarifications

- Should setup automatically chain into a dedicated `bootstrap-solution-knowledge` workflow after scaffolding?
- Which workflows should be approval-gated before the next phase can start?
- How should documentation update workflows be triggered and audited after each phase?
