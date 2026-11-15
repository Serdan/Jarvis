# P1 — OpenTab re-resolves a mutable window alias after selection

**Frozen source:** `ef1756a99c12392febacc6810fce2dc1b030343d`

**Evidence:** `Client/Modules/BrowserBridge.fs:311–321` resolves a specific ready `window` from a specific connection, but lines 332–340 send the original caller-provided `windowRegistration` string (which may be an alias) rather than the selected opaque `windowRegistration` and `registrationGeneration`. `browser-extension/background.js:606` then independently calls `validWindow(message.windowRegistration)` to resolve the alias at execution time; the extension's `registerWindow` at lines 676–693 permits an alias to be renamed or reassigned. Its later check at line 613 validates the newly resolved window, not the one the bridge selected.

**Failure sequence:** Bridge selects ready window A via alias `agents`. Before the extension processes the open request, A's alias is changed and a different authorized window B is assigned `agents`. The extension resolves the mutable alias to B, creates the tab there, and reports success. The action has silently migrated to another registered window, violating the task's one-selected-window identity requirement even though it stayed on the same authenticated connection. The bridge does not pass or verify a window generation to catch this.

**Required change:** After resolving the window, send its opaque registration ID and expected generation (not the input alias). Validate the generation and readiness in the extension immediately before `chrome.tabs.create`, comparing the exact selected registration throughout. Prefer an explicit fail-closed identity/generation mismatch to silently retargeting. Add a deterministic test that changes window alias/registration generation between bridge selection and extension execution and proves the request cannot open in a different window.

**Impact:** An agent-created tab can be opened in the wrong browser window during registry changes.