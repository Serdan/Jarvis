# P2 acceptance gap — No paired real Chromium/Edge bridge end-to-end run

The task requires at least one live supported Chromium/Edge test through an explicitly registered browser window/tab on a generic JS-rendered fixture, demonstrating open, project-script execution, DOM wait and capture, export and permitted close. The author's own review summary says the generic JS-rendered fixture was demonstrated in Edge headless, but **no deployed extension or paired live Edge acceptance** has been done. The existing .NET and Node tests use an in-memory/mock bridge and extension-browser API stubs. They do not verify actual `chrome.userScripts.execute` semantics in a paired MV3 extension or real background-tab readiness/event waits.

**Fix/acceptance evidence:** Run one supported live Edge/Chromium with the extension enabled, opt in/register a test window and allowed local test origin, exercise selector hydration, delay, custom event, file snapshot and created-tab cleanup, and record actual logs/results without DOM secrets. This test requires explicit local user setup/authorization; do not deploy without it.

**Severity: P2 acceptance gap**, not by itself proof of a runtime bug.
