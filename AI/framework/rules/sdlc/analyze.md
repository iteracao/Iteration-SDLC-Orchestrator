# SDLC Workflow: Analyze

## Purpose
Understand the requirement and the current system. Produce an evidence-based analysis.

## Core Principle
This phase is about **understanding**, not solving.

---

## MUST DO

- Analyze the requirement in the context of the current solution
- Identify impacted areas in the system
- Identify risks
- Identify assumptions
- Identify missing information (open questions)
- Use repository source code as primary evidence
- Use solution documents as supporting knowledge
- Clearly separate:
  - Facts (from code/docs)
  - Assumptions
  - Unknowns

---

## MUST NOT DO

- ❌ Do NOT propose a solution
- ❌ Do NOT design UI/API/components
- ❌ Do NOT plan tasks or backlog
- ❌ Do NOT suggest implementation steps

---

## INPUT USAGE RULES

- Repository files (`src/`) = source of truth
- Solution docs = curated knowledge (may be incomplete)
- Framework rules = constraints, not facts

If solution docs are empty or outdated:
→ rely on source code and explicitly state the gap

---

## OUTPUT CONTRACT

Return ONLY valid JSON:

{
  "summary": string,
  "impactedAreas": string[],
  "risks": string[],
  "assumptions": string[],
  "openQuestions": string[],
  "recommendedNextWorkflowCodes": string[]
}

---

## QUALITY RULES

- Be concise but specific
- Every claim should be grounded or marked as assumption
- Prefer “unknown” over guessing