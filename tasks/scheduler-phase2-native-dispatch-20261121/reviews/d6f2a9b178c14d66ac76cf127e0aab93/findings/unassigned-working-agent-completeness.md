# P1 — Working agent with no canonical assignment is incorrectly marked complete

Frozen source: `8ed32fd8c9f8be19afda677cc3a28bba4f4e835e`, `scripts/jarvis-phase2-snapshot-adapter.mjs` lines ~115–149.

**Reproduction:** In a valid disposable canonical Git fixture set `agents/V29ya2VyIDE.json` (Worker 1) to `currentAssignment:null`, `execution:"Working"`, `unassignedDisposition:null`, `activeSessionId:null`. The attempt and task still exist. The adapter returns `agent.state:"Working"`, `currentAssignment:null`, `verified:true`, `completeness.agents === true`. An independent assertion expecting incomplete failed (`true !== false`), adverse job `3ed6beea671f493295d9dc56d57ee8f9`, OS exit 1.

**Cause:** The contradiction guard rejects unassigned Working only when unassignedDisposition is non-null. Null disposition incorrectly bypasses validation despite no task/attempt/generation ownership binding.

**Required:** Mark a Working agent without a canonical current assignment incomplete (irrespective of disposition) with explicit diagnostics. Preserve valid Ready/Stopped, NeedsWork, Parked and assigned Working distinctions; add this adverse regression. There is currently no live dispatch authority.