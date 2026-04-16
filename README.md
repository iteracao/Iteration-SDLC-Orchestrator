# Iteration SDLC Orchestrator

Iteration is a solution-centric SDLC orchestration platform where users provide **solution requirements**, agents operate inside fixed workflow boundaries, and the cockpit tracks requirements, workflow runs, questions, decisions, backlog work, and generated knowledge.

## Current direction

The orchestrator now follows these modeling rules:

- **Requirement** = the canonical solution need or constraint submitted by a user or stakeholder
- **Backlog item** = an implementation work item or iteration derived from planning one or more requirements
- **Workflow run** = the execution record of a workflow acting on a requirement, a backlog item, or the solution itself

In other words:

`Requirement -> Analysis -> Planning -> Backlog items -> Implementation/Test/Delivery`

## Current scope

- Setup a solution and bootstrap its AI workspace
- Create first-class requirements
- Run `analyze-request` from a requirement
- Persist workflow runs, reports, open questions, and decisions
- Keep backlog available as a downstream implementation work model
- View core data through API and cockpit pages

## Current implementation state

- Domain, application, infrastructure, solution bridge, API, cockpit, and AI framework starter files are included
- Microsoft Agent Framework + Ollama analyst integration is wired in
- Config loading works from the local `AI/framework` folder
- Solution bridge is read-only
- SQLite is used for persistence
- Requirement intake is the canonical analysis entry point
- Backlog is reserved for planned implementation work and should no longer be used as requirement intake

## Recommended next milestone

See [ROADMAP.md](ROADMAP.md).

## Running locally

1. Restore packages:
   - `dotnet restore`
2. Run the API:
   - `dotnet run --project src/Iteration.Orchestrator.Api`
3. Run the cockpit:
   - `dotnet run --project src/Iteration.Orchestrator.Cockpit`

## Notes

- The included solution is a real working starter, not a placeholder skeleton.
- The AI workspace under `/AI` is part of the source of truth for workflow and agent behavior.
- Backlog still needs a dedicated planning workflow so it can be generated from requirements instead of being created ad hoc.
