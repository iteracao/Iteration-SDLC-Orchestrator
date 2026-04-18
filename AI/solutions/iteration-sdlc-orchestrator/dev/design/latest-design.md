# Latest Design

## Summary

The current design direction is a target-scoped SDLC orchestrator where stable target docs, framework config, and runtime workflow artifacts remain separate but connected.

## Confirmed Direction

- keep `Solution` as the logical grouping and `SolutionTarget` as the runtime/repository boundary
- keep requirement-first intake and plan-generated backlog as the normal workflow path
- keep background workflow execution, persisted reports, and per-run workflow logs
- keep framework-defined prompts/schemas/rules, but continue layering runtime context and orchestration rules in code until prompt assembly is formalized further
- keep the cockpit centered on stage cards and per-run inspection instead of treating workflow artifacts as hidden internals

## Current Implementation Notes

- runtime APIs and cockpit refresh already follow target scope
- later workflows read stable target docs such as `analysis/latest-analysis.md`, this file, and `delivery/latest-plan.md` when they exist
- the current agent runtime is still read-only, so implementation does not yet satisfy the intended write/apply part of the design

## Open Design Gaps

- align document browsing with the selected target
- support more than one target per solution in setup and edit flows
- decide whether validation should be one workflow or split across test and review
- formalize how stable target docs are updated from workflow reports without turning them into raw artifact dumps
