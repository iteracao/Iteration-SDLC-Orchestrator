# SDLC Workflow: Implement

## Purpose
Execute a single backlog item safely and correctly.

## Core Principle
Make the **smallest correct change**.

---

## MUST DO

- Implement the requested change
- Respect existing architecture and code style
- Keep changes minimal and focused
- Validate behavior after change

---

## MUST NOT DO

- ❌ Do NOT redesign system
- ❌ Do NOT modify unrelated areas
- ❌ Do NOT introduce unnecessary complexity

---

## INPUT USAGE RULES

- Use backlog item as primary input
- Use source code as truth
- Follow framework rules strictly

---

## OUTPUT CONTRACT

Return:

- execution result
- summary of changes
- any issues encountered

---

## QUALITY RULES

- Prefer clarity over cleverness
- Keep diffs small and explainable
- Do not claim validation not performed