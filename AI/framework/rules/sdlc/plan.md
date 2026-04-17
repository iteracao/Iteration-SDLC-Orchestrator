# SDLC Workflow: Plan

## Purpose
Break the design into executable backlog items.

## Core Principle
Turn design into **small, ordered, testable work units**.

---

## MUST DO

- Create backlog items
- Define execution order
- Define dependencies between items
- Keep items small and independent
- Ensure each item is testable

---

## MUST NOT DO

- ❌ Do NOT redesign the solution
- ❌ Do NOT write code
- ❌ Do NOT merge multiple concerns into one task

---

## INPUT USAGE RULES

- Use DesignReport as primary input
- Validate feasibility against source code

---

## OUTPUT CONTRACT

Return structured JSON:

{
  "backlogItems": [
    {
      "title": string,
      "description": string,
      "dependencies": string[]
    }
  ]
}

---

## QUALITY RULES

- Prefer more small tasks over few large ones
- Ensure logical execution order
- Avoid hidden dependencies