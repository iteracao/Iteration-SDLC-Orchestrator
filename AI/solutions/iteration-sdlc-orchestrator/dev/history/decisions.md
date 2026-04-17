# Decisions

## Active Decisions

- Requirements are the canonical intake object for solution needs.
- Backlog items are downstream implementation work rather than original request intake.
- Workflow execution runs in the background through a queue and hosted service instead of blocking HTTP requests.
- SQLite is the current persistence store.
- Microsoft Agent Framework + Ollama is the active local AI integration path.
- The `AI/framework` folder is part of the source of truth for profiles, workflows, and agent behavior.
- Managed solution knowledge is stored under `AI/solutions/<solutionCode>` in the target repository.
- Per-run workflow logs are written to `workflowRunId.log` and exposed through the cockpit.

## Superseded Decisions

- Running long-lived workflow execution synchronously inside the API request path has been superseded by background execution.
- Using backlog as the primary intake object has been superseded by requirement-first modeling.
