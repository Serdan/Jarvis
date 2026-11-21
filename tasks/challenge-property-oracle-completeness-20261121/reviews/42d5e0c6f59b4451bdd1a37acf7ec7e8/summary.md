# Independent Reviewer 4 — ChangesRequested

**Task:** `challenge-property-oracle-completeness-20261121`; **review:** `42d5e0c6f59b4451bdd1a37acf7ec7e8`; claimed Reviewer 4 at seq9878. **Exact tests/docs-only frozen SHA:** `87bbadfe0bac60ec4d14f51d382841076f427d18`, branch `refs/heads/test/challenge-property-oracle-completeness-worker7-20261121`, immutable submitted review ref `refs/heads/jarvis-review/challenge-property-oracle-completeness-20261121/42d5e0c6f59b4451bdd1a37acf7ec7e8/r1`. Verified direct parent `332020cd50f3a2a3986933763edb8f81251e0b1c` (prior Reviewer1 ChangesRequested seq9827); base `d702352ef705f9991ac1cb14dc15a13cce78aa44`; target main. Only `Client.Tests/ChallengeStateMachinePropertyTests.fs` modified and `docs/testing/challenge-property-oracle-completeness-20261121.md` added. Reviewer isolated worktree clean; no author source edits, merge or push.

Read agent/reviewer inboxes, current Reviewer role, root/project AGENTS, canonical new task, prior review summary and all four findings. Independently inspected exact source diff, canonical fixture, Git behavior and .NET11 Release tests.

## Substantive corrections confirmed

- CAS loser test now accepts only `StaleEntityVersion` or `LedgerContended`, requires exactly one winner and canonical receipt/sequence/event concordance, loser receipt absent and no unwanted notification/index writes (`ChallengeStateMachinePropertyTests.fs:654–736`).
- Generated 192×56 model commands now assert applied/rejected/replayed **per actionable class**, with typed stale/unauthorized/reused errors, dependency/generation contexts and separately treated observations; four sampled real two-clone 56-command traces also require per-class coverage (`...fs:1531–1654`).
- Mixed real Git checker reads raw canonical lifecycle/assignment JSON (exact actor/action/sequence/phase/generation/issuer, path sets, spec/source fields) with historical bytes immutable, rejects and replays preserving HEAD/sequence/index (`...fs:1227–1363,1392–1505`).
- Added wrong LedgerId, epoch, unapproved remote binding, and partially canceled multi-dependency cases 21–25. Original 9000–9003 seeds and 4–20 modes retained.

## P1 blocking findings — both recorded as canonical individual findings

**1. `findings/mode15-post-injection-owner-version.md`**. Mode15 still calls `ChallengeIndexFaults.inject` at line1812 **before** `ReviewWorkflow.getAgentInbox` at line1813 supplies request versions, rather than capturing pristine versions first. This violates the precise correction required after independent production Reviewer5 seq9845: prove direct public zero-dependency manual requeue fail-closed independently of a preflight index read. Capture task/attempt/agent versions on clean ledger, inject forged owner index, call direct requeue and assert typed integrity rejection with identical canonical HEAD/sequence/audit/receipts/index. Prior fault remains expected-red, not evidence this oracle is fixed.

**2. `findings/source-provenance-only-null-fixture.md`**. All mixed real Git fixture source pins come from legacy `ChallengeDependencyChecks.assign` → `ReviewWorkflow.assignTask`, whose `BaseCommit`, `TargetRef`, `SourceRepositoryId`, `SourceRemote`, `SourcePushRemote`, `SourceRef` are **all null** (`ReviewWorkflow.fs:1791–1799`). The new exact-JSON comparisons can only prove null→null preservation, not preservation of a real nonempty frozen source identity across manual and automatic N→N+1 issuance. Add independently authored nonempty source-pinned two-clone trace, retaining legacy/null compatibility control.

## Independent exact frozen .NET 11.0.100 Release checks

All `dotnet test` runs used `Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "<below>" --verbosity minimal`. Actual exits/counts:

| Filter or command | Exit | Passed | Failed |
|---|---:|---:|---:|
| `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources` | 0 | n/a | n/a |
| `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore -v minimal` | 0 | n/a | 0 errors (4 unrelated warnings) |
| `FullyQualifiedName~GeneratedTaskAndChallengeCommandsExercisePositiveAndNegativePaths` | 0 | 1 | 0 |
| `FullyQualifiedName~ConcurrentTwoCloneWritersAcceptAtMostOneVersionWinner` | 0 | 1 | 0 |
| `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault` | 0 | **47** | **0** (0 skipped; 3m18s) |
| `FullyQualifiedName~ChallengeReverseIndexFaultTests` | **1** | **1** | **3** |
| `FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests` | **1** | **11** | **11** |

Separate production-failing suites are **intentionally RED on unchanged original production ancestry**, not claimed as green: original deterministic 9000,9001,9002 expect integrity but receive `Ok` (positive 9003 succeeds); expanded modes 4,5,6,7,8,9,11,15,16,18,20 remain red; modes 21–25 all pass. Mode15 still returns `Ok` and changes **disposable** remote HEAD. Original seeds/expected assertions unchanged.

**Separate production status:** corrected Worker4 production `0a596bfb505f50bbf4b56bb93dffa1ac4d64920b` has independently been **Accepted** by Reviewer5 review `41c92e8e4b614ff0ab1c026271fc9b73`, decision seq9882. Its acceptance does not compose or validate this rejected tests-only source; no combined QA or publication authorized.

**VERDICT: ChangesRequested** solely for exact tests/docs source `87bbadfe...`. Repair two test oracle gaps in new isolated tests/docs source, preserve all seeds and expected-red matrices, execute exact source-bound Release checks and submit a NEW immutable independent review. No source edits, main publication, deployment, Scheduler/Health activation, uncertain effect replay or Codex through Jarvis performed.