# P1 — Stopped registered execution can claim new ancillary Role/Task messages and strand their handlers

**Severity:** P1 — exclusive handler safety, queue demand coverage, and stalled-execution liveness.  
**Frozen source:** `4b8d76eeccf02b2dd12d0291281401447bc6d700`  
**Locations:** `Server/WorkQueueContracts.fs:675-690`, `:884-930`, `:934-979`.  
**Accepted architecture:** `docs/specs/work-queues-and-ephemeral-executions.md` §9, "Stop-requested transition" (line 299), and §11 message-handling/recovery (lines 367-375).

## Observation

`markStalled` leaves the launch in `LaunchState.Registered` with its primary `WorkStage.State = Claimed L`, but mutates `launch.Lease.StopRequested = true`. This is intentional: the STOP request is not yet a project-terminal receipt, and the old launch/token still need recovery.

`claimAncillaryMessage` authorizes **both** passive Task-message handling and stage-backed Role-message handling using only `launch.PrimaryWorkId.IsSome`, `launch.State = Registered`, and `message.State = Pending` (lines 887-889). It neither checks for a lease with `not StopRequested` nor verifies that the primary remains a live `Claimed L` stage. The Role branch can then take W from `Available` to `Claimed L`, assign the exclusive message handler and rematch unclaimed launch credits (lines 908-928). The Task branch likewise takes passive attention at lines 900-906.

This is inconsistent with the newly tightened `completeAncillaryMessage`, which correctly rejects acknowledgements when the launch lease is `StopRequested` (lines 942-948). The accepted §9 rule is explicit: after a Stalled/StopRequested receipt the original session may perform safe shutdown/checkpoint/terminal operations **but must not claim new work or advance unrelated stages**.

## Reachable interleaving

1. Registered Lead launch L has primary Plan claimed, an active lease generation g and one pending Role(Lead) message M with `role-msg:M` Available. There is no competing unclaimed launch credit.
2. `markStalled(p.Version, "stall", "L", g, now, threshold, p)` succeeds after the configured idle threshold. L stays Registered and Plan stays Claimed; `Lease.StopRequested = true`.
3. The old L uses the *latest* project version and a fresh operation ID: `claimAncillaryMessage(stalled.Version, "claim-after-stop", "L", "M", stalled)`. All actual guards in lines 887-929 pass, so M and W become `ClaimedAncillary L` / `Claimed L`. This is not a stale-CAS attempt; refreshing the CAS head bypasses the absent lease fence.
4. `completeAncillaryMessage` with the now-current handler generation cannot acknowledge it because `not lease.StopRequested` is false. M/W are exclusively claimed by a STOP-requested execution, no longer available to the scheduler or another role execution. Recovery of this already-claimed launch is left to later-phase implementation. The same unsafe intake exists for workless Task messages.

Thus the contract accepts new responsibility for a stopped execution and can remove valid Role demand from availability with no permitted handler completion. This is an independent static code-path counterexample, not an executed negative fixture. Existing passing tests cover STOP vs heartbeat/primary claim and handler-generation reclaims, but not STOP vs ancillary intake.

## Required correction

Under the **same project CAS** as ancillary intake, require `LaunchState.Registered` plus a present, live, non-stopped lease, and verify the exact primary work is still currently `WorkState.Claimed launchId` with a permitted activation/claim generation (and any relevant gate/fence). Apply this to both Task-passive and Role-stage-backed branches. Preserve same-launch *valid* post-gate reacquisition when Plan is still genuinely live; do not prohibit legitimate registered sessions merely because they have previously reclaimed a handler.

Add deterministic negative/reverse-race fixtures:

- STOP wins before Role claim: fresh-head and stale-head attempts both reject, M remains Pending, W Available, demand credits/account token unchanged.
- STOP wins before Task-passive claim: no handler responsibility assigned.
- Ancillary claim wins before STOP: the stalled claim must remain fenced and be subject to the documented terminal/recovery path, never a newly claimable work item; after an authorized terminal/recovery action, W/M requeue once without duplicated demand or replies.
- Repeat/retry with identical vs changed operation ID/payload; no operation receipt should authorize a post-stop fresh claim.

**Disposition:** Blocking Phase A contract defect. Do not integrate this frozen SHA before a new exact-source review confirms the STOP fence. This is not an assertion that the production Scheduler or message runtime is already deployed.
