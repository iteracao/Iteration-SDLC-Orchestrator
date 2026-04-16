# Open Questions

## Open

### Q-001 — Requirement model evolution
Should the current `BacklogItem` evolve into a richer `Requirement` entity, or should the platform keep both with distinct responsibilities?

### Q-002 — Continuity update execution model
Should documentation, decisions, and open-question updates happen inside every workflow, or should they be centralized in a mandatory follow-up workflow?

### Q-003 — Agent domain and skill enforcement depth
How far should runtime policy enforcement go beyond current configuration loading? For example, should the orchestrator block execution when workflow, domain, and skill compatibility is incomplete or ambiguous?

### Q-004 — Cockpit source-of-truth model
Should the cockpit persist explicit documentation/knowledge entities in the database, or should it project from markdown files plus workflow artifacts?

### Q-005 — Workflow transition policy
Should movement from analysis to planning, implementation, testing, review, and delivery be automatic, manually approved, or configurable per workflow/profile?

### Q-006 — Filesystem casing convention
Should the repository standardize on `AI/` or `ai/` paths everywhere to avoid case-sensitivity issues across environments?

## Resolved

None recorded yet.
