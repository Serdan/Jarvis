# Supplemental independent WQ03 UUID / host-scope audit — reconciled with canonical ChangesRequested

**Reviewer:** Reviewer 2  
**Frozen review:** `b4b9f0e236f44d7c9a416928f93d882a`, task `wq03-source-effect-journal-bridge`  
**Exact source:** `f85c288d45981aef5502ce953f7e135e2fd9e965`; direct accepted inert parent `e7a8a3edacbbcdfc3f74e72361b21a6a912f85da`  
**Canonical outcome:** **ChangesRequested, decision sequence 3687**. This supplemental audit confirms the separate blocking P1 detailed in `findings/missing-journal-replay.md`. This document replaces an earlier, overly narrow recommendation of inert acceptance: the new live opt-in routed callback makes the missing-journal recreation an actual at-most-once failure even in the nonproduction file-only path.

## Independent verification

- Pin/ancestry: `review_details` reported exact f85c288d; `git show --stat` shows a single parent e7a8a3ed and 11 changed files (627 insertions, 58 deletions). Registered source checkout `worker6-wq03-host-bound-uuid-3627` was clean. `git diff --check e7a8a3ed f85c288d`: exit 0.
- .NET toolchain: `dotnet --version` = **11.0.100**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~SourceEffect --logger console;verbosity=normal`: **24 passed, 0 failed, 0 skipped**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --logger console;verbosity=minimal`: **225 passed, 0 failed, 0 skipped**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkQueueGitLedger|FullyQualifiedName~SignalRDispatch|FullyQualifiedName~SourceEffectHostBinding' --logger console;verbosity=minimal`: **29 passed, 0 failed, 0 skipped**, overlapping the first filter. All executed locally on frozen Release source; no remote push or hosted Actions.
- The above passing tests do **not** cover removal/replacement of the journal DB after a first dispatch. This is a static source-code failure proof, not an executed deletion fixture.

## Blocking P1: missing journal silently resets the original-UUID replay fence

`Client/Modules/SourceEffectHostBinding.fs:125-146` calls `openAtHostRoot` immediately before a real opt-in file edit. In `Client/Modules/SourceEffectJournal.fs:215-258`, if the configured directory exists but `source-effects-v1.sqlite` does not, `openAtHostRoot` **creates a new empty SQLite effects/fences database and returns Ok**. `beginBeforeDispatch` then finds no previous row for an already-used UUID and reports `FirstDispatch`; `runAwaitedHostFileEdit` launches the callback again.

A crash, accidental deletion or root replacement between client A's original effect U and client B's retry with *the identical U, payload and canonical scope* can therefore execute the source edit twice. A restored SQLite journal with no independent continuity proof is not proof that U never existed. This violates the source task's durability, restart and original-UUID replay requirement. The existing intact-DB replay tests do not cover that failure mode.

**Required before acceptance:** provisioning separated from dispatch-only open; an independently durable enrolled journal/root continuity anchor; missing/replaced journal must produce RecoveryBlocked before the mutation. Add two-client/second-process tests of first effect → close/crash → journal deletion/root switch → retry **same UUID** → zero subsequent callbacks. Keep intact-DB and corrupt-schema controls. Full details and required correction are in the canonical `findings/missing-journal-replay.md`.

## Verified narrow positives that do not waive the P1

| Invariant | Evidence | Status |
|---|---|---|
| Caller-retained UUID through WriteFile/PatchFile MCP → server → distinct SignalR ReceiveSourceCommand → bounded queue/promotion | `McpTools.sendWithSourceOperationId`, `ClientService.SendCommandToUser`, `Client/Program.fs`, `Client/SignalR/Client.fs`; server transport test | **PASS, narrow** |
| GUID validation; no implicit UUID substitution from correlation/job IDs; different request/hash/scope rejected | `parseSourceOriginalId`, `bindCanonicalClaim`, `SourceEffectJournal.beginBeforeDispatch`; client/server negatives | **PASS, intact store only** |
| Trusted project/ledger/repository/launch/checkout construction | `prepareHostFileEdit` reloads approved trust config, registered source checkout and verified canonical Git-CAS snapshot; `bindCanonicalClaim` demands unique Claimed Implementation, Registered primary launch, generation and gate | **PASS, scoped pure-seam tests; real host/actor ownership not fully proven** |
| Prepared fence commits before callback; concurrent original UUID from two store handles runs only once | SQLite transaction/unique UUID and new host binding tests | **PASS, intact store only** |
| File result error/exception or unknown process/push is never accepted as full succession proof | `SourceEffectJournal`, `SourceEffectRouteBridge`, unconditional `verifySourceExternalEffectsAndRefProtection` error | **PASS, fail-closed current handoff** |
| Same original UUID after missing/replaced journal | `openAtHostRoot` creates empty DB and `beginBeforeDispatch` returns FirstDispatch | **FAIL — P1 blocking** |
| Upstream original-ID issuance/retry admission bound to authenticated initiating agent/session and claim | `bindCanonicalClaim` does not accept/verify initiating actor/session; caller may mint a different UUID | **UNPROVEN — production blocker** |
| All OS/Git routes, original remote push/late effects, canonical RecoveryBlocked CAS | Only file-only path covered; `inspectForHandoff` remains RecoveryBlocked | **UNPROVEN — production blocker** |
| Host WIP protected create-only, nondeletable, nonforce, retained refs and effective credential bypasses | External host policy not inspected or mutated | **UNKNOWN — external production blocker** |
| YieldSource, Scheduler, production cutover | Unconditional source-effects/ref-policy hold; exact nonproduction env opt-in and Frozen migration | **BLOCKED, intentionally** |

## Final review disposition

**ChangesRequested** for exact `f85c288d45981aef5502ce953f7e135e2fd9e965`. The missing-journal replay reset is a real failure of the new source-effect route's original-operation at-most-once property, regardless of the present YieldSource hold or default-off flag. Preserve the accepted parent, keep new routed source handoff dormant, and require a new exact frozen independent review after fixing journal continuity. No code, host settings, WIP refs, remote push, Scheduler, deploy or Codex used by this supplemental audit.