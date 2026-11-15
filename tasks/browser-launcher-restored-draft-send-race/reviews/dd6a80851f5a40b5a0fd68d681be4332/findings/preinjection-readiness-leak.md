# F02 — P2: Known pre-injection readiness failures still leak the freshly created tab with the default close preference

**Frozen source:** `406568403971356a62dcedd3090ba31883c939aa`.

**Evidence:** `Client/Modules/BrowserBridge.fs:760-774,799-803,821-824` treats `TabNotReady`/repeated exact `TabUnavailable:loading` as a confirmed **pre-injection** failure, with the comment "No user script was injected; cleanup cannot cancel a submission." Yet `cleanupNotReady()` closes only if `closeAfterExecution=true`, and otherwise deliberately returns `TabNotReady; cleanup=retained`. `Client/SignalR/Client.fs:665` defaults `CloseTabAfterExecution` to **false** for `BrowserRunInNewTab`; therefore normal generic invocations that time out waiting for a discarded/loading tab leave an agent-created registration and potentially a restored unsent draft behind even though the run cannot have sent anything. The 20-tab active cap makes repeated failures consequential.

**Coverage gap:** `Client.Tests/BrowserBridgeTests.fs:536-592` tests both success and failure of readiness cleanup only with `closeAfterExecution=true`, so it does not catch the default false branch. The new definitive-result tests at lines 435-534 exercise page-script statuses after successful execute, not this known no-injection failure.

**Required correction:** Treat a positively confirmed pre-dispatch no-script failure as a disposable agent-owned tab independent of the caller's *post-success* close preference, using the same pinned `ownerConnectionId` and `registrationGeneration` safety fence and explicit `cleanup=closed`/failure result. Continue retaining on changed ownership/generation, uncertain post-dispatch errors, and possible sends. Add a deterministic regression invoking this path with `closeAfterExecution=false`, proving exact close request, no replay, failed cleanup surfaced, and unrelated registrations untouched.

**Disposition:** Correct in the current scoped cleanup change; this is a residual definitive failed-launch leak, not an uncertain-send path.
