# P1 — Manual zero-dependency Challenge accepts orphan current-generation reverse index

Frozen source: `8ed32fd8c9f8be19afda677cc3a28bba4f4e835e`, `scripts/jarvis-phase2-snapshot-adapter.mjs` lines ~285–315.

**Reproduction:** In the existing disposable bare-Git account/two-project fixture, set the alpha attempt (32 a's) at generation 4 to `Blocked` and its canonical wait to `status:"Waiting", challengeIds:[], ownerAgentId:"Worker 1"`. Insert only `challenge-waits/by-challenge/<32 c's>/alpha/<32 a's>/4.json`, schema 2, matching task/attempt/owner/generation; no `by-owner` index. `captureCanonicalSnapshot` returns `completeness.challenges === true` without an index diagnostic. An independent negative-space assertion requiring false failed (`true !== false`), as part of adversarial job `3ed6beea671f493295d9dc56d57ee8f9`, OS exit 1.

**Cause:** For empty dependencies the adapter checks only the current `by-owner` path; its final scan of `by-challenge` records only verifies schemaVersion, not the relationship to the canonical wait. A manual hold must have no matching current-generation reverse index in either namespace.

**Required:** Reconcile both reverse-index namespaces against canonical wait identity and generation; reject unmatched current-generation locators, preserve genuine historical older generations and N→N+1 behavior. Add this adverse test. Account capacity remains unverified and runtime default-off, so this is false completeness, not evidence of a live launch.