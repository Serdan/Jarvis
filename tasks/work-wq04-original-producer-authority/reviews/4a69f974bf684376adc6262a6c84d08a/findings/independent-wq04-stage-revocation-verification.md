# Independent Reviewer 2 — original WQ04 review/integration graph revocation

**Review:** `4a69f974bf684376adc6262a6c84d08a` (claimed seq 5746)  
**Task:** `work-wq04-original-producer-authority`  
**Exact frozen SHA:** `2670e6a2f10059ad80c35f1cd175b068d7b87e7b`  
**Correction of:** Reviewer 4 ChangesRequested seq 5688, review `5fd240db69f24a219e7cb75ac8c418d9`, frozen `27ccbfed6252948aed645ebaa109a601af7a2b26`.  
**Baseline:** `d356d14c46eef89af4522c05411f25dfd6c10065`.

## Independently inspected source

- Local checked-out HEAD equals frozen `2670e6a2...`; branch/worktree clean before and after tests; original rejected frozen `27ccbfed...` is an ancestor. Full baseline-to-frozen `git diff --check` exits 0. SDK `dotnet --version` = **11.0.100**.
- `Server/WorkQueueProducers.fs:100-114`: genuine producer IDs are `wq04-<kind>-...-g<generation>`, consistently recognized by correction's origin-scoped gate.
- `Client/Modules/WorkQueueReceiptProducer.fs:135-204` and `Client/Modules/WorkQueueGitLedger.fs:1074-1182`: ordinary TaskCreated/review/integration producer resolves canonical original source and versioned role, and writes queue record, immutable original origin and composition link in the winning Git project CAS. It reserves **no** WQ07 account credit.
- `Client/Modules/WorkCompositionGitLedger.fs:194-267,379-513`: `planOriginalStage` binds Planning/FrozenReview/Integration to immutable feature, spec, generation and context. Correction extends `queueRevocations` to original WQ04 review and integration, checking pre/post graph against immutable link; relevant Available or Blocked stages gain `CompositionHold` in the **same** graph+queue CAS, stale claimed/live stages reject the graph mutation before any partial write. Irrelevant graph changes do not mutate the queue; unbound legacy original stages fail closed with an actionable conflict.
- `Client/Modules/WorkQueueGitLedger.fs:802-815`: structural validation now permits `CompositionHold` for original review/integration only, preserves Blocked state and excludes independent queue kinds. Source patch touches only WorkCompositionGitLedger, WorkQueueGitLedger and the canonical-CAS test fixture; no WQ07, STOP, Scheduler, cutover or hosted Actions changes.

## Actual independent tests

Ran **on the exact frozen checkout**, not a previously built claim:

```
dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --no-restore --filter "FullyQualifiedName~WorkQueueCanonicalCasBinderTests|FullyQualifiedName~WorkQueuePracticalAdmissionTests|FullyQualifiedName~WorkQueueIntegrationSourceAuthorityTests|FullyQualifiedName~WorkCompositionQueueTransactionTests|FullyQualifiedName~WorkQueueReleaseAcceptanceTests" --logger "console;verbosity=normal"
```

NUnit net11.0: **47 discovered, 47 passed, 0 failed; exit 0** (Job `2cf76a312df0430ab7abb9ab8f976de7`). Includes ordinary original receipts/role/source CAS, two-clone review Available-to-Blocked (spec, ancestor context, cancellation, linked task), integration Available-to-Blocked, immutable same-CAS receipt path, duplicate UUID replay, dependency wake blocked from resurrection, claimed review and claimed integration rejection with no partial CAS, unrelated change preservation, and WQ07-adjacent account/claim/source/STOP safeguards. Compilation emitted only warnings.

## Scope and decision

**Accepted.** The prior P1 original FrozenReview/Integration revocation hole is addressed at the authoritative mutation boundary with targeted negative coverage. The claim/launch fixtures are hermetic and in part deliberately inert; acceptance of this correction is **not** a production Scheduler/cutover authorization. WQ07 global capacity and STOP remain separate, default-off. Independent review only: no source edits, merges, hosted Actions or Codex.
