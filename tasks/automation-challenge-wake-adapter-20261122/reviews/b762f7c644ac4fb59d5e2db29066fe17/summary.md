# Independent frozen review — Accepted (source-only)

**Review** `b762f7c644ac4fb59d5e2db29066fe17` of Worker 17, exact committed head `c1b202fcc62f54400b8146e90b20d468652aa724`.

## Provenance and lease
- Independently observed clean registered worktree at that head. `git show -s` establishes the **direct parent** `d312f1cc83685e781ce69e57ad0d4bf613bfb843` (accepted-only private composition).
- `git diff --name-status d312..c1b` adds **exactly five** permitted files: `Client/AutomationChallengeWakeAdapter.fs`, `docs/testing/automation-challenge-wake-adapter-20261122.md`, and three NEW files under `tests/AutomationChallengeWake20261122/`. No existing production module, existing test, or .fsproj modified.
- Independent `git ls-tree` confirms **all eight frozen existing source blob IDs** from the contributor's evidence: Client.fsproj `5ba8b9c5`, NativeHealth `4753293d`, ChallengeWorkflow `98101151`, WorkQueueReactiveWake `87f81d55`, WorkQueueSchedulerHostLoop `800a7388`, WorkQueueSchedulerHeadBinding `6218666e`, NativeSchedulerAccountReservation `fd9c9591`, WorkQueueNativePrivateHostBridge `9a2b51f2`.

## Implementation reasoning
- The adapter is **unwired** and disabled by default; inspecting with enabled=false performs no Git read. A caller-supplied locator is never accepted as authorization. A fresh approved project Git fetch must match project/ledger/epoch/head. Validation requires the original `ResolveChallengeCondition` operation and origin, matched request/actor/condition/event, the actual satisfied canonical challenge and all sibling dependencies, original owner/challenge reverse-index records, verified owner inbox, queued attempt at blockedGeneration+1, matching same-CAS assignment issuance/lifecycle, and Ready/stopped owner.
- Inspected real `ChallengeWorkflow.wakeDependents` implementation: final condition satisfaction writes the `Requeued` wait, next-generation `Queued` attempt, durable assignment/lifecycle and Ready owner in the *same* resolving Git operation. The adapter neither recreates that CAS nor independently admits a scheduler Work.
- `deliver` only raises `DependencyCommitted` through `WorkQueueReactiveWake.Coordinator.Wake`. Its real coordinator has its own default-off switch and independently reads account/project heads, capacity, source identity, planner intent and fresh fence. A hint cannot confer Work authority. No registered authenticated Health receipt or production host hook is present.

## Verified process and result evidence
- Inspected the **actual managed** focused Release job `5ae9990de01c4288b1ae3df4283ab1c8`: **Completed, exit 0**, 8 passed / 0 failed / 0 skipped. Independently read `w17-focused-final.trx`: NUnit discovered 8, counters total=8 executed=8 passed=8 failed=0 error=0 notExecuted=0.
- Positive test runs real disposable bare Git remote and two clones through `createTask`, `assignTask`, `beginAttempt`, real `blockAttempt`, real `resolve`, canonical index/readback and Ready generation 2. Negatives include foreign project/ledger, changed head, unresolved challenge, missing reverse challenge edge, forged condition/owner/operation, incorrect generation, CAS loser, and default-off WorkQueue hint. Duplicate resolve idempotence is observed on the real ledger.
- Inspected original RED-first build `945b112f5feb4954ad224a1cce2bbb06`: **exit 1** expected missing adapter source FS0225. Intermediate test job `3439d3d0c72e4972b0669eb97b3792f0` was legitimately RED 5/6 due to diagnostic specificity and then corrected. Separately inspected production-host RED job `038854ddfa844f46913194b17b9842cf`: **exit 1 FS0039** because the adapter namespace is absent from the genuine production Client DLL, deliberately so.

## Strict evidence boundary
**Accepted only as a frozen NEW-only standalone source/test contract.** This is not a production-registered Challenge→scheduler host, registered-Health ingress, provider B2 authorization, a full Client or Server Release pass, main integration, or approval to deploy/activate unattended Work. The suite is focused; no independent additional runtime tests were launched by this reviewer. Not all possible combinations (e.g. a multiple-sibling poison fixture or an enabled Work coordinator with a nonauthorized plan) have dedicated isolated tests; source is fail-closed on those paths and they remain useful future integration coverage, not claims made by this verdict. Full composed integration needs independent review and operator approval.

**Verdict: Accepted**, no blocking finding within this source-only lease.
