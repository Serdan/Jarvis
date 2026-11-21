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

## Review corrections and reproducible .NET 11 Release filters

This document now distinguishes **the prior frozen review source** from the follow-up test-only repair. The original source `cd895d493b419e127ededea15f814ab53e484407` and Review 1's decided ChangesRequested `a17c4d9f4e584c449c0876a205e317bb` remain immutable. The separate follow-up task `challenge-property-model-review-repair-20261121` has its own isolated branch `refs/heads/test/challenge-property-model-review-repair-worker7-20261121`; its **exact source SHA is the frozen review's ReviewedHeadOid**, not the old SHA and not the current main branch.

Reviewer 1 independently executed the original source with .NET SDK 11.0.100 Release:
- The previously documented `FullyQualifiedName~ChallengeStateMachinePropertyTests` filter **did not select only two tests**. It selected 9, exited **1**, with **6 passed / 3 failed**, exactly the known red production seeds 9000, 9001, 9002.
- `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFaultTests` exited **0** with **45 passed**, excluding only the known-red original fault fixture.
- `FullyQualifiedName~ChallengeReverseIndexFaultTests` exited **1** with **1 passed / 3 failed**. Those are production faults, not a claim that the model test suite was green.

The follow-up introduces a **two-method filter** that unambiguously selects the original fast and sampled Challenge model methods:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~SeededReferenceTracesMaintainConjunctionAndTerminality|FullyQualifiedName~TwoCloneStateMachineTracesMatchCanonicalChallengeAndAudit" --verbosity minimal
```

The generated mixed dependency/attempt reference has its own filter:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MixedChallengeAttemptPropertyTests" --verbosity minimal
```

Use these **separate** production-regression filters, never treating deliberately red tests as a green CI gate:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultTests" --verbosity minimal
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests" --verbosity minimal
```

A broader Challenge green-candidate filter excludes **both** original and expanded reverse-index corruption fixtures:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault" --verbosity minimal
```

The follow-up's model generates 192 seeds × 44 mixed commands (in addition to original 256 × 80 Challenge commands), and sampled genuine two-clone bare-Git traces with independent model-versus-canonical task/agent/assignment/wait/index checks. Replay assertions check immutable receipt sequence/request/result hashes; every rejected/replayed operation checks canonical remote HEAD, sequence, and reverse index paths. The reference model's rejected actions have typed failure families; shrinking preserves the same structured failure category and treats fixture/Git exceptions as infrastructure failures. The index matrix adds missing/extra edges, orphan/stale identities, duplicate persisted dependency, cancellation/partial wait, identity mismatch, after-auto and before-manual corruption, and an **expected-positive** valid historic N→N+1→N+2 control. Original red mode 0/1/2 assertions remain unchanged. Any *new* red matrix fixture must be separately classified as a production defect or a test implementation problem; do not report this branch as fully green before confirmed fixes and independent review.

## Execution results / provenance

Execution environment: **.NET SDK 11.0.100**, `net11.0`, Release. A fresh isolated checkout first required `dotnet restore Client.Tests/Client.Tests.fsproj`; no restore failures or external production state was involved. `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore -v quiet` exited **0**, with no compiler errors. The final source contains only tests and documentation; no source files owned by Worker 4, 5 or 6 were edited.

Executed precise filters on the **follow-up candidate source** with `--no-build --no-restore` after each Release compile:

| Group / filter | Exit | Passed | Failed | Meaning |
|---|---:|---:|---:|---|
| Two-method `SeededReferenceTraces...|TwoCloneStateMachineTraces...` | **0** | **2** | 0 | Fast independent Challenge model plus sampled real bare-Git canonical/audit/receipt oracle |
| `FullyQualifiedName~MixedChallengeAttemptPropertyTests` | **0** | **2** | 0 | Generated mixed Challenge/task/attempt/owner/assignment/dependency/generation reference and two-clone runner |
| `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault` | **0** | **47** | 0 | Broad existing and new non-red Challenge regression filter (2m13s measured) |
| `FullyQualifiedName~ChallengeReverseIndexFaultTests` | **1** | **1** | **3** | Unchanged prior production regressions: seeds 9000/9001/9002 red; seed 9003 green |
| `FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests` | **1** | **6** | **11** | Additional deliberately fail-closed production probes, 17 total (24s measured) |

**Exact failing expanded matrix modes:** 4 missing by-challenge; 5 missing by-owner; 6 unlinked challenge extra edge; 7 extra foreign-owner edge; 8 nonexistent challenge edge; 9 stale attempt identity; 11 duplicate persisted canonical dependency; 15 extra owner edge before manual requeue; 16 extra future-generation edge after auto requeue; 18 wrong-owner payload; 20 extra owner edge after a valid manual zero-dependency requeue. In these cases the fixture expected an integrity rejection while the existing production reader/mutator returned `Ok`, and the remote canonical commit was captured before/after the operation. These are deliberately **red, actionable production-side fail-open findings** for the independent Worker 4 correction to investigate; this tests-only task does not repair them.

**Passing expanded modes:** 10 stale generation zero; 12 canceled dependency cannot requeue; 13 wrong project binding; **14 valid historical N1→N2→N3 requeue control**, retaining historical edges; 17 wrong generation field; 19 wrong task locator. The positive historical control must remain green after production fixes. The original four-mode fixture for seeds 9000–9003 was checked byte-for-byte against frozen `cd895d493b419e127ededea15f814ab53e484407` and remains unchanged.

The two clean model filters and 47-test broad filter are the only **green gates**. The two fault filters are **expected red** and remain merge blockers until separately owned production fixes are reviewed. Do not call the whole suite green, silently disable fault cases, or confuse test-harness errors with production-fail-open evidence. No full unrelated project-wide suite was run.

**Exact-source rule:** The test file resolves `git rev-parse HEAD` at execution. Before this follow-up is committed, that reports the old ancestor `cd895d...` even though the worktree contains changes; such precommit runs are candidate evidence only. The review request freezes the new immutable `headOid`, and the final post-commit rerun must be recorded against that exact SHA. There is no self-referential commit SHA embedded in this Markdown file.

## Constraints

The two clones and bare repo are created in a unique OS temporary directory per sampled trace and deleted in a `finally` clause. No notification writes are permissible. Assertions inspect actual canonical records, events, reverse index paths and ledger receipts rather than only presentation. The test file is a bounded fixture owned entirely by Worker 7; it does not touch `Client/Modules/ChallengeWorkflow.fs`, `Client/Modules/ReviewWorkflow.fs`, or parallel Worker 1/5/6 leases.
