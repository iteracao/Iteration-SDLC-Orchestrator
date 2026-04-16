# Business Workflows

## Purpose

This document describes the intended SDLC workflow flow and distinguishes between the workflows defined in the framework and the subset currently executed in code.

## Main Workflows

The framework currently defines this end-to-end sequence under `AI/framework/workflows/`:

1. `setup-solution`
2. `analyze-request`
3. `design-solution-change`
4. `plan-implementation`
5. `implement-solution-change`
6. `test-solution-change`
7. `review-implementation`
8. `deliver-solution-change`
9. `update-solution-history`

### 1. Setup Solution
Purpose:
- initialize or validate a solution workspace
- ensure repository baseline
- create the SDLC documentation structure

Current implementation status:
- implemented end-to-end through API, application handler, setup service, persistence, and artifact output

### 2. Analyze Request
Purpose:
- inspect the real solution state
- produce grounded structured analysis
- identify impacted areas, risks, assumptions, and recommended next steps

Current implementation status:
- implemented end-to-end through API, application handler, solution bridge, analyst host, persistence, and artifact output

### 3. Design Solution Change
Purpose:
- transform analyzed requirement into proposed target design changes

Current implementation status:
- framework-defined only
- not yet executed in application code

### 4. Plan Implementation
Purpose:
- create an implementation plan from the approved/analyzed requirement and design

Current implementation status:
- framework-defined only

### 5. Implement Solution Change
Purpose:
- execute the planned technical change in the target solution

Current implementation status:
- framework-defined only

### 6. Test Solution Change
Purpose:
- validate the implemented change through test evidence

Current implementation status:
- framework-defined only

### 7. Review Implementation
Purpose:
- assess quality, alignment, and readiness for delivery

Current implementation status:
- framework-defined only

### 8. Deliver Solution Change
Purpose:
- package and mark the result as delivered or ready for handoff

Current implementation status:
- framework-defined only

### 9. Update Solution History
Purpose:
- keep long-term continuity documents current

Current implementation status:
- framework-defined only

## Required Operating Flow

The intended flow is not “user tells an agent to do work.”

The intended flow is:
1. user submits a solution requirement
2. orchestrator selects the appropriate workflow
3. workflow loads its contracts, rules, and required knowledge
4. configured agent performs only the work allowed by that workflow
5. workflow outputs are persisted as structured artifacts
6. documentation, questions, decisions, and history are updated
7. cockpit reflects the new current state

## Exceptions

### Early-stage implementation exception
The current codebase supports direct creation of backlog items with a selected workflow code. This is acceptable for a starter but still weaker than the intended requirement-driven intake model.

### Documentation update exception
Although framework rules say documentation should be updated after each workflow, this is not yet automated in the current codebase.

## Open Clarifications

1. Should `update-solution-history` remain a dedicated workflow, or should every workflow perform mandatory continuity updates before completion?
2. Which workflows are allowed to create new requirements versus only open questions or decisions?
3. How should blocked workflows surface missing information back into the cockpit?
4. Should workflow-to-workflow transitions be fully automatic, operator-approved, or configurable per profile?
