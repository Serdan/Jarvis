# Independent review — Work-first authenticated queue/claim facade

**Task:** `work-native-claim-tool-facade`  
**Review:** `7bd029a674964575976a7e90b618468b`  
**Frozen commit:** `2ade4efef0c9bae199d65dada75dbc306d7a65cc`  
**Verdict:** Changes requested (one P2 correctness issue)

## Checks performed

- Inspected the frozen diff against `944bca7fa38cb5e973b04ce7829966cca351202b` (10 files; 729 lines added, 2 removed), including WQ02/WQ09 contracts, MCP/SignalR routing, permission policy, default-off mutations, tests and rollout documentation.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueToolFacadeTests --no-restore --verbosity minimal`: **5 passed, 0 failed, 0 skipped**.
- `dotnet build Jarvis.slnx -c Release --no-restore --verbosity minimal`: **succeeded, 0 errors** (11 warnings in unrelated existing files).
- Confirmed no live Work mutation: all three write actions return `RejectedMissingBinding` without invoking the ledger writer, preserve supplied UUID, and reject stale project versions.
- Authenticated server actor is derived from the OAuth user, not the caller's legacy Agent identifier. WQ04 role provenance is observational, never an admission permit.

## Finding requiring correction

**P2 — Claimed recovery-held Work is misclassified as Active.** See `findings/claimed-recovery-hold-classified-active.md`.

The facade short-circuits `WorkState.Claimed` to `Active` whenever the launch primary ID matches, even if WQ09 classifies the same Work as `Blocked / RecoveryHold` due to integration/source/stop gate. `ListWork(category="Blocked")` omits recovery-held claimed work and `Active` can include stop/terminal-uncertain launches. Honor WQ09 recovery classifications and add negative-space tests.

## Integration boundary

The default-off seam avoids unsafe writes, but the queue projection is not coherent until blocked/active categorization is corrected. Keep the frozen SHA unintegrated pending correction and independent re-review.