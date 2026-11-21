# Independent Reviewer 2 — Accepted

**Canonical task:** `challenge-property-oracle-source-pins-and-mode15-20261121`  
**Review:** `4d7ca26879ed4d349c6e9e2d907e220d` (claimed by Reviewer 2 at sequence 9944)  
**Exact frozen source:** `9c71f86380754f6f9bb1758fdec38171f99c0959`  
**Immutable review ref:** `refs/heads/jarvis-review/challenge-property-oracle-source-pins-and-mode15-20261121/4d7ca26879ed4d349c6e9e2d907e220d/r1`

## Scope and provenance

I independently read the pinned task, Reviewer role v3, root/project AGENTS.md, Reviewer 4's prior ChangesRequested review `42d5e0c6f59b4451bdd1a37acf7ec7e8` (decision 9887), and its two findings. The new commit's *direct parent* is prior frozen rejected tests-only `87bbadfe0bac60ec4d14f51d382841076f427d18`, based on `d702352ef705f9991ac1cb14dc15a13cce78aa44`. This successor changes only `Client.Tests/ChallengeStateMachinePropertyTests.fs` and adds `docs/testing/challenge-property-source-pins-mode15-20261121.md`; no production implementation is edited. Frozen worktree remained clean. `git ls-remote origin` independently resolved the immutable review ref to exactly `9c71f86380754f6f9bb1758fdec38171f99c0959`.

Accepted production `0a596bfb505f50bbf4b56bb93dffa1ac4d64920b` was independently verified as Reviewer 5 **Accepted** in review `41c92e8e4b614ff0ab1c026271fc9b73`, sequence 9882. I did **not** assume its acceptance applied to these tests.

## Adjudication of the two predecessor defects against the original task

1. **Mode 15 direct requeue oracle — corrected.** In mode 15, `getTask` and `getAgentInbox` obtain clean task, attempt, owner, generation, and version information **before** forged current-generation by-owner index injection (test lines 1811–1826). No post-injection inbox read intervenes before the public zero-Challenge-ID `ChallengeWorkflow.requeueAttempt` invocation using those exact versions (1834–1837). `checkRejected` accepts only typed `LedgerIntegrityError` and rejects remote HEAD advance. The explicit after-check preserves `NextSequence`, entire Git `ls-tree -r` listing (all audit, receipt, wait and index blobs), and the by-owner path set (1827–1850). This exercises the intended direct writer rather than an earlier reader.

2. **Nonempty source-pinned issuance — corrected.** The added independent disposable bare-Git/two-clone fixture obtains a real source branch ref with `symbolic-ref HEAD`, a concrete base commit with `rev-parse HEAD`, actual approved fetch/push endpoints and SHA-256 repository identity. It uses public `ReviewWorkflow.bindAttemptSource`, whose production implementation verifies branch, base object, ancestry and target. Its initial legacy generation-1 issue remains genuinely null-pinned and byte-immutable, not retrospectively fabricated as source-pinned. The canonical attempt is nonempty pinned after binding; manual N1→N2, automatic Challenge resolution N2→N3 and manual N3→N4 issue real records carrying exact base/target/repository/fetch/push/source pins, spec blob OID, identity, generation, and monotonically increasing issue sequence. The test parses raw canonical JSON and compares historical issue bytes across transitions. Identical UUID replay must be effect-free with the same receipt; changed-payload UUID reuse must be `OperationIdReused`; stale CAS must be `StaleEntityVersion`; rejected/replayed requests preserve HEAD, sequence and entire tree.

The new diff does not remove or weaken original deterministic seeds 9000–9003, modes 4–25, prior typed two-clone competing-CAS loser, per-action-class generated outcome assertions, mixed lifecycle/assignment-history checks, or failure-category-preserving shrinker.

## Independently executed .NET 11.0.100 Release evidence

All commands were actually run with real process exit codes and `Client.Tests/Client.Tests.fsproj`; tests use `-c Release --no-build --no-restore` after each worktree's successful restore/build.

| Environment and filter | Exit | Passed | Failed | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Exact frozen test source: restore | 0 | — | — | Successful |
| Exact frozen test source: Release build | 0 | — | 0 errors | Four preexisting/unrelated warnings |
| Frozen: source-pin + generated per-class + strict concurrent CAS | 0 | 3 | 0 | Green |
| Frozen: `FullyQualifiedName~Challenge&FullyQualifiedName!~ChallengeReverseIndexFault` | 0 | 48 | 0 | 0 skipped; 3m17s |
| Frozen: original `ChallengeReverseIndexFaultTests` (9000–9003) | 1 | 1 | 3 | Expected red against unchanged old production |
| Frozen: direct mode15 matrix filter | 1 | 0 | 1 | Expected red: actual `Ok`, disposable remote HEAD advanced |
| Disposable Accepted-production composition: restore/build | 0 | — | 0 errors | Four unrelated warnings |
| Composed: source-pin + generated per-class + concurrent CAS | 0 | 3 | 0 | Green |
| Composed: original 9000–9003 controls | 0 | 4 | 0 | Green |
| Composed: direct mode15 matrix filter | 0 | 1 | 0 | Typed integrity/no mutation now enforced |
| Composed: complete modes 4–25 matrix | 1 | 21 | 1 | **Only mode 8 red**; not a fully green matrix |

The composition was strictly local to my own disposable QA worktree whose unchanged HEAD was accepted production `0a596bfb...`. Its overlaid test file Git blob OID `050452c69ecc8c24b6f69aedf3ad22af659d870d` matched the exact frozen `9c71...` blob; no combined commit, source publication, main merge or live effects occurred. After test execution I cleaned the overlay and removed the QA worktree through Jarvis's inspected, clean/integrated worktree-removal path.

**Mode 8 limit:** an intentionally forged edge under an unrelated unknown Challenge prefix is not discoverable by the bounded reader; the composed test returned `Ok` but did not advance the disposable remote HEAD. The original pinned task explicitly designates this exact scenario a bounded-reader discovery **non-goal**. It remains an accurately disclosed red test, not a new requirement or a ground to reject the two required fixes. No scope expansion or independent corrective Work is authorized by this review.

## Verdict

**Accepted**, restricted to this immutable **tests/docs-only** frozen source `9c71f86380754f6f9bb1758fdec38171f99c0959`. Both specific predecessor P1 oracle gaps are resolved and independently tested; no additional in-scope blocking finding was demonstrated. This is **not** an integration, main publication, independent production re-review, or assertion that every fault-matrix case passes. Previous review decisions and source snapshots remain immutable.
