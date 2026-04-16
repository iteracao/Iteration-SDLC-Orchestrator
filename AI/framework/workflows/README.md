# SDLC Workflows V1

This package contains one workflow definition per file for the first end-to-end SDLC backbone:

1. setup-solution
2. analyze-request
3. design-solution-change
4. plan-implementation
5. implement-solution-change
6. test-solution-change
7. review-implementation
8. deliver-solution-change
9. update-solution-history

Each workflow file defines:
- purpose
- owning agent
- required inputs
- knowledge documents read
- produced outputs
- knowledge documents updated
- next workflows

## Workflow root model

The intended ownership model is:

- `setup-solution` acts on the solution
- `analyze-request` acts on a requirement
- design and planning workflows act on a requirement
- implementation, test, review, and delivery workflows act on backlog items produced by planning

This means backlog is downstream from requirement analysis and planning, not the original intake model.

These files are intended as a clear, fixed, first-pass framework definition that you can evolve inside the SDLC orchestrator.
