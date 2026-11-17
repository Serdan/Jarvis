# G1b stale-restore boot fence — corrected test-only diagnostic RED evidence

**Date:** 2026-11-17.
**Status:** TEST-ONLY DIAGNOSTIC RED; **not** a host-facing admission test, G1b acceptance gate, full-domain snapshot, production Recovery implementation, or merge authorization.
**Base:** clean `51b67bf1d5ba7e16512b915f8004456440b3489c`, isolated `test/work-recovery-g1b-bootfence-red-worker12-20261117`.
**Files:** `Server.Tests/WorkRecoveryRestoreBootFenceRedFirstTests.fs`, one test-project compile include, this evidence document. No production source changes.
**Design:** independently Accepted **design only** `docs/specs/work-sqlite-domain-backup-contract-2026-11-17.md` at `60cacc98152da3f66cc13f63224d32854d400c53`, review `9f386cc72d004deba5b4ad79bf7be3e2`, especially sections 3–5.
**Correction:** Reviewer 2's `0f2d740b9a87464c857fc6d2b4b80f62` ChangesRequested at frozen `dd2995580e0b2361a3e811a520810dabc5eaa96c`, decision sequence 6321, acknowledged. This revision addresses the two test-contract blockers without entering production G1b.

## Evidence boundary and original fixture

`WorkSqliteAuthority.Commit` is **raw internal B transaction substrate**. Its own source says domain reducers authenticate/authorize commands *before* calling it; it has no host/Scheduler admission boundary. A correct design could enforce Recovery *above* B while B still accepts these transactions. **Therefore four raw B probes are diagnostics only and cannot establish failure or success of a real host-facing G1b fence.** Their intentionally RED outcomes must not be promoted into future green/merge acceptance.

Current narrow F1 `WorkSqliteBackup.restoreEmpty` returns `RequiresRecovery=true` in a process-local receipt, **not** a persisted authority lock. G1a `WorkRecoverySqlite.seed` can downgrade project/stage/capacity after an approved caller supplies suitable proof, but it does not itself provide authenticated pre-serve admission. The test-only fixture deliberately does **not** call seed. It creates two project IDs (`alpha`, `beta`) sharing account `shared-account`, a Prepared launch/stage, an Unknown external effect and receipt at sequence N. It exports the narrow F1-v2-admissible flat B rows, records an additional Unknown effect at N+1 on the source, restores backup N to a disposable empty authority and opens a fresh process-level instance. Restored N correctly lacks N+1 evidence. This models a dangerous *hypothetical* pre-seeding gap, not a trusted complete-account restore witness or authenticated host route.

## Non-vacuous diagnostic contract

`rawBProbe` explicitly distinguishes `RawBAdmitted` and `RawBRejectedWithoutRecoveryProof`. All four `assertRawBDiagnosticRed` invocations intentionally fail for **both** outcomes: acceptance observes B permissiveness; any internal `WorkSqliteFailure` (validation, stale CAS/evidence, capacity, storage, operation ID or otherwise) still lacks independently observable durable Recovery-specific authority. No `Error _` or unchanged sequence can pass as Recovery denial. The helper has **no GREEN path** until a separate, genuinely authenticated Recovery-enforced boundary and persisted lock witness exist; do **not** retrofit these raw-B probes as a future acceptance test.

| Focused test | Result | Evidence boundary |
| --- | --- | --- |
| `diagnostic_raw_b_stale_restore_admission_is_not_host_gate` | Intentionally RED | Alpha's raw internal transaction accepted; N+1 effect absent in restored N; no host gate tested |
| `diagnostic_raw_b_two_project_shared_capacity_is_not_host_gate` | Intentionally RED | Beta's raw internal transaction accepted despite Alpha uncertainty; no account-wide host admission proven |
| `diagnostic_raw_b_historical_replay_is_not_new_dispatch_permission` | Intentionally RED | Old exact receipt replays without new sequence, but a new-UUID raw B write accepted; no permission/dispatch tested |
| `diagnostic_raw_b_empty_restore_write_is_not_host_gate` | Intentionally RED | Raw internal transaction accepted from empty restored N; no identity or complete-current-backup proof |
| `negative_control_stale_cas_rejection_cannot_masquerade_as_recovery` | PASS | Deliberately stale version guard returns typed `WorkSqliteFailure.StaleVersion`; sequence unchanged, classified *without* Recovery proof |
| `ordinary_intact_sqlite_restart_is_not_a_stale_restore` | PASS | Restarting same intact durable SQLite supports an ordinary guarded internal write; not a stale restore |

