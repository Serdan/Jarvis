# [P2] Reentrant executor wake bypasses duplicate suppression

**Frozen commit:** `b3e3d9c2f5f5fef1b6697cfca872c26177d1d6d1`

**Location:** `Client/Modules/WorkQueueReactiveWake.fs`, `Coordinator.Wake` around `lock sync`, `execute intent`, and `remember recent original`.

A .NET monitor lock is recursive on its owning thread. `Wake` invokes injected `Execute` while holding `sync`, but only inserts `OriginalBrowserOperationId` into `recent` *after* `Execute` returns. If the executor synchronously emits a capacity/work transition signal and invokes this same `Coordinator.Wake` in an event handler, that nested call re-enters the lock and sees neither `recent` nor `unknownOriginals`; it calls `Execute` again with the same operation. The included parallel `Task.Run` test cannot expose this path, because different threads serialize on the monitor.

**Minimal regression:** define a fake executor that increments a counter and, on its first invocation, calls `coordinator.Wake [WorkWakeTrigger.CapacityReleased]` synchronously, then returns `Applied`. Use the existing constant report with the same valid candidate. Two `Execute` invocations occur, contrary to the one-callback coalescing guarantee. A failure/unknown result in a nested call can also make the outer result misleading.

**Correction:** mark an in-flight original operation *before* invoking external callbacks, with explicit in-flight/definitive/unknown state transitions. Reentrant calls should report coalesced or needs-attention without re-executing. Do not treat an in-memory mark as durable proof after restart; the executor must still reconcile the same original UUID from the approved journal. Test same-thread reentrancy in addition to concurrent threads.