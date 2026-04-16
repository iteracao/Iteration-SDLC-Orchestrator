# Known Gaps

## Current Gaps

### G-001 — Rich requirement management is not implemented
The current domain uses `BacklogItem` as the work intake object. This does not yet express the full requirement-centric model described by the solution vision.

### G-002 — Cockpit is still placeholder-level
The cockpit pages are starter placeholders and do not yet expose requirements, workflow traces, analysis reports, decisions, open questions, or documentation state.

### G-003 — Most framework workflows are not wired into application execution
The YAML framework defines an end-to-end SDLC flow, but the application layer currently executes only setup and analysis.

### G-004 — Automatic documentation and knowledge updates are not implemented
The setup workflow creates documentation files and the analysis workflow reads them, but workflow execution does not yet mutate business rules, decisions, architecture, or known-gap documents automatically.

### G-005 — Decisions and open questions are not first-class persisted entities
The intended operating model requires them, but they are currently tracked only as documentation content, not as structured database entities.

### G-006 — Solution bridge search is intentionally naive
`LocalFileSystemSolutionBridge.SearchFilesAsync` performs simple text search over selected file types and is sufficient for a starter, but not for deeper semantic or large-repository analysis.

### G-007 — Path casing is inconsistent
The repository stores framework and solution docs under `AI/...`, while some runtime code uses lowercase `ai/...` when constructing knowledge paths.

## Risks

- The current starter may produce the appearance of a broader SDLC platform than is actually implemented if documents do not clearly separate current state from target state.
- Case-sensitive environments may fail to find knowledge documents created under a different directory casing convention.
- Without first-class requirement, decision, and open-question entities, the cockpit cannot become the authoritative operational view you want.
- Weak contract enforcement may still allow workflows to drift toward prompt-driven behavior instead of governed execution.
