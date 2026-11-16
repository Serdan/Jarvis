# P1 — cancelled composition parent can release an original held WQ04 stage

## Frozen source
- Review: `65e741d4fead4a27b9fc2c12a92682e4`
- Exact review commit: `2f652b1b76511b08277334203dae3f663deac4b8`; independent accepted ancestors `117ec8630` (writer fence), `9602a85ac` (composition fixture), `55c162c68` (typed private-host NO-GO) are present, clean, and `git diff --check 117ec8630..2f652b1` passes.
- This finding is in the *new integration rollup*, not a claim that host-private dispatch is production-ready.

## Reproduced failure on exact frozen source
Isolated review worktree `review/reviewer5-rollup-cancel-parent-20261117` is based on `2f652b1`. Only a local, uncommitted regression was appended at `Client.Tests/WorkCompositionQueueTransactionTests.fs:528-551`; the frozen review branch was not changed.

Run:
```
dotnet test Client.Tests/Client.Tests.fsproj -c Release \
  --filter FullyQualifiedName~CancelledParentMustNotReleaseHeldOriginalWq04StageWhenPrerequisiteBecomesAccepted \
  --logger 'console;verbosity=normal'
```
Result: **1 discovered / 1 failed as expected** (exit 1; ~3.77 s of test execution, .NET 11.0.100).

The regression uses the existing *real original WQ04* two-clone project ledger and independent accepted review/integrated receipt fixture. It:
1. Publishes the original immutable WQ04 `delivery-task` planning stage with a hard dependency on `prerequisite`; its WQ02 state is `Blocked` with `CompositionHold`.
2. Produces the independently Accepted/Integrated receipt for `prerequisite`, before composition acceptance.
3. Cancels the dependent feature's **parent** `delivery` through the ordinary `WorkCompositionGitLedger.execute(Cancel)` path. This is a valid graph mutation; the stage stays held.
4. Re-reads the fresh original Git head from the second clone and invokes `WorkCompositionQueueTransaction.acceptAndRelease` with the valid original stage/operation/receipt provenance.

**Expected:** a recovery-blocking refusal, unchanged original head and held stage. **Actual:** `Ok` for `WorkCompositionQueue.FrozenAcceptAndRelease`, one operation writing both `records/work-composition/graph.json` and `records/work-queue/project.json`, releasing the WQ02 stage to `Available`. NUnit failure: “Cancelled composition parent unlawfully released WQ02: Ok ...”. A canceled ancestor cannot authorize local queue eligibility.

## Root cause
- `Client/Modules/WorkCompositionGitLedger.fs:154-165`: `graphBlocked` correctly includes cancelled ancestors. Original WQ04 stage publication uses this gate (`:252`); ordinary graph edits use it in same-CAS `queueRevocations` (`:398-400`).
- `Client/Modules/WorkCompositionQueueTransaction.fs:56-101`: `load` rejects only `dependent.CancellationReason.IsSome`, not canceled **ancestors**, and accepts a held stage.
- `Client/Modules/WorkCompositionQueueTransaction.fs:194-236`: `acceptAndRelease` proves prerequisite acceptance and WQ02 dependencies but never checks `WorkCompositionGitLedger.graphBlocked nextGraph dependent` **after** the prerequisite acceptance. This bypasses the very graph gate used by original WQ04 and revocation. A valid prerequisite review/integration is insufficient if a parent is canceled.

## Required correction and scope
- Re-check the *entire current composition blocker set* on the proposed `nextGraph` before changing `CompositionHold` to `Clear` and WQ02 to `Available`, including canceled ancestor chain and any other blockers. Reject in the same original CAS, not through an after-the-fact second mutation. Preserve original immutable WQ04 link and no new source operation ID.
- Add this negative two-clone fixture on the worker's new source branch, plus reopened-parent/stale original head and sibling correctness if relevant. Re-run local .NET 11 Release focused and adjacent tests; freeze a **new** independently reviewed commit.
- Do not enable Scheduler, public Work host, production migration, credential send, or cutover. Existing private-host API remains correctly typed `HostBridgeUnavailable`.

**Review disposition:** ChangesRequested, P1. Local reproducible safety failure in the offline positive path; global production remains NO-GO regardless of other green tests.
