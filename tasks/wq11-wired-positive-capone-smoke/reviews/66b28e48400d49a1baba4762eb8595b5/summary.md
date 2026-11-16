# WQ11 connected positive smoke — Reviewer 2 independent review

**Frozen source:** `2b3be69eb50253099e387a3b7bc4b2131a48bcdb`  
**Compared with:** `162f0cd98fc0ee7d9c58349ba6316d1901d74479`  
**Assessment:** Accept — no blocking findings.

## Verified changes and behavior

The exact diff changes only `Client.Tests/WorkQueueReleaseAcceptanceTests.fs` (+114 lines) and `docs/WorkQueueWQ11ConnectedSmoke.md` (+52 lines); no production dispatch code or configuration changes.

The test runs the existing WQ02 `ReviewWorkflow.createTask` original Git operation and WQ04 `WorkQueueReceiptProducer.preview/produce` APIs. A second disposable clone independently fetches the resulting WQ04 canonical work and immutable origin. The real WQ08 `WorkQueueSchedulerHostPreview.preview` binding reads versioned role state, project Git head, provenance, and a separately initialized WQ07 disposable account authority. Assertions cover cap=1/free=1, cap=1/occupied=1/free=0 with `CapacityFull`, stable original/producer UUID replay, altered-payload rejection, no duplicate work, and no project launches. The helper creates a local bare remote plus two clones and deletes them in `finally`.

## Independent execution

From the isolated Worker 7 worktree using .NET 11 Release:
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~OriginalCreateTaskReceiptReachesRealSchedulerPreviewWithDisposableCapOne`: **1 passed, 0 failed, 0 skipped**, exit 0.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~WorkQueueReleaseAcceptanceTests|FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~WorkQueueSchedulerHostPreviewTests'`: **24 passed, 0 failed, 0 skipped**, exit 0.

The existing `Client/Program.fs` FS0760 compiler warning is outside this diff.

## Explicit limits

The test calls the same producer module used by the explicit CLI but does not launch a CLI subprocess. It establishes **connected demand visibility**, not actual launch authorization, migration activation, browser send, provider once-only launch, first connection, or terminal claim/release. `CanDispatch=false` correctly remains unchanged. The documentation states the bounded next adapter needed for a fake-provider positive route. No blocker against the requested default-off WQ11 acceptance slice.
