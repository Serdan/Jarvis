# Independent review — WQ11 Linux Client.Tests diagnostic assertion update

**Review:** b69a1214ce58498ba36f75c7011fd4d4  
**Frozen head:** `9eca1db7b254c9e615c7e1b2296d97d3cfe4c28a`  
**Base:** `0231ea78ed758a8fa1c63295c59f74ca53267b89`  
**Verdict:** Accept — no blocking findings in the submitted patch.

## Source inspection

The complete base-to-head diff modifies only `Client.Tests/OrchestrationApiTests.fs`, replacing two stale expected-message substrings in unconfigured **write** tests with `ledger has no approved local binding`. Production `Client/Modules/OrchestrationApi.fs:118` contains that same diagnostic. Read-path expectations, authorization, production code and test fixture semantics remain untouched. `git diff --check` passed; the changed tests preserve checks that no binding/tasks are created and Git status remains clean.

## Reviewer-run verification

Ran on the frozen worker worktree with .NET 11 Release using the existing Linux hermetic runner:

`dotnet fsi scripts/test-client-hermetic.fsx -- --timeout-seconds 120 --filter 'FullyQualifiedName~disabledReadAndWriteDoNotTouchV1Tasks|FullyQualifiedName~ledgerTaskSetupAndInboxRefuseUnconfiguredCheckout'`

Result: **2/2 NUnit tests passed**, exit 0, not timed out. The runner reported its disposable environment was removed. No full unfiltered suite was run by this reviewer.

## Worker diagnosis evidence (reported, not independently rerun)

Worker 4 reports 951 default Client.Tests cases passing in eight disjoint hermetic partitions: Review 143/143 (212s), WorkQueue 98/98 (128s), Challenge 36/36 (90s), Activity 26/26 (4s), D1 156/156 (14s), D2 86/86 (43s), E 229/229 (76s), complement 177/177 (11s). Review plus WorkQueue alone take approximately 340s, exceeding the unfiltered harness's 240s limit. These are worker-reported partition measurements, **not** a claim that the 240s unfiltered suite is green.

## Scope and follow-up

Accept this precise assertion correction and measured diagnostic handoff. The aggregate full-suite time-budget issue remains open separately; this review does not approve broad timeout inflation, changes to the hermetic isolation contract, or integration to main.