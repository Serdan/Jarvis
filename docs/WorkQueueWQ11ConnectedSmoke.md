# WQ11: connected, default-off task-to-Scheduler positive smoke

## Scope and source

This is an explicit **test-only** connection of integrated WQ02, WQ04, WQ07, and WQ08 adapters. It is not Scheduler activation, admission authorization, launch, or a new production execution path.

The worker verified `git ls-remote origin refs/heads/main` at `162f0cd98fc0ee7d9c58349ba6316d1901d74479` on 2026-11-16 and branched the isolated worktree `test/wq11-wired-positive-smoke-worker7` from that commit.

Entry point: `Client.Tests/WorkQueueReleaseAcceptanceTests.fs`, test `OriginalCreateTaskReceiptReachesRealSchedulerPreviewWithDisposableCapOne`.

## Connected path actually exercised

1. Create one **real canonical CreateTask** using `ReviewWorkflow.createTask`. This writes its original, stable operation UUID and immutable request hash to the disposable project Git ledger; it does not fake a task event.
2. Call the **same explicitly invoked WQ04 receipt producer** exposed by `JarvisClient queue-producer ... produce`: first `WorkQueueReceiptProducer.preview`, then `WorkQueueReceiptProducer.produce` with a distinct stable UUID. It uses the WQ04 canonical CAS binder and creates genuine planning work plus immutable origin.
3. Independently fetch the project from the **other Git clone** using `WorkQueueGitLedger.load` and `readOriginAt`. Assert that the stage is Available, is in the planning queue, and retains the source CreateTask UUID/hash.
4. Read an independently initialized **disposable WQ07 account capacity Git authority** and the versioned, enabled `Planner` role using the **real WQ08 read-only Scheduler host preview**, `WorkQueueSchedulerHostPreview.preview`, not fixture ports. Check the exact selected work/role, original operation ID, source hash, provenance blob OID, verified Git heads, cap=1, occupied=0, available=1.
5. Reserve **simulated capacity only on the disposable account ledger**. Preview again: the same work remains visible, but occupied=1, available=0, and `CapacityFull` is reported.
6. Replay the **original stable CreateTask and producer UUIDs** from the opposite clone after later Git heads. Require idempotent AlreadyApplied outcomes, reject altered original CreateTask payload, and confirm no duplicate work and no project launches.

The test uses `Path.GetTempPath()/wq11-acceptance-<random>/` with a local bare remote and two independent clones. Its `finally` block deletes the disposable remote and both clones. It asserts both source checkouts remain clean. No real account, project, source branch, browser, provider, or timer is touched.

## Commands and validation

Verify remote main before branching:

```sh
git ls-remote origin refs/heads/main
```

Run the focused .NET 11 Release smoke (from this worktree):

```sh
dotnet --version
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter 'FullyQualifiedName~OriginalCreateTaskReceiptReachesRealSchedulerPreviewWithDisposableCapOne' --logger 'console;verbosity=normal'
```

The wider connected acceptance set was run with:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests' --logger 'console;verbosity=minimal'
```

Observed on SDK **11.0.100** / Release / net11.0: focused connected smoke **1 passed / 0 failed** (4.38 s test run); connected WQ11 + canonical binder + Scheduler host preview regression **24 passed / 0 failed / 0 skipped** (31 s test execution). Both commands exited zero. This is a targeted Client.Tests slice, not a claim that the entire solution was tested.

## Deliberate boundaries and next missing adapter

- The original WQ04 producer's **module API** (used by its explicit CLI) is exercised, but this test does not modify an operator's persisted trust binding or start a JarvisClient CLI process. This keeps the smoke fully hermetic.
- A preview is **only information**: `CanDispatch=false` is intentionally unchanged. The account's migration phase is not promoted; a migration-related preview issue is expected while keeping the route default-off.
- No token is reserved on a real account. Only the disposable capacity ledger is altered to demonstrate a full-capacity read.
- This does **not** prove WQ03 browser submission, a fake-provider once-only launch, an actual UUID registration/first-connect, a terminal claim/release, or an executable multi-project Scheduler admission decision.
- The next useful bounded proof is a separate **explicit, default-off fake-provider adapter** from the selected, pinned demand and account/project authority through one durable launch/original-operation journal and first-connect, followed by terminal claim/release. Its operation must have once-only replay and unknown-outcome observability. The production Scheduler gate stays OFF pending operator authorization.
- Existing WQ11 coverage for conflicting Git heads and unknown original effects remains relevant; this smoke adds the missing **positive connected visibility** proof, rather than replacing that recovery coverage.
