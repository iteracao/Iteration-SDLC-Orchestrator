# SDLC Workflow: Design

## Purpose
Define how the requirement will be solved.

## Core Principle
Translate analysis into a clear, structured solution design.

---

## MUST DO

- Propose a solution approach
- Identify impacted components/modules
- Define architecture changes
- Define data flow and interactions
- Define API/UI changes if applicable
- Respect existing architecture and boundaries

---

## MUST NOT DO

- ❌ Do NOT write code
- ❌ Do NOT create backlog/tasks
- ❌ Do NOT re-analyze the requirement deeply (assume analysis is valid)

---

## INPUT USAGE RULES

- Use AnalysisReport as primary input
- Validate against source code when needed
- Respect framework and architecture rules

---

## OUTPUT CONTRACT

Return structured JSON:

{
  "summary": string,
  "componentsImpacted": string[],
  "designDecisions": string[],
  "constraints": string[],
  "risks": string[],
  "recommendedNextWorkflowCodes": string[]
}

---

## QUALITY RULES

- Keep design simple and aligned with current system
- Avoid speculative architecture changes
- Be explicit about trade-offs