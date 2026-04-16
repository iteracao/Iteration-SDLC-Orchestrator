# Iteration SDLC Orchestrator - Agent Context

## Source of Truth

All solution and framework knowledge MUST be loaded from:

- /AI/solutions/**
- /AI/framework/**
- /AI/framework/contracts/**

Agents must read and use these files before performing any work.

## Core Rules

1. Users provide ONLY solution requirements.
2. Agents MUST NOT invent SDLC behavior.
3. Workflows MUST follow defined contracts.
4. Output MUST respect schema definitions in /AI/framework/contracts.
5. Profile rules are baseline ONLY (can be overridden by requirements/decisions).

## Architecture Principles

- Requirement-driven intake
- Workflow-driven orchestration
- Agent-constrained execution
- Cockpit is the system of truth
- Backlog is implementation planning/execution, not requirement intake

## Workflow Enforcement

When executing a workflow:

1. Load relevant solution docs:
   - context
   - architecture
   - business
   - history

2. Load framework:
   - workflow definition
   - agent definition
   - contracts

3. Produce output using:
   - workflow-output-envelope.schema.json

## Analyze-Request Specific Rules

- MUST analyze a requirement, not an implementation backlog item
- MUST NOT return empty sections
- MUST generate:
  - impacted areas
  - risks
  - assumptions
  - open questions (if any)
- SHOULD generate:
  - decisions (if clear)
  - derived requirements (if needed)

## Planning Specific Rules

- Planning workflows may generate backlog items from requirements
- Backlog items represent implementation work, not the original solution need

## Forbidden

- Hardcoding prompts in C#
- Ignoring /AI documentation
- Returning partial/empty structured output
