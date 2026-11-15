# Reviewer 2 — independent exact frozen review

**Verdict: Accepted** — for the **inert WQ-07 account-global Git-CAS foundation and F01/F02 corrections only**.

- Review: `d4e8d63a6b684d7e9bea5b32654937c2`
- Exact frozen source: `e72650db82f883ad15108b9d83d7cdfa28efaff5`, frozen ref `refs/heads/jarvis-review/wq-parallel-account-cas-foundation/d4e8d63a6b684d7e9bea5b32654937c2/r1`
- Base: `90ed868d04524d0483ccd979cffb5029d8cf7027`
- Prior changes-requested review: `c76f54834dbe4b179ed775f1912821fd`, exact `75c2dd887e119315373aff66bbe4acc852fe4656`
- Scope: five added/updated files, 1,349 additions: `Client/Modules/AccountCapacityLedger.fs` (611 lines), `Client.Tests/AccountCapacityLedgerTests.fs` (626 lines), design doc (110 lines), and two explicit .fsproj includes.

## Independent evidence

1. Inspected the **full exact frozen** account-ledger module and all 14 fixture tests, the complete foundation design note, accepted `docs/specs/work-queues-and-ephemeral-executions.md` clauses for two-ledger authority/C01/§§7-8, and project `LedgerStore.putRecord`/immutable operation origin semantics at the same SHA. Verified the correction worktree remains clean at the exact reviewed commit.
2. Ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~AccountCapacityLedgerTests --no-restore` independently in `worker4-wq-account-cas-corrections` on .NET 11: **14 passed, 0 failed, 0 skipped**, exit 0, six-second test execution. Existing warnings FS0760 (Program.fs) and FS3369 (BrowserSchedulesTests.fs) are outside this source change.
3. No source changes, branch merges, deployments, account migration or Scheduler wiring performed by reviewer.

## Previous findings resolution

**F01 (P1): resolved for the disposable project-proof adapter.** `AccountCapacityLedgerTests.fs:93-220` now writes both original `AdmissionWon` and `AbortOnlyUnbound` to the same `records/launch-{launch}.json` path with `ExpectedBlobOid=None`. `LedgerStore.putRecord` checks that shared path against the fetched project head, and stale push retry rechecks the expected absence (`LedgerStore.fs:968-1047`). The verifier resolves the current project ledger, verifies terminal operation's immutable origin/current blob and the admission/abort decision, and requires the original admission operation to be absent when certifying abort-only terminality. The abort-first / late admission after account reuse, admission-first / rejected abort, exact UUID replay and ordinary terminal tests check the relevant fences (`AccountCapacityLedgerTests.fs:431-560`). The original different-path/absence-only release race no longer exists in this fixture.

**F02 (P2): resolved.** `AccountCapacityLedger.fs:108-119,172-211,449-519` adds dynamically scoped Git transport faults and a before-lease-push interposition without production activation. The accepted Git push / lost acknowledgment / failed post-push fetch test (280-316) verifies `AccountOutcomeUnknown` then immutable original result on exact UUID replay, with one token. The two-clone reserve/reserve (319-349) and reserve/capacity-shrink (393-428) tests force both writers to reach the **same parent head before either push** and verify the loser replans/fails without overcommit. Unverifiable release transport (351-390) retains the token and denies another reserve until exact release reconciliation.

## Additional architecture checks

- One separate account Git authority `refs/heads/jarvis-account-admission`, independent from project task/launch authority; bind account+ledger+epoch and approved fetch/push endpoints, fetch current remote and protect trusted ancestry. Account writes use a precise old-head `--force-with-lease`; conflict repeats the verified read/plan, not stale mutation.
- Stable operation UUID plus canonical full semantic request and immutable original-result receipt. Replay performs full request equality; receipt read checks hash, original first-parent addition/blob, and origin commit trailers. Duplicate launch IDs cannot create tokens; Released tombstones remain. Held occupancy includes all logical uncertain launches.
- Capacity shrink and reservation share the same CAS; stale configuration or shrink below held tokens rejects; unknown write outcomes never constitute success or safe release.
- Release requires a trusted project-verifier adapter and exact launch/project/admission/terminal IDs, matched immutable terminal evidence and a single held-to-released account state transition. Mere caller assertion or missing project operation cannot release in the corrected test model.
- `LegacyActive` initialization has **no production activation**, scheduling, migration or browser side effects. The production WQ-02/activation adapter is deliberately outside this review.

## Nonblocking but mandatory production follow-ups

See `findings/integration-boundaries.md` for exact boundaries: use independently accepted WQ-01/WQ-02 state machines and operator migration gates, verify distinct global-reservation versus project-admission operation IDs, and enforce protected account-ref writer policy/transition provenance. Passing these 14 disposable tests does **not** authorize deployment, WQ-07 rollout, or integration with unavailable adapters.

**Decision:** Accept corrected exact frozen source `e72650db82f883ad15108b9d83d7cdfa28efaff5` within stated inert-foundation scope; no remaining blocking F01/F02 findings.
