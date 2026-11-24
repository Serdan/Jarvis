# Automation Challenge -> verified wake adapter (Worker 17, 2026-11-22)

## Scope and disposition

**Unwired, read-only adapter on the accepted-only private source base.** This lease does **not** authorize enabling the scheduler, a full Automation cutover, native B2 provider/terminal operation, registered-Health recovery, browser sends, account reservation, deployment, or changes to `main`. This adapter never drives a Challenge mutation or schedules Work itself. Canonical Challenges already own the Ready/requeue transition in a single Git CAS.

Source checkout: `feat/automation-challenge-wake-worker17-20261122`, created as a distinct clean registered worktree directly from `d312f1cc83685e781ce69e57ad0d4bf613bfb843`. No existing production files or `Client.fsproj` are edited. New source is `Client/AutomationChallengeWakeAdapter.fs`, compiled **only** into standalone tests; no production host registers it.

The implementation checks a caller-supplied **locator**, not an entitlement. It independently fetches the approved canonical project Git head and revalidates the immutable `ResolveChallengeCondition` operation, its original origin/paths, exact challenge and event, completed conditions, current dependency wait, exact blocked-generation reverse owner/challenge index lineage, current Queued attempt, durable issued assignment and lifecycle event, and the owner's verified Ready projection. If any input diverges, it returns `Hold` and emits **no hint**. `Off` does not read Git.

If `Ready` is proved, `deliver` invokes the existing real `WorkQueueReactiveWake.Coordinator.Wake [WorkWakeTrigger.DependencyCommitted]`. A signal is only a lossy hint; the coordinator **independently** rereads and fences account/project/role/Work source, capacity, original operation identity and planner intent, and may refuse execution. This adapter cannot create Work, requeue an attempt, grant access, mint launch IDs, reserve capacity, execute provider operations, or recover an unknown outcome.

## Existing canonical writer and exact seam

- `Client/Modules/ChallengeWorkflow.fs`: `blockAttempt` in one Git CAS stores `AttemptChallengeWait` and immutable owner/by-challenge edges, puts attempt in `Blocked` and owner in `NeedsWork`. `resolve` -> private `update` -> private `wakeDependents` on final `Satisfied` verifies actual reverse edges and all sibling dependencies. If owner is free, that **same** resolving CAS sets wait `Requeued`, attempt `Queued` at `generation+1`, issues durable assignment and lifecycle event and makes agent `Ready`; if busy, it persists `Eligible` instead. Direct `requeueAttempt` provides an existing explicit CAS recovery path for manual/legacy waits. There is no reason or permission for this adapter to add another requeue writer.
- `Client/Modules/ReviewWorkflow.fs`: `getAgentInboxAt` performs verified owner-wide historical reverse index checks in the exact fetched snapshot. `getTaskAt` verifies current task/attempt. The adapter additionally requires the challenge edge and matching generation plus exact requeue issuance/lifecycle in the resolving operation's immutable path list.
- `Client/Modules/LedgerStore.fs`: `getOperationAt` validates persisted original request and result hashes; `getOperationOriginAt` checks immutable originating commit and approved trail. The adapter rejects altered head, project or ledger identity, invalid locator, missing/mutated operation, altered index and incorrect generation.
- `Client/Modules/WorkQueueReactiveWake.fs`: `Coordinator.Wake` is the existing independently fenced Work-side reconciliation API. No new subscriber is wired.

**Production missing hook, intentionally left unresolved:** no registration exists to pass a verified `ResolveChallengeCondition` receipt from the authenticated project command path through `AutomationChallengeWakeAdapter.inspect/deliver` to an approved default-off WorkQueue host. `Client/Client.fsproj` excludes `Client/AutomationChallengeWakeAdapter.fs`. Without a separately reviewed production host and trust binding, this is not a runtime integration. A standalone FSI expected-RED script under `tests/AutomationChallengeWake20261122/ExpectedRedHostHook.fsx` probes that absence from the genuine production DLL.

## Frozen source provenance

Actual `git ls-tree HEAD --` verified all eight exact blobs from parent `d312f1cc83685e781ce69e57ad0d4bf613bfb843`:

```text
Client/Client.fsproj                                5ba8b9c5c9ff2fc25c1464f101b52afa819333bd
Client/Modules/NativeHealth.fs                      4753293d7da5379db18ade6f03e40ac34fb3d58b
Client/Modules/ChallengeWorkflow.fs                 981011513c2e7636d9318e907987d4d2232e3fda
Client/Modules/WorkQueueReactiveWake.fs             87f81d557b2f7ed00efccb693b9c08a78e3cb0e1
Client/Modules/WorkQueueSchedulerHostLoop.fs        800a7388dc301a1560de0aa22f07f5a1d9fbad59
Client/Modules/WorkQueueSchedulerHeadBinding.fs     6218666e1f5089773afbe567781b71b2d6c60124
Client/Modules/NativeSchedulerAccountReservation.fs fd9c9591c74ce906e15dfc191c2dbac0995b8dc3
Client/Modules/WorkQueueNativePrivateHostBridge.fs  9a2b51f261da7c1a42e455fcbe78bfb0642f61c8
```

## Test isolation and evidence

Standalone `tests/AutomationChallengeWake20261122/AutomationChallengeWake20261122.fsproj` targets net11.0 and references the real Client assembly, compiling the new unwired adapter only in this private test project. Its fixtures use a fresh local bare Git remote, two independent disposable clones, real `LedgerStore.initialize` and agent registration, actual `createTask`, `assignTask`, `beginAttempt`, `blockAttempt`, `resolve`, immutable canonical origin and signed Git CAS. They must not be called a production provider test or full Client/Server Release pass. Stale/forged inputs must return `Hold` rather than a fake success; no browser/provider send is allowed.

RED-first genuine .NET11 Release build job `945b112f5feb4954ad224a1cce2bbb06` exited **1**, with FS0225: `Client/AutomationChallengeWakeAdapter.fs` absent before implementation. The next compile run `e3911ca9915e4c65b1a4fc9463319518` exited **1** with new-file F# errors (missing import and ambiguous record inference), both repaired without touching existing sources. A subsequent job `3439d3d0c72e4972b0669eb97b3792f0` compiled, wrote `w17-attempt2.trx`, exited **1** with five passed / one failed; forged stale generation was safely held as `ResolutionMismatch` instead of the more precise `StaleGeneration`. The early generation check has been corrected.

**Final focused Release evidence:** job `5ae9990de01c4288b1ae3df4283ab1c8` completed with managed process **exit 0**; NUnit terminal **8 passed, 0 failed, 0 skipped, total 8**. The genuine `tests/AutomationChallengeWake20261122/TestResults/w17-focused-final.trx` reports `Counters total="8" executed="8" passed="8" failed="0" error="0" notExecuted="0"` (read back from the generated file). .NET 11 Release command:

```sh
dotnet test tests/AutomationChallengeWake20261122/AutomationChallengeWake20261122.fsproj -c Release --logger 'trx;LogFileName=w17-focused-final.trx' --results-directory tests/AutomationChallengeWake20261122/TestResults
```

**Separate expected RED production-hook proof:** job `038854ddfa844f46913194b17b9842cf` completed with managed **exit 1** running `dotnet fsi --exec tests/AutomationChallengeWake20261122/ExpectedRedHostHook.fsx`; real `FS0039`: namespace `Client.AutomationChallengeWakeAdapter` is not defined in the production Client DLL. This is intentionally absent and **not** a failing member of the passing NUnit suite. No full Client or Server suite has been run or claimed.

## Residual risk

- The native Challenge subsystem and the WorkQueue scheduler currently describe different work abstractions; a committed Challenge resolution is **not** a Work admission or launch authorization. The signal remains independent of and subordinate to the scheduler's authenticated account, role, planner and provider gates.
- A lost wake hint or process restart requires the scheduler's existing periodic/manual canonical full scan, not a synthetic retry or a new operation ID.
- Future production integration must design a registered receipt-bearing host port with correct account/project identity and verified-head binding, independent review, original operations and idempotence. Do not promote a disposable B1 fixture, test-only candidate or B2 missing provider trust root into production evidence.
