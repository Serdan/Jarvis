# Independent review — ChangesRequested (P0 Worker24 client settings)

**Frozen commit:** `55fdbcb93681abaa07d94cc62da2d2fef6ce59b3` (main base `dcac884fc061869572b12a03befc12f11c009f2f`). **Reviewer:** Reviewer 1. **Scope:** exactly seven changed files (ClientConfig.fs, ConsoleTui.fs, Program.fs; ClientConfigTests.fs; isolated SettingsHarness Program/fsproj; scoped docs). Clean source branch verified at frozen HEAD. No shared production fsproj, Worker25 protocol paths, main, live WSL or deployment modified.

## Positive evidence, preserved

- Independent `.NET 11 Release --no-build` run of all `ClientConfigTests` on frozen branch: **23/23 PASS**, terminal exit 0, `/tmp/jarvis-review1-w24-settings-focused.trx`.
- Full unfiltered Client TRX `/tmp/jarvis-w24-client-full/w24-client-full-unfiltered.trx` parsed independently: **1360 passed, 0 failed** (TRX total 1362, executed 1360; existing skipped/not-run boundaries must not be inflated). This validates broad non-regression, not omitted requirements.
- Real Linux PTY TUI edit/Save/reopen for machine/workspace and all eight settings; isolated CLI config set/show/unset; disposable SignalR hub demonstrating manual `Program.register` with two names; in-test runtime A/B project enumeration; cross-process-revision CAS and malformed/symlink negative tests.
- Reviewed dirty separate mutation worktree: one-line `elif false && revision original <> snapshot.Revision` guard removal. Inspected its negative TRX `/tmp/jarvis-w24-settings-mutant/w24-cas-guard-mutant.trx`: **1 failed of 1**, `simultaneousSnapshotsNeverBothCommitAndOverwriteEachOther`. This is meaningful CAS mutant evidence, not production RED.
- Original RED evidence is only for saving nonexistent workspace roots, **not** the operator's complete workspace/no-persistence behavior or machine-name case. Do not mislabel it.

## Findings / acceptance boundary

1. **F1 P0, `findings/p0-observed-end-to-end.md`:** Both operator-observed bugs still lack independently captured original RED-first real-client behavioral proof; the new name SignalR test calls `Program.register` directly and cannot validate automatic connected-loop registry updates or device view, and the workspace test builds a `Runtime` in the same test process instead of restarting a real client and observing its project operations.
2. **F2 P0, `findings/p0-settings-consumers.md`:** Whole-settings P0 runtime action matrix remains incomplete by author's own matrix: trust-gated actions, actual subprocess env, import limit actions, bridge disable/rebind and fake-clock NativeHealth checks; cancel/mouse/default/negative boundaries similarly incomplete.
3. **F3 P1, `findings/p1-trust-confirmation.md`:** The TUI silently permits trust `Some "none"` -> `None` (effective `partial`) on `u` then `s` without the required local privilege-increase confirmation. CLI higher-trust authorization semantics are also unverified. This is concrete source-observed behavior; remote boundary is stricter.
4. **F4 P1, `findings/p1-shared-boundary.md`:** Shared durable low-level save is valuable, but no complete typed application transaction with authorization provenance, effective source, old/new values, per-caller applied/restart/reconnect flags and typed failure results exists yet. TUI and CLI status remain adapter-specific; integration with Worker25 must be explicitly coordinated.

**Decision: ChangesRequested.** This is a scope/evidence and concrete trust-confirmation verdict on the immutable source, not rejection of the genuine fixes/tests. Correct only original-task requirements with disjoint ownership and return a new frozen independently reviewable source. Do not adopt any new scope or prescribe broader refactors absent separate adjudication; no live WSL config/client actions, main push/deployment or schedules.
