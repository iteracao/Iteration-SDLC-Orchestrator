# Open Questions

## Open

- Should setup automatically trigger a dedicated solution knowledge bootstrap workflow after scaffolding?
- Which existing repository `.md` files should always be loaded as authoritative onboarding inputs for an existing solution?
- What is the exact strict output schema for `analyze-request` so it cannot drift into design?
- How should generated documentation updates be reviewed, applied, and audited over time?
- Should workflow logs remain file-based only or also become queryable structured DB artifacts later?
- What is the final cockpit refresh strategy for delta-only updates without page flicker?

## Resolved

- Long-running workflows should not execute synchronously in the HTTP request path.
- Failed workflow runs must surface their error state and failure reason in the cockpit.
- Workflow logs should be available per workflow run from the cockpit.
