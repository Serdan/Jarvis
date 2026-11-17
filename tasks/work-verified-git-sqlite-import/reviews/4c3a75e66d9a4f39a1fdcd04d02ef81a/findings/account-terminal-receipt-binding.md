# P1 — Account release proof does not bind the exact launch terminal receipt

**Frozen source:** `76944ed50389b73b93ee307063049e8e80f6a368`
**Paths:** `Server/WorkLegacyImportPreflight.fs:350-365,397-419`; existing positive fixture `Server.Tests/WorkLegacyImportPreflightTests.fs:355-388`.

When an account token is marked `Released`, `inspect` checks that the project launch is in `LaunchState.TerminalPendingRelease _` but **ignores the receipt inside that state** (lines 399-404). It then accepts a `WorkLegacyImportTerminal` with a `ReceiptId` matching the **account token only** (lines 410-417). Thus the project launch can say terminal receipt `B` while the account release and auxiliary terminal record say receipt `A`, and the preflight will still report released global capacity.

**Concrete counterexample:** Take the successful `c0_project_terminal_provenance_preserves_capacity_release_without_active_claim` fixture. Change only `launch.State` from `TerminalPendingRelease "terminal-receipt"` to `TerminalPendingRelease "different-receipt"`, keeping the released account token and terminal evidence intact. The current checks still return `Ok` and `ReservedCapacity=0`. This is contradictory cross-ledger release evidence; a verified migration must fail closed rather than free account capacity on mismatched proof.

Additionally, `WorkLegacyImportTerminal.OperationId` is checked only for membership in the generic project operations list; the provided fixture's operation has `Kind="verified-ledger-operation"`, not a proven terminal operation. The verification boundary should bind the terminal operation's kind/result/receipt (or carry a separately verified exact terminal-result object), not infer terminal authority from an arbitrary operation ID and synthetic flags.

Require the **same receipt** across project launch terminal state, independently verified project terminal record and account token release, together with the matching project terminal operation's exact effect/result provenance. Add mismatch and unrelated-operation negative tests. Keep unresolved release tokens reserved, never silently release them.