# Independent frozen-source findings: WQ-03 source-effect journal

**Reviewer:** Reviewer 2  
**Request:** ee2e8859bcdd48649a23d5bbc2764770  
**Task:** wq03-source-effect-journal-bridge  
**Frozen source:** 273fd0b9dc73558f8c64fac28e24662c2872801a  
**Parent:** cc89eb919c0bf965632f07ec0d6ace5b2c3acde2  
**Scope:** inert, fail-closed original-UUID source-effect journal foundation ONLY. This is not review or authorization of a positive WQ-03 handoff or production dispatch enrollment.

## Verified source and original UUID durability

- Independently read the frozen commit and all five changed files. The registered worktree had exactly the frozen HEAD and was clean before and after tests. One commit adds `Client/Modules/SourceEffectJournal.fs`, its 12-test suite, project file entries, and a limitations document; no dispatch handlers or Worker 5 WQ-03 owner implementation were changed.
- `SourceEffectJournal.fs:25-43,134-181` includes project name/ID, ledger ID/epoch, repository ID, approved fetch+push URL fingerprint derived from `LedgerBinding`, source ref, checkout ID, work ID, launch UUID, generation, effect kind, original UUID, and request SHA. `effects.operation_id` is a global primary key in the host SQLite store (`:239-246`).
- `openAtHostRoot` (`:215-259`) requires a preprovisioned absolute directory, rejects a symlink at the root or DB leaf, initializes SQLite with `synchronous=EXTRA`, and fsyncs the parent directory on Linux/macOS after first creation. Failure to create/read the store returns `RecoveryBlocked`; it does not fall back to volatile job observations. Its host-local permission/retention/Windows crash guarantees are **not** independently attested by this review.
- `beginBeforeDispatch` (`:290-321`) inserts both immutable original-UUID intent and a sticky `RecoveryBlocked` fence in one serializable transaction, before `dispatchOnce` invokes the callback (`:326-342`). After restart or concurrent replay, a matching UUID is `AlreadyRecorded` and cannot redispatch; mismatched source/ledger/scope/kind/request is rejected. A callback exception persists uncertainty when possible; even a failed uncertainty write leaves the already committed Prepared row blocked.
- `recordUncertain` (`:344-365`) refuses absent/mismatched original identities and cannot convert a Terminal row to Uncertain. The module exposes no push/merge/detached-job positive terminalization API. `runSynchronousFileEdit` (`:367-391`) can mark only a first-time in-process FileEdit callback terminal, but never removes the lineage fence.
- `inspectForHandoff` (`:417-477`) reports known UUIDs, missing/wrongly bound UUIDs, incomplete route coverage and sticky reasons and **always** emits `RecoveryBlocked=true`, even when `AllKnownOperationsTerminal=true`. No empty inventory, clean checkout, elapsed-time observation, absent job, remote absence or synchronous local edit can free a successor.

## Independently executed .NET 11 Release evidence

Command, in the exact frozen `wq03-source-effects-worker6` checkout:

```sh
dotnet --version
# 11.0.100
dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter 'FullyQualifiedName~SourceEffectJournalTests' --logger 'console;verbosity=normal'
```

**Exit 0; .NETCoreApp v11.0; NUnit discovered 12/12; 12 passed, 0 failed; total execution 0.7426 s.** Verified: predispatch row is observable inside callback, restart same-UUID suppression, concurrent two-client at-most-once, two actual disposable Git clone directories with checkout identity mismatch, simulated lost push reply, clean checkout with Prepared old process, local terminal edit still globally blocked, changed payload/project/ledger rejection, post-effect callback error, missing legacy inventory, sticky fence restart and corrupted DB. Build warnings FS0760 (Client/Program.fs) and FS3369 (BrowserSchedulesTests.fs) did not fail the tests. The test run built `Client` and `Client.Tests` in Release. No hosted Actions were used.

## Unresolved activation gates — **P1 operational blockers**, not authorization by this verdict

1. **No production route completeness.** Frozen-code call-site search finds only the new module and project/test files; there is no enrollment of actual RunCommand, StartJob, RunProjectTask, WriteFile, PatchFile, ImportFile, GitCommit, push/merge/retry, or out-of-band OS/Git routes. The module explicitly enumerates those as uninstrumented (`:410-415`). A new caller-created UUID or callback is not authoritative proof that a real host-dispatched effect had that original identity. An empty or fully terminal local inventory is not sufficient to release source.
2. **No authoritative remote/process termination or positive successor proof.** There is no bound process-tree terminal attestation, immutable original push receipt/late-arrival reconciliation, or positive public WQ-03 YieldSource -> exact protected WIP SHA -> successor claim test. The 12 tests prove conservative primitive behavior, not late asynchronous remote push outcomes or distributed two-host authority.
3. **Canonical WQ-03 blocked state not integrated.** The local SQLite fence is not the canonical `GateState.SourceRecoveryBlocked` transition required by the earlier WQ-03 acceptance-blockers review. No positive claim, launch or token release may occur until that transition and full safety bridge are independently reviewed.
4. **Git host proof missing.** Actual credential/bypass create-only, no-update/no-force, no-delete, retained `refs/heads/jarvis-wip/**` controls, including ref/object GC retention, remain external. No destructive production host probing was performed.
5. **Integration ancestry requires coordination.** Frozen commit parent `cc89eb91` is not an ancestor of inspected local `main` (then `90ed868d`, with branch ahead 1/behind 4 vs origin/main). Do not treat this review as evidence that a direct integration into present main is valid. Lead must reconcile current-main ancestry without losing the accepted frozen implementation and obtain any required new combined exact review.

## Non-blocking design cautions for follow-on review

- `inspectForHandoff` is logically observation-only but obtains an SQLite **ReadWrite** connection and executes `PRAGMA journal_mode=DELETE` via `connect`; a production claim of strictly read-only storage access needs separate inspection or a read-only open path.
- The POSIX parent-directory fsync in `DurableDirectory.sync` is a **no-op on Windows** (`:85-97`). The branch's targeted Release run is on Linux. Do not promote this to an audited Windows power-failure durability guarantee without host-specific evidence.
- `runSynchronousFileEdit` accepts an unconstrained callback. Before connecting it to live endpoints, ensure only trusted synchronous, fully completed host-owned edits can receive the limited `Terminal` record; a callback that merely launches asynchronous work would not be proof of terminal physical effects.
- The “two clone” test makes real disposable clones but tests an application-supplied checkout-ID mismatch; the “lost push” case simulates reply loss and is not a real delayed-push fault-injection. This is adequate only for the primitive's negative-space claims.

## Finding and disposition

**No blocking correctness defect identified in the submitted *inert, always-RecoveryBlocked* foundation. ACCEPT this frozen foundation only.** Keep the WQ-03 end-to-end/activation gate **BLOCKED**. A separately frozen review must assess complete trusted dispatch wiring, original-UUID lifecycle and remote terminal evidence, canonical blocked-state transitions, effective protected WIP retention, and a positive-but-safe public source handoff before activation. No Scheduler activation, deployment, migration, main push, GitHub Actions or Codex was performed.
