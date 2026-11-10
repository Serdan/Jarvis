# P2 acceptance gate — No live paired Chromium/Edge extension end-to-end verification

Frozen branch: `94c73e426a9283b7d66f21d63e7cb996a2fcbaa0`.

The task specification explicitly requires at least one **paired real Chromium/Edge extension** end-to-end test on a dynamic JS-rendered page. The agent's review summary confirms that the test is still pending explicit operator opt-in and extension setup. The existing independent 572 Client, 128 Server and 35 JavaScript tests exercise mock/loopback and handler behavior, and are successful; those tests do **not** prove real MV3 service worker/extension user-script execution, browser tab/window consent, target origin revocation, or end-to-end DOM wait/export/close behavior.

**To complete acceptance:** Under explicit operator setup/authorization, pair the extension to JarvisClient, register a permitted browser window and test origin, run created-tab/project-script hydration, selector/delay/custom event snapshot, bounded file export with integrity, and safe tab close. Exercise refusal to close a manually registered tab, and capture non-sensitive logs/results. Do not bypass user consent or deploy merely to run tests. Once recorded, submit a new frozen acceptance review.

**Severity: P2 task acceptance dependency, not a demonstrated source-code defect.** The prior server MCP OAuth P1 is fixed by this branch and independently verified.
