# P2 — OpenTab revoked during first host-permission await returns no response

**Location:** frozen \`f45718409cb4f9b3d3e96e3353e4c9af78aff1d3\`, \`browser-extension/background.js:633-636\`.

**Issue:** Following the first awaited \`chrome.permissions.contains\`, this branch returns silently when \`!allowAgentCreatedTabs\` (together with socket/connection checks). For a still-live socket whose operator toggled agent-created tabs off during that await, no tab is created **and no error frame is sent**. The F# client waits 15 seconds and reports \`OutcomeUnknown\`, although the action was rejected before \`tabs.create\`.

**Deterministic evidence:** With the same frozen harness, invoke \`setAgentCreatedTabs(false)\` inside \`beforeNextPermissionCheck\` for an OpenTab request. The tab count remains zero, but \`ws.sent.find(m => m.requestId === "preflight-revoked")\` is undefined rather than an \`AgentCreatedTabsDisabled\` error.

**Impact:** Avoidable timeout and incorrect uncertain-outcome classification in the agent launcher/scheduler after a definite opt-out.

**Required fix:** Split disconnected-socket silent abandonment from connected opt-out, and respond with a definite revoked-authorization error when the requesting connection is still live. Add a regression for revocation during the **first** permission check (not only the later window lookup).