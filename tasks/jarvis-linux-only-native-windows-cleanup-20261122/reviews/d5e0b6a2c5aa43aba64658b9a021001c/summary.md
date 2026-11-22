# Reviewer 3 — W26 Linux/WSL2 retirement inventory

**Verdict: Accepted for inventory/custody/supersession proposal ONLY.**

**Frozen source:** `6e0f099f2c4a0edd49fa8e62a9698e6e674db941`; exact parent/main baseline `dcac884fc061869572b12a03befc12f11c009f2f`; review `d5e0b6a2c5aa43aba64658b9a021001c`.

## Independent evidence
- `git show --stat`, `git diff --name-status`, `git rev-list --parents`: precisely one added 85-line Markdown file, `docs/platform/linux-wsl2-client-retirement-inventory-2026-11-22.md`. **Zero production/test/script/CI/bridge changes**. Read-only review, no build or test acceptance.
- Baseline `git grep` for Windows APIs/RIDs across Client/Client.Tests/Common/Server/scripts returned 34 OS-sensitive paths; proposed grouped exact-path table covers observed OS-sensitive client, tests, scripts, security journals, server exception and bridge paths. Tracked scripts listing contains no `.github` workflow. Proposed leases separate Worker24 settings, Worker25 protocol/tool, Worker6 mutex, Automation/Integrator, SelfUpdate, and extension.
- Baseline `scripts/publish-client-downloads.cs` includes Linux, Windows and macOS RIDs; `scripts/build.cs` exposes generic RID arguments; `docs/ClientDownloads.md` lists all artifacts. Proposal correctly restricts *client* RID publication without accidentally restricting server RID handling, and calls for unsupported-RID, no-partial-publish, checksum and stale-output negatives.
- Browser claims verified directly: `browser-extension/background.js:305` uses `ws://127.0.0.1`; `manifest.json:19` includes loopback permission; `Client/Modules/BrowserBridge.fs:152-180` restricts `chrome-extension://` Origin and authenticates nonce/HMAC, `:284` binds 127.0.0.1; `Client/Program.fs:193` reports browser listener. Browser-window naming is not Windows OS support. Preservation and no security relaxation are explicit. Real **Windows Chrome -> WSL2 handshake is NOT RUN** and appropriately not claimed.
- Canonical ledger independently confirms W6 `bfde56bcc79cedf923d8eaff7ea66cd0af488f23` review `0f4376c3a15d4a2282ecb2de61320bcb` **Accepted for Linux provisional only; Windows HOLD**; W6 Windows-specific harness `775e85dc...` review `2f5ab143...` **ChangesRequested**; WQ11 assignment **Blocked** with historical Accepted/CR pieces and no full Windows suite acceptance. Windows Git profiling task remains Open. Separate arbitrary executable-name Linux SelfUpdate task remains Open. Proposed version-checked supersession affects **only obsolete Windows-native obligations**, preserves full task/review/receipt history, and defers mutation to Lead/Operator.

## Scope guard and observations
- This approval **does not** accept any cleanup implementation, packaging edits, releases, runtime changes, live WSL or extension validation, builds, tests, or W6/WQ11 cancellation. Each implementation phase needs a separate lease, frozen source, negatives, tests and independent review.
- `git diff --check` reports four trailing double-space Markdown hard-line-breaks on doc lines 3–6 (exit 2). This is a nonfunctional formatting warning; suggest cleanup on next docs touch, not a blocking requirement.
- The document's packaging lease marked PENDING reflects its earlier timestamp; the canonical task spec was subsequently updated with a narrow phase-1 packaging lease at 12:45 CET, subject to Integrator collision check. **Current canonical lease is authoritative**, not this static document.
- Candidate `task-4bcca...` and possibly changed task states must be resolved to full canonical identifiers and versions before any actual supersession; document already makes this limitation explicit.

**Rationale:** Satisfies bounded inventory, owner/custody map, preservation of Windows Chrome to Linux/WSL localhost integration and security, and controlled supersession *proposal* without claiming native Windows retirement or test green. No in-scope correctness blocker found.
