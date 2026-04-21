# SDLC Workflow: Analyze

## Purpose
Understand the requirement and the current system. Produce an evidence-based analysis report.

## Core Principle
This phase is about **understanding**, not solving.

---

## MUST DO

- Analyze the requirement in the context of the current solution
- Identify impacted areas in the system
- Identify risks and constraints
- Identify assumptions
- Identify missing information and evidence gaps
- Use repository source code and repository documents as primary evidence
- Use solution documents as supporting knowledge
- Clearly separate:
  - facts from evidence
  - assumptions
  - unknowns

---

## MUST NOT DO

- Do NOT propose a solution design
- Do NOT design UI, API, components, or data contracts
- Do NOT plan backlog slices or implementation tasks
- Do NOT suggest implementation steps

---

## EXECUTION SHAPE

- This workflow is prompt-driven, not JSON-contract-driven
- Prompt 1 delivers SDLC, workflow, agent, and domain context
- Prompt 2 delivers the real repository structure only
- Prompt 3 directs targeted tool-based repository investigation and requests the final Markdown report
- All prompts and responses belong in workflow logs
- Only the final Markdown analysis report is persisted as an artifact file

---

## INPUT USAGE RULES

- Repository files = source of truth for current implementation behavior
- Solution docs = curated knowledge that may lag behind the code
- Framework rules = constraints, not implementation facts
- Repository structure = navigation aid, not proof by itself

If solution docs are empty or outdated:
-> rely on source code and explicitly state the gap

---

## OUTPUT EXPECTATION

- Return a plain Markdown analysis report in the final prompt
- The final Markdown should be suitable to save directly as `analysis-report.md`
- Do not require JSON fields, response envelopes, or schema validation

---

## QUALITY RULES

- Be concise but specific
- Ground every claim in evidence or mark it as an assumption
- Prefer "unknown" over guessing
- Be explicit when evidence is incomplete or conflicting
