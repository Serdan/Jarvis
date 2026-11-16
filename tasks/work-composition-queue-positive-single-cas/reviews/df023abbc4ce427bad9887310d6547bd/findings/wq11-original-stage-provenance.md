# WQ11 retained vertical-slice fixture: invalid retroactive original WQ04 link

**Exact reviewed correction:** `9602a85acde4679efd6f555aceb00c20ff67a196` from `e2830bc9a158f83f4a467d9f68ac45903f69c70d`.

## Reproduction on old e283

WQ11 `PinnedCompositionAndWq02ViewExposeMissingWorkToSchedulerBindings` produced `dependent-task` with `WorkQueueReceiptProducer.produce` **before** creating the composition graph. The original stage therefore had no `records/work-composition/stage-links/<stage>.json` birth association. The test then called ordinary `WorkCompositionGitLedger.execute` with `LinkedTaskId = Some "dependent-task"`. Canonical `queueRevocations` rejected it as `InvalidBinding "WQ02 stage lacks immutable original composition binding; reconcile before graph mutation."`. Previous broad Client run: **251 passed / 1 failed**; exact failure in this fixture, not a production bypass.

## Correction without weakening guards

- Keep the graph's `dependent` unit `LinkedTaskId = None` because the original WQ04 planning stage predates the graph; the graph's hard prerequisite remains independently Blocked.
- Add an explicit rejected `Revise("dependent", ..., { Spec with LinkedTaskId = Some "dependent-task" })` against the original `b1` client. Assert the missing immutable link `InvalidBinding` and unchanged `b2` verified original Git head.
- Existing negative assertions still require `ClaimEnabled=false`, `RejectedMissingBinding`/`Applied=false` for ClaimNext, `CanDispatch=false` and no Scheduler/provider execution. The stage can read as Available precisely because it has **no graph binding**, but availability cannot grant execution authority.
- No edits to `Client/Modules/WorkCompositionGitLedger.fs`, immutable stage birth writer, duplicate task ID, pagination, `Client.Tests.fsproj`, Worker4's fence files or Worker8 permissions.

## Independent verification checklist

Confirm new commit has single changed fixture path and frozen parent `e2830bc9` (which itself has accepted `2dd803b6` as exact parent); rerun local .NET 11 Release WQ11 1/1, focused two-clone 68/68, broad `WorkComposition|WorkQueue` 252/252 and full Server 295/295; `git diff --check` and clean tree. Report reviewer decision for **this** exact snapshot separately. Older review `9e1ca754` remains non-Accepted pending its original ChangesRequested reconciliation. This is **not** operational Work/Scheduler/migration GO.
