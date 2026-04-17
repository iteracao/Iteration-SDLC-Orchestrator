# Known Gaps

## Current Gaps

- Seeded solution knowledge markdown files are placeholders and are not yet automatically bootstrapped from repository truth.
- Existing repository `.md` files outside the SDLC-managed knowledge folder are not yet fully harvested into managed knowledge during onboarding.
- Analyze workflow prompts are oversized and not yet strict enough about input framing and output contract.
- Analysis outputs can drift into design/proposal content instead of staying strictly analytical.
- Cockpit polling currently refreshes too much state and can cause visible flicker.
- There are no test projects in the current solution snapshot.
- Solution bridge search/read behavior is intentionally simple and limited; it is not yet a rich code intelligence layer.

## Risks

- Empty or weak managed knowledge reduces grounding quality for every downstream workflow.
- Prompt drift can generate plausible but semantically wrong workflow outputs.
- Overly large prompts can hurt latency and model reliability, especially on local Ollama models.
- UI flicker may reduce operator confidence even when workflow state is correct underneath.
