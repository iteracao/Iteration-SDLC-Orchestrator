# SDLC Workflows V1

This package contains one workflow definition per file for the first end-to-end SDLC backbone:

1. setup-solution
2. setup-documentation
3. analyze-request
4. design-solution-change
5. plan-implementation
6. implement-solution-change
7. test-solution-change
8. review-implementation
9. deliver-solution-change
10. update-solution-history

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
- `setup-documentation` acts on the solution target independently from requirement lifecycle
- `analyze-request` acts on a requirement
- design and planning workflows act on a requirement
- implementation, test, review, and delivery workflows act on backlog items produced by planning

This means backlog is downstream from requirement analysis and planning, not the original intake model.

These files are intended as a clear, fixed, first-pass framework definition that you can evolve inside the SDLC orchestrator.
