# Challenge state-machine property tests — 2026-11-21

Task: `challenge-state-machine-property-tests-20261121` (Worker 7, attempt `70978b37-0498-445d-aa16-1fed8bf38c56`, assignment generation 1).

Isolated source: `refs/heads/test/challenge-state-model-worker7-20261121`. Source baseline: `d702352ef705f9991ac1cb14dc15a13cce78aa44` on `refs/heads/main`. No production source, main branch, scheduler activation, deployment, or real project ledger changed.

## Implementation and oracle

`Client.Tests/ChallengeStateMachinePropertyTests.fs` provides a domain-only reference state: a Challenge is a set of Waiting, Obstructed or Done conditions, plus a terminal Cancelled flag, version and operation receipt map. The reference model has no imports of persisted Challenge types, no copy of workflow CAS/index code and no state derived from the system under test. One owner per condition (Lead or Agent 8), issuer Agent 3 and a foreign actor are modeled explicitly. Completion is conjunction over Done conditions; obstruction takes precedence over incompleteness. Terminal satisfied/cancelled Challenges cannot transition again.

A built-in `System.Random(seed)` generates valid and invalid create/resolve/block/reopen/cancel/invalid actor/stale version/duplicate UUID operation/payload-mutation actions. Seeded operation IDs and Challenge IDs derive from SHA-256. Commands retain fixed logical numbers for faithful replay. On failure, bounded delta deletion shrinking tries up to 64 candidate reductions and reports original and minimal commands, seed, source SHA (resolved from the test checkout at runtime), modeled expected versus actual result, and canonical record state. The shrinker is intentionally local, not a general framework. NUnit/FsUnit were already installed; FsCheck was **not** in the project and no new dependency was added.

## Execution tiers and invariants

1. **Fast model tier:** 256 seeds × 80 commands = 20,480 generated commands. Checks receipt replay uniqueness, stable versions, conjunction and terminality, rejected writes leaving modeled state unchanged.
2. **Sampled real Git tier:** three fixed seeds (`0xC0FFEE`, `0xBADA55`, `0xA11CE`) × 22 commands each, using a genuinely disposable bare Git remote and two clones. Alternate clones write and re-fetch ledger state after every step. Check independently modeled acceptance/rejection/replay, actual challenge versions and condition statuses, immutable audit count and increasing unique sequence, and zero notification files.
3. **Dependency tier:** seeds 44–47 cover empty manual dependency, all-satisfied automatic wake, cancelled partial dependency and blocked-condition reopen. Check exact blocked attempt/generation identity, duplicate normalized dependency rejection, foreign actor denial, canonical dependency lists, reverse by-owner/by-challenge path sets, no notifications and **only N→N+1** requeue. Historical N indices intentionally remain present after valid requeue and are not discarded as corruption. Stale/duplicate requeue is denied.
4. **CAS tier:** three distinct seeds (31–33) race two clones resolving different conditions at the same Challenge version. Exactly one write may succeed; canonical remote version, condition statuses and two audit events must agree, with no notifications.
5. **Adversarial integrity tier:** four separate one-mutation **real bare-Git** fixtures corrupt by-challenge or by-owner edges after a valid N=1 block. These tests are expected to remain red pending separately scoped production correction; they are isolated from the green test filter. They do **not** use a mock ledger or alter production state.
6. **Immutable receipt tier:** seeds 77–79 replay create and resolve UUIDs from the other clone. Compare the original operation sequence, request hash, result hash and stable created identities; a payload-mutated UUID must fail without advancing the remote HEAD, challenge version, event history or notification tree.

## Reproducible production findings (source baseline)

All corruption cases have the minimal logical trace: (1) register agents and create task/assignment N=1; (2) create a single-condition Challenge; (3) block assigned attempt on it; (4) inject exactly one adversarial index record into the disposable canonical Git ledger via `LedgerStore.transact`; (5) resolve Challenge or read inbox and assert integrity rejection. Test case integer is the stable fault seed `9000 + mode`; UUID values of unrelated ephemeral fixture identities do not affect this reproduction.

- **Mode 0 / seed 9000, FAILED**: add orphan `challenge-waits/by-challenge/{challenge}/{task}/{attempt}/2.json` while canonical wait and attempt remain generation 1. `resolve` accepts the extra N+1 locator, commits Satisfied and issues generation 2 instead of failing closed. Production site: `Client/Modules/ChallengeWorkflow.fs` `wakeDependents` loops index paths, but ignores reverse-edge completeness beyond local identity.
- **Mode 1 / seed 9001, FAILED**: rewrite the N=1 by-challenge locator with `ownerAgentId="Intruder"` while the canonical wait owner remains `Agent 3`. `resolve` returns Ok, writes Satisfied, but silently skips the linked wait because the owner does not match; the dependent remains blocked. The outcome violates exact dependency wake consistency.
- **Mode 2 / seed 9002, FAILED**: add orphan `challenge-waits/by-owner/{owner}/{task}/{attempt}/2.json` without a matching generation-2 canonical wait. `ReviewWorkflow.getAgentInbox` returns Ok instead of an integrity error. Production site: `ReviewWorkflow.fs` inbox projection does not reconcile global reverse indices against canonical active waits.
- **Mode 3 / seed 9003, PASSED**: rewriting an N=1 by-challenge locator with the wrong task ID is correctly rejected with `LedgerIntegrityError`.

Modes 0–2 are intentionally red production-facing regression tests, **not** an assertion that this tests-only branch is merge-ready. Create a separate correction task owned by Lead for canonical reverse-index completeness/fail-closed validation, with an independent source review. Do not silently fix production in this test task, ignore historical valid edges, or weaken the assertions to obtain green CI. Re-run all four corruption cases on the correction branch and verify historical N→N+1 reblocking remains valid.

## Commands and results

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeStateMachinePropertyTests --no-restore --verbosity minimal` — exit **0**, 2/2 passed, .NET 11 Release, 10s test duration.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeDependencyPropertyTests --no-restore --verbosity minimal` — exit **0**, 2/2 passed, .NET 11 Release, 9s test duration.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeReverseIndexFaultTests --no-restore --verbosity minimal` — exit **1**, 1 passed / 3 failed, 4 total, 6s test duration. This is a **confirmed production failure result** (not a fixture-setup failure).
- Broad existing Challenge regression + generated suites (the module-prefix filter also selects the intentionally failing corruption fixtures): **exit 1, 41 passed / 3 failed / 0 skipped, 44 total, 1m20s**, .NET 11 Release. All three failures are the documented modes 0–2; no other failures.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFaultTests" --verbosity minimal` — **exit 0, 45 passed / 0 failed / 0 skipped, 45 total, 1m29s**, .NET 11 Release, includes the immutable receipt property. The explicit exclusion is only for the three confirmed production defects. The frozen review's exact commit SHA is the final source provenance.

## Constraints

The two clones and bare repo are created in a unique OS temporary directory per sampled trace and deleted in a `finally` clause. No notification writes are permissible. Assertions inspect actual canonical records, events, reverse index paths and ledger receipts rather than only presentation. The test file is a bounded fixture owned entirely by Worker 7; it does not touch `Client/Modules/ChallengeWorkflow.fs`, `Client/Modules/ReviewWorkflow.fs`, or parallel Worker 1/5/6 leases.
