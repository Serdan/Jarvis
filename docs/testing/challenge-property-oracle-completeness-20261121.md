# Challenge property-oracle completeness — Worker 7 P1 follow-up (2026-11-21)

**Canonical task:** `challenge-property-oracle-completeness-20261121`, attempt `c464dc6e-0944-479c-a612-e30d16c29495`, assignment generation 1.

**Source and review ancestry:** This is a new tests/docs-only branch `refs/heads/test/challenge-property-oracle-completeness-worker7-20261121` created from exact previously rejected frozen source `332020cd50f3a2a3986933763edb8f81251e0b1c`. Its predecessor `challenge-property-model-review-repair-20261121` is Finished, and Reviewer 1's decided ChangesRequested review `06d4e9e1d2c0415d9d55697d0dff61cb` (seq 9827) is immutable. This new branch must have a **new** independent review request, not amend the old frozen source or decision. The original 9000–9003 fault seeds and all 4–20 negative/positive modes are preserved as regression evidence.

## Review findings and repairs

1. **Concurrent CAS losing outcome:** The independently constructed two-clone race must have precisely one accepted write and one **typed** loser (`StaleEntityVersion` or exhausted `LedgerContended`), never a generic `Error _`. Its one canonical operation receipt (UUID, actor, type, request/result hashes, sequence), zero loser operations, old/new HEAD, exactly one sequence increment, correct winning condition, immutable event history and absence of notification/index writes are asserted.
2. **Per-class evidence, not aggregate counters:** The reference model generates 192 deterministic seeds × 56 steps and a fixed preamble that forces all action types into their actual preconditions, including manual and automatic N→N+1 requeue, multi-dependency waiting, a partially satisfied manual rejection, unauthorized condition owner, stale versions, canceled dependency, and unchanged/changed UUID replay. Counts are keyed by the **materialized operation class** (including its Replay and Rejected outcomes), with separate explicit Retry diagnostics; each of the seven mixed mutation classes must exercise accepted, rejected, *and* replayed paths. Create UUID replay/mutation is independently exercised in the reference Challenge model.
3. **Canonical task/assignment/audit oracle:** The sampled bare-Git fixture uses two real clones and a real disposable bare remote. At every initial and mixed step, an independent transition model predicts task phase, owner, version and generation. From the fetched canonical commit, the test directly parses raw `tasks/{task}/lifecycle/{seq}.json` and `tasks/{task}/attempts/{attempt}/assignments/{generation}.json` files, comparing exact **path set, sequence, UUID, actor, action, reason, old/new phase, old/new generation, previous generation, owner, issuer, issued sequence and source/spec pins**. The canonical `spec.html` blob OID and attempt's source identity fields must equal the original assignment. Every historical lifecycle and issuance blob is compared byte-for-byte with earlier snapshots. Rejected and replayed commands cannot alter Git HEAD, `NextSequence`, reverse-index path sets or these immutable records.
4. **Additional identity/canceled-dependency modes:** Extend the existing separately classified fault matrix with wrong `LedgerId`, wrong epoch, unapproved remote name, and two real multi-dependency canceled cases (other dependency still pending, and other dependency satisfied). Both cases require typed stale rejection, unchanged canonical HEAD/sequence/wait bytes and unchanged owner/by-challenge index paths and generation. The prior 4–20 matrix cases are not weakened and known-red production probes are never included in a green filter.

The reference and shrinker keep fixed command numbers and operation UUIDs when reducing failures, preserve their original typed counterexample category, and bound trials/real bare-Git fixture size. Test implementation must not change `Client/Modules/ChallengeWorkflow.fs`, `Client/Modules/ReviewWorkflow.fs`, Scheduler, Health, Worker 4's production correction, Worker 6's adapter, main, real project ledger or deployments.

## Execution commands and evidence classification

Build the **isolated** source with SDK 11.0.100; a fresh worktree requires NuGet restore before `--no-restore` builds:

```sh
dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources
dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore -v minimal
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MixedChallengeAttemptPropertyTests" --verbosity minimal
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ConcurrentTwoCloneWritersAcceptAtMostOneVersionWinner" --verbosity minimal
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault" --verbosity minimal
```

**Separate, intentionally red, production-facing probes** (not green CI gates):

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultTests" --verbosity minimal
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests" --verbosity minimal
```

Do not claim all tests pass if fail-closed production index faults remain red. Prior Reviewer 1 evidence on frozen `332020cd...`: green broad Challenge filter **47 passed**; exact original negative filter **1 passed, 3 failed** (9000–9002 deliberately red); expanded 4–20 matrix **6 passed, 11 failed**. These prior counts are **not** evidence for this new source. Report the new counts, source SHA, exit statuses and specific failure modes only after executing these commands.

**Exact-source provenance:** Tests' embedded source lookup is `git rev-parse HEAD`. Prior to a new commit it still names the ancestor; such runs are provisional. For independent review, freeze a NEW commit containing only this test module and this document, then reexecute selected Release tests against that frozen SHA and submit an independent review tied to this canonical task. Preserve the old worktree, review requests and frozen source commits.

## Candidate execution evidence (precommit worktree)

All commands below ran in the new isolated tests-only worktree on SDK **11.0.100**, `Release`, after `dotnet restore`; the Release build exited **0**, with 0 errors and 3 warnings in the most recent incremental build (warnings in unrelated preexisting test files). As required, these candidate tests still label `git rev-parse HEAD` as the **ancestor** `332020cd...` because the new changes are not committed yet.

| Exact scoped filter | Exit | Passed | Failed | Measured observation |
|---|---:|---:|---:|---|
| `GeneratedTaskAndChallengeCommandsExercisePositiveAndNegativePaths` | **0** | 1 | 0 | 192 × 56 generated commands; explicit seven-class accepted/rejected/replayed assertions |
| `ConcurrentTwoCloneWritersAcceptAtMostOneVersionWinner` | **0** | 1 | 0 | Three real bare-Git CAS races; typed losing error and immutable winner receipt |
| `GeneratedTwoCloneDependencyTracesMatchTaskWaitGenerationAndIndexModel` | **0** | 1 | 0 | Four 56-step real two-clone traces; raw lifecycle/issuance record oracle; **1m57s** |
| `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault&FullyQualifiedName!~GeneratedTwoCloneDependencyTracesMatchTaskWaitGenerationAndIndexModel` | **0** | 46 | 0 | Other non-red Challenge tests, **1m41s** |
| `ChallengeReverseIndexFaultTests` | **1** | 1 | 3 | Original unmodified seeds 9000–9002 red, 9003 passes |
| `ChallengeReverseIndexFaultMatrixTests` | **1** | 11 | 11 | Previously known fail-open **modes 4,5,6,7,8,9,11,15,16,18,20** remain red; all five new modes 21–25 pass |

For the expanded matrix, mode 21 rejects wrong ledger ID, mode 22 rejects wrong ledger epoch, mode 23 rejects unapproved remote/ref binding, and modes 24–25 reject manual requeue after one of two dependencies is canceled while the other is respectively pending/satisfied. Both partial cancellation probes additionally compare the exact canonical blocked-wait blob, owner and both challenge-index path lists, generation and immutable remote HEAD.

**Status boundary:** the green candidate gates can only be claimed individually as listed; the original and expanded fault matrices are deliberately red on this prior source ancestry pending independently reviewed production corrections. Run the exact full broad green filter against the **new frozen commit** after commit; do not substitute the sum of previous filters for an executed broad gate. Record the new SHA and submit the new review before completing the canonical assignment.
