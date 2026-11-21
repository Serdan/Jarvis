# Challenge direct mode-15 and source-pinned assignment oracle (2026-11-21)

**Task:** `challenge-property-oracle-source-pins-and-mode15-20261121`; Worker 7, attempt `d4be16cf-4115-4b6c-a936-45f916c7b4df` (generation 1). **Scope:** tests and this document only. Branch `refs/heads/test/challenge-property-source-pins-mode15-worker7-20261121` derives from the exact previous frozen tests-only source `87bbadfe0bac60ec4d14f51d382841076f427d18`. The previous Reviewer 4 `ChangesRequested` (request `42d5e0c6f59b4451bdd1a37acf7ec7e8`, sequence 9887) remains immutable. Production repair `0a596bfb505f50bbf4b56bb93dffa1ac4d64920b` was separately Accepted by Reviewer 5 (sequence 9882), but this branch neither incorporates nor changes that production code.

## Direct zero-dependency mode 15

The old matrix read the agent inbox **after** injecting a forged current-generation by-owner index, allowing an index-read preflight to obstruct the intended writer regression. The revised probe obtains the task version, attempt version, assignment generation, attempt identity, and owner version while the disposable canonical ledger is pristine; blocks with zero Challenge IDs; injects the forged owner index only after saving those values; then directly invokes public `ChallengeWorkflow.requeueAttempt` from the other real clone. Only typed `LedgerIntegrityError` is accepted. It checks that rejected operation preserves remote HEAD, NextSequence, the complete Git tree blob listing (all receipts, lifecycle audit, wait and reverse-index files) and explicit owner index paths. This remains expected-red against historical production `48f1a8c6030677960a3d84629c4e29cecf56a132`, and should be green only against the accepted corrected production code.

## Genuine nonempty source-pinned assignment control

The existing generated bare-Git property model remains the **legacy/null control** and continues checking all previously defined outcome families. A separate two-clone test uses real Git repository HEAD, a validated branch ref, the actual approved bare remote endpoints and an independently recomputed SHA-256 source repository ID; then calls production public `ReviewWorkflow.bindAttemptSource` instead of changing canonical records directly. Production `assignTask` issues generation 1 *before* source binding and genuinely records null source fields. The oracle verifies this historical generation-1 issue is immutable; it does not pretend that nonempty source pins existed before they were bound.

Generation 2 is issued by public manual requeue with nonempty source pins, generation 3 by automatic Challenge satisfaction, and generation 4 by a further public manual requeue. The test independently parses raw canonical attempt and generation-specific assignment JSON, asserts source `baseCommit`, `targetRef`, `sourceRepositoryId`, `sourceRemote`, `sourcePushRemote`, `sourceRef`, `specBlobOid`, attempt/task/owner identity, generation, previous generation, and monotonic issuance sequence. Every historical issue is retained and compared byte-for-byte across state transitions, failed stale-version/CAS requests, identical UUID replay and changed-payload UUID rejection. The two clones alternate writes and deliberate unrelated remote HEAD advancement. Rejections and replays preserve canonical HEAD/sequence/tree.

Original deterministic seeds 9000–9003, expanded fault modes 4–25, typed CAS loser, per-class generated outcome coverage and category-preserving shrinking have not been removed or weakened.

## Verification contract

Use the isolated test source under .NET SDK 11.0.100. Execute real OS Release `dotnet restore`, `dotnet build`, and exact filtered `dotnet test --no-build --no-restore` commands. Report *actual* process exits and pass/fail counts, separating green property tests, known-red original production fault tests and disposable accepted-production overlay. The independently Accepted production source is not part of this tests-only commit; any mechanical composition for QA must take place in a new disposable worktree without amending either accepted or frozen commit. Mode 8, when the accepted writer cannot discover an unrelated unknown Challenge-prefix edge, is a separately classified bounded-index scope limitation; it must never be reported as a general green matrix or used to weaken other assertions.

No publication to `main`, production Git, deployment, Scheduler/Health activation, uncertain external-effect replay, or Codex-through-Jarvis execution is authorized.

## Executed tests-only source checks (pre-freeze, exact ancestor production)

All commands were executed in the new isolated worktree under SDK `11.0.100`, `Release`, after successful restore. The source file edits are still uncommitted at this checkpoint, so the test helper's `git rev-parse HEAD` reports parent `87bbadfe0bac60ec4d14f51d382841076f427d18` rather than a new frozen SHA. These are provisional execution measurements, not a claim of frozen-source verification.

| Exact filtered Release check | Exit | Passed | Failed | Classification |
| --- | ---: | ---: | ---: | --- |
| `TwoClonePublicBindingPreservesSourcePinsThroughManualAndAutomaticRequeue` | 0 | 1 | 0 | Genuine public nonempty source binding and raw immutable four-generation assignment oracle |
| `SourcePinnedChallengeAssignmentPropertyTests\|GeneratedTaskAndChallengeCommandsExercisePositiveAndNegativePaths\|ConcurrentTwoCloneWritersAcceptAtMostOneVersionWinner` | 0 | 3 | 0 | Pin control, generated per-class oracle, typed two-clone competing-CAS loser |
| `ChallengeReverseIndexFaultMatrixTests&Name~15` | 1 | 0 | 1 | **Expected red on unmodified original production:** direct manual requeue returned `Ok` and advanced disposable canonical remote HEAD, instead of typed `LedgerIntegrityError` |
| `ChallengeReverseIndexFaultTests` | 1 | 1 | 3 | Original seeds 9000–9002 deliberately red, positive seed 9003 green |

The corrected mode 15 specifically failed with `actual=Ok` and two different disposable remote HEADs, demonstrating it reached the direct public writer instead of failing in corrupted-index preflight. This is not a production QA pass. Separate frozen-source and accepted-production mechanical overlay evidence is recorded with the independent review submission, not assumed from these provisional results.

The complete provisional green filter `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault` also exited **0**, with **48 passed, 0 failed, 0 skipped** (3m16s). The complete unfixed-production matrix `FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests` exited **1**; the runner's bounded output was truncated before its final count, so this document does not fabricate that aggregate. It includes the explicit mode-15 fail-open result. The full suite is intentionally not claimed green against original production.
