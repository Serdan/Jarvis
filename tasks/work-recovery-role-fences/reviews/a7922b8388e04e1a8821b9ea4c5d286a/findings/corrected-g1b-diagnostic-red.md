# Independent Reviewer 2 findings — corrected G1b diagnostic RED

**Disposition: Accepted for the bounded TEST-ONLY DIAGNOSTIC RED contract; production and host acceptance remain HOLD.**

## Frozen source and scope
- Claimed review `a7922b8388e04e1a8821b9ea4c5d286a`; exact source HEAD `13c0d29c994f66de67d07418e5748bb77590a114`; base `51b67bf1d5ba7e16512b915f8004456440b3489c`. The registered isolated worktree was clean at exact HEAD.
- Independently checked `git diff --name-status base HEAD`: only `Server.Tests/Server.Tests.fsproj` (one test include), new `Server.Tests/WorkRecoveryRestoreBootFenceRedFirstTests.fs`, and new `docs/testing/work-recovery-g1b-red-first-bootfence-2026-11-17.md`. No production source changes. `git diff --check` clean.
- Compared rejected frozen `dd2995580e0b2361a3e811a520810dabc5eaa96c` to this commit: the correction changes only the test and evidence document, not the fsproj or original B/F1 implementation.

## Prior blocking findings resolved, within diagnostic scope
1. **No false Recovery acceptance from unrelated B failure.** Previous `assertRecoveryDenied` accepted `Error _`. New `rawBProbe` explicitly tags successful raw B writes vs ordinary `WorkSqliteFailure` without Recovery proof; `assertRawBDiagnosticRed` always emits a **diagnostic failure** for either branch, rather than treating arbitrary validation/CAS/capacity/storage failure as a real Recovery denial. This helper intentionally has *no future GREEN path*.
2. **Adversarial negative control.** `negative_control_stale_cas_rejection_cannot_masquerade_as_recovery` deliberately changes expected guard versions and asserts the specifically typed `WorkSqliteFailure.StaleVersion` with unchanged sequence. It demonstrates that low-level rejection is not authority-lock evidence. The original blanket `Error _` test would have mistakenly passed.
3. **Host-boundary honesty.** `Server/WorkSqliteAuthority.fs` lines 10–12 explicitly define B as internal substrate, with host/domain authorization preceding `Commit`. Four test names, helper comments, and evidence now correctly say raw-B *diagnostic probes*, not authenticated pre-serve admission contracts. The evidence explicitly prohibits promoting these failures into future GREEN acceptance/merge. The exact future needed boundary is a persisted, independently witnessed RecoveryRequired (or reviewed equivalent) lock plus authenticated host/Scheduler/tool denial causally derived from that lock.

## Independently authenticated receipts
- Local .NET SDK is `11.0.100`. Independently hashed original 5-case historical TRX at `522d77d52d28f692ef26e554a20e5202e3b1f68b1c88af71fe8cbd1ad696732f`; the corrected 6-case TRX hashes `3dbd954fc330da3b0d2cfa64551fae6969ae3fb22a50c7c75b65dc1db659de1d`. Both match evidence document.
- Independently parsed corrected TRX counters: **6 total/executed; 2 passed; 4 failed; 0 errors/aborted/notExecuted**. The four failures have the intended `DIAGNOSTIC RED G1b (raw B only): internal transaction accepted` messages, not compilation, fixture, or miscellaneous failures. Passing tests are the typed stale-CAS negative control and ordinary intact SQLite restart. This validates *recorded focused Release evidence*, not a new independent test run or full-suite pass.

## Explicit exclusions — still HOLD
These tests neither authenticate a full-account B/F1 backup and remote original high-water witness nor exercise same-name cross-device identities, real claim/launch/permission routes, a persisted quarantine surviving host restart, or proof-based progressive release. B provenance, F1 full-domain inverse, and G1b host implementation require separately reviewed/integrated source and positive end-to-end tests. Do **not** merge four intentionally failing diagnostics into main, grant G1b implementation GO, enable unattended Scheduler, deploy/restart live host, or infer production Recovery/ABI/security acceptance. No production change is approved by this verdict.