The stale-CAS adversarial control would have been incorrectly accepted by the old `assertRecoveryDenied` branch (`Error _ -> ()`). It now demonstrates explicitly why **an unrelated low-level rejection with zero sequence change cannot count as a pre-serve Recovery fence**. The positive intact-restart control distinguishes ordinary process restart from stale restore.

## Independent test evidence

- **Original historical RED baseline (unchanged):** frozen `dd2995580e0b2361a3e811a520810dabc5eaa96c`; focused Release 5 executed, 4 intentional RED, 1 PASS, 0 skipped/aborted/errors. TRX `Server.Tests/TestResults/work-recovery-g1b-red-20261117.trx` SHA-256 `522d77d52d28f692ef26e554a20e5202e3b1f68b1c88af71fe8cbd1ad696732f`. Its old assertion names/message and all-error acceptance were the rejected contract, not a valid host-fence proof.
- **Corrected independent run:** `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkRecoveryRestoreBootFenceRedFirstTests --logger 'trx;LogFileName=work-recovery-g1b-red-reviewfix-20261117.trx' --results-directory Server.Tests/TestResults`.
- **SDK/target:** .NET SDK 11.0.100, Release / net11.0. Test project compiled. **6 executed, 4 intentionally diagnostic RED, 2 PASS, 0 skipped, 0 aborted/error; exit 1 expected.** All four RED failures say `DIAGNOSTIC RED G1b (raw B only): internal transaction accepted`, not fixture/compile failures. The new typed stale-CAS negative control and intact restart PASS.
- **Corrected TRX:** `Server.Tests/TestResults/work-recovery-g1b-red-reviewfix-20261117.trx`; SHA-256 `3dbd954fc330da3b0d2cfa64551fae6969ae3fb22a50c7c75b65dc1db659de1d`. This is a generated *local*, untracked test artifact, not part of the frozen source review. No full-suite pass or production acceptance is claimed.
- Source-frozen review metadata must identify the revised exact commit. Keep all intentionally failing RED tests on this isolated source branch, **unmerged**.

## Required future G1b acceptance, explicitly HOLD

1. **Worker20 / B:** independently reviewed closed typed writers for all durable record kinds; trusted original repo/ref/commit and project-device identity, source/role/generation/provenance and account membership; no arbitrary body or credential/grant export. Distinct **same-name projects across devices** must be validated by authentic IDs, not simulated by reusing `alpha`/`beta`.
2. **Worker13 / F1:** reviewed exact closed, credential-safe full-account JSON snapshot and inverse, including original audit/receipt/claim/launch/Unknown effects, independent trusted Git backup root and verified complete account membership. Existing narrow `RequiresRecovery` receipt is not a durable pre-serve lock.
3. **G1b pre-serve gate:** an independently observable **persisted `RecoveryRequired` authority state** (or equally strong reviewed schema-level lock) installed before a restored DB becomes visible. An authenticated host/Scheduler/tool authorization adapter must causally return a typed Recovery denial from this same persisted state, across restarts/two clients, for new stage, claim, launch, capacity, permission, verifier and effect actions. A non-Recovery B error is failure, not success; recovery denial must not commit a new admission.
4. **Reconciliation/release:** demonstrate source N vs newer N+1 Git/launch/review/integration/side-effect evidence; cross-device same-name mismatch, STOP and original operation/claim identities, no old receipt-as-new-dispatch, account-wide capacity fence, Git outage/unknowns remaining blocked, authenticated progressive versioned proof-based release of resolved scope without automatically releasing unresolved scopes or reconstituting manual server-memory grants.
5. **Host-loss rehearsal:** fake-only two-client/disposable restored DB with complete-account authenticated witness, persisted pre-serve quarantine, crash/restart and failure injection, zero premature admission/effect and exact evidence-backed progressive release. Intact DB restart remains a positive control. These are **future tests at the real enforced boundary**, not the current raw B probes.

The staged G1b implementation remains HOLD until B/F1 interfaces are independently reviewed/integrated and the canonical task owner authorizes source work. No G1b production files (`WorkSqliteAuthority`, `WorkSqliteBackup`, `WorkSnapshot*`, importer, Scheduler or host), deployment/restart, production migration, external action, credential grants or unattended Scheduler are changed or enabled by this artifact.
