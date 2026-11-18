# Independent review — P2 terminal resize bounds and TUI recovery

**Reviewer:** Reviewer 2  
**Frozen review:** `b6c5221720264ed29b4a62af44f5c6f2`  
**Exact head:** `8daffe0ca8f7f83abcc2ac8889002ebb7b88ae07`  
**Baseline:** `aa2e46ff2927b8e9fd0af25204377c299f2ec576`  
**Verdict:** Accepted **for the reviewed implementation**, not live Windows release acceptance.

## Independent source assessment

Inspected the exact five-file diff and task contract on a clean registered worktree at the frozen head. The identified stale-frame out-of-bounds operation in `TerminalFrame.PresentOn` compared the new right-edge cell to a narrower prior frame even when a full redraw was required. The change explicitly suppresses old-edge lookup on a full redraw; the main cell-diff comparison was already short-circuited by `full`. This addresses the root index defect rather than relying only on exception handling.

`TerminalLayout.tryFrameSize` rejects 0/1-column and zero-row physical geometries without inventing a drawable frame, while reserving the rightmost physical column. `ConsoleTui.renderForGeometryUnsafe` uses a frame-sized layout snapshot; `PresentOn` checks actual terminal size before rows, changed runs, edge painting, and completion. An interrupted/partial paint returns false; `ConsoleTui` discards prior-frame and mouse-hit state, ensuring subsequent redraw starts clean. Expected console resize/index/IO exceptions are reported to the ActivityLog diagnostics instead of writing a warning above the TUI header. No author source edits, integration, or cutover performed.

## Independently executed evidence

- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter 'FullyQualifiedName~TerminalFrameTests|FullyQualifiedName~TerminalLayoutTests|FullyQualifiedName~TerminalMouseTests'`: **29 passed / 0 failed / 0 skipped**, .NET 11.
- `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~ClientActivityTests|FullyQualifiedName~ActivityLogTests|FullyQualifiedName~ActivityPresentationTests'`: **45 passed / 0 failed / 0 skipped**. Permission-failure messages in stdout came from intentional ActivityLog failure fixtures, not failed tests.
- `git diff --check aa2e46ff2927b8e9fd0af25204377c299f2ec576 8daffe0ca8f7f83abcc2ac8889002ebb7b88ae07`: clean.
- Test source inspected: added fake terminal exercises growth from smaller prior frame, mid-write shrink and retry, injected index exception, 0/1/2/normal dimensions, and background-produced frame updates.

## Explicit residual acceptance HOLD

This review **does not establish** live Windows Terminal / PowerShell 7.6 rapid-resize behavior, preservation of real interactive input/connection during that test, or the **entire** Client.Tests Release suite. The author reports the full suite timed out at 180 seconds; I did not claim an all-tests pass. The injected-terminal tests verify bounded logic but cannot substitute for actual Windows Terminal QA. Retain the task's manual Windows acceptance and complete-suite gate before claiming end-to-end release readiness. No code defect requiring a source change was found in the frozen reviewed scope.
