# P1 — Composite launch does not pin the created tab's registration generation

**Frozen source:** `ef1756a99c12392febacc6810fce2dc1b030343d`

**Evidence:** `Client/Modules/BrowserBridge.fs:701–704` reads the opened tab's `registrationId` and the owning `connectionId`, but discards the returned `tab.registrationGeneration` and `agentCreated` state. The readiness poll at lines 725–733 checks only ID plus `availability=ready`; the subsequent `Execute` (lines 747–750, method lines 432–450) re-reads the *current* tab generation. In `browser-extension/background.js:716–724`, manually registering an already agent-created tab keeps its registration ID while incrementing its generation and changing `agentCreated` to `false`.

**Failure sequence:** Jarvis creates a new ChatGPT home tab and waits for readiness. Before injection, the operator manually registers that tab, converting it to a user-owned registration (generation 2). The composite launch accepts the unchanged ID as ready and injects the bootstrap into the now manually registered tab. Cleanup only notices the non-agent-created state later and refuses to close it. This violates the task's restriction to the original Jarvis-owned tab and its requested generation checks; a preflight authorization/ownership change must fail closed, not silently adopt newer registration state.

**Required change:** Pin the exact returned created-tab registration ID, generation, and `agentCreated` provenance from `OpenTab`. Verify all three during readiness and at the atomic `Execute` preflight (pass the expected generation through to `Execute` rather than acquiring a newer one), and use a pinned close/cleanup guard. Fail before injection if the generation or ownership changes. Add a regression that re-registers the created tab between Open and Execute and proves no script is sent or user tab closed.

**Impact:** Wrong ownership boundary, potential replacement/submission of text in a tab no longer controlled by the agent.