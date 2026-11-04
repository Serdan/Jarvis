# Review summary — Accepted

**Exact snapshot:** `90689856b590c5114823cc9f28dba6012adf73e5`; incremental correction since `23a6a5582f238d40b1a9a5fa446513c72682f6bc`.

The previous stale-session finding is resolved. Checkpoint, yield and completion calls that omit optimistic versions now verify the calling session against the active Working session within the ledger transaction. SignalR supplies SessionId, and OrchestrationApi preserves it in the intent journal. Calls providing all exact version preconditions retain prior behavior. Administrative recovery preserves session history so a prior execution cannot restart the new queued generation. The new Git-backed regression covers same-task/owner generation 1-to-2 recovery, stale resume before and after new start, stale checkpoint/yield/complete rejection, and valid new-session writes. No blocking issue remains.

Tests: 341 client tests excluding two unrelated startJob tests plus all 75 server tests passed; full suite hit the known five-second startJob timeout. git diff --check clean. Documented limitation: versionless completion from Ready requires resuming first or supplying exact versions.

**Decision: Accept.** Lead must reconcile protocol/catalog edits across parallel branches and run combined tests before deployment.