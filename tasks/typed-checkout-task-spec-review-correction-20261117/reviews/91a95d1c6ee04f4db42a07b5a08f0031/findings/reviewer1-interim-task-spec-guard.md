# Reviewer 1 — independent P1 checkout guard review

**Request:** `91a95d1c6ee04f4db42a07b5a08f0031`  
**Frozen source:** `b67c155688b650a6a12f8dfecbe37393e1d3bbf7` on `feat/incremental-agent-instructions`; prior rejected source `36a7d5dc86ce1b2371f21ff46dbfb9bcfa2aef19`, decision 7236; base `efe769e06b34451e007198a050d227947fd83410`.  
**Scope:** independently review the fail-closed **interim** task-spec protection only, as permitted by the follow-up task's option B. Do not conflate this with full multi-worktree task-spec support or acceptance of the old rejected parent.

## Findings

1. **Interim safety gate is in the correct entrypoints.** `Client/Modules/OrchestrationApi.fs:1244-1266` rejects `ExportTaskSpec` and `ImportTaskSpec` for a typed `ProjectTarget.Registered` before `writeUsingContext`, thus before ledger binding, source-file IO, and pending-intent journal creation. `writeForContext` gates the validated context target; legacy `write` parses and gates the command selector. `Client/SignalR/Client.fs:404-407` sends task-spec source operations only through the prepared selected context and rejects missing resolution. `Client/Modules/ProjectPaths.fs:25-46,254-269` represents the selected checkout distinctly and resolves it from Git-registered worktrees.

2. **Regression coverage addresses the actual corruption path.** `Client.Tests/ReviewWorkflowTests.fs:1986-2032` creates two Git-registered worktrees with differing HTML at the same relative path, executes both actions through `TaskApi.writeForContext` and legacy `TaskApi.write`, asserts a non-main validation rejection, and checks that both selected content and main-collision content remain unchanged and the canonical ledger head is unchanged. The preceding positive main-checkout import/export checks at lines 1925-1984 remain. This exercises the interim rejection, not positive non-main IO.

3. **Frozen source and independent execution.** The correction commit changes only `Client/Modules/OrchestrationApi.fs` and `Client.Tests/ReviewWorkflowTests.fs` relative to rejected parent, avoiding `LedgerStore.fs` and `ReviewWorkflow.fs` regions reserved under Worker 21's lease. Git confirmed the rejected parent is an ancestor and the reviewed worktree is clean at the exact frozen head. Independently ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **393 passed, 0 failed, 0 skipped** (.NET 10, 1m24s); `git diff --check efe769e06b34451e007198a050d227947fd83410 b67c155688b650a6a12f8dfecbe37393e1d3bbf7`: clean.

## Scope limits / disposition

**Accepted for the interim fail-closed guard only.** Non-main task-spec import/export remains intentionally unavailable. Separate selected source-root propagation, exact physical source/ref provenance, CAS retry and symlink/path validation for *positive* worktree IO, and its end-to-end tests are **not implemented or accepted** by this verdict. Those require the explicitly held follow-on ownership/lease and another independent review. No merge, live activation, cutover, deployment or native Windows acceptance is implied.
