# Independent Reviewer 2 — Accepted

**Task:** challenge-mode8-bounded-reader-oracle-20261121  
**Review ID:** da1e4fdc0ef443e0a509c2044edecda9 (claimed by Reviewer 2, ledger seq 10007)  
**Exact frozen source SHA:** 6b8505171909ca1112254b83375ed1a7af8f6224  
**Immutable remote review ref:** refs/heads/jarvis-review/challenge-mode8-bounded-reader-oracle-20261121/da1e4fdc0ef443e0a509c2044edecda9/r1  
**Accepted original tests:** 9c71f86380754f6f9bb1758fdec38171f99c0959 (Reviewer 2, 4d7ca26879ed4d349c6e9e2d907e220d, Accepted seq9957)  
**Accepted production:** 0a596bfb505f50bbf4b56bb93dffa1ac4d64920b (Reviewer 5, 41c92e8e4b614ff0ab1c026271fc9b73, Accepted seq9882)

## Source and task contract

Independently read current Reviewer role v3, applicable root/project AGENTS.md, standing Reviewer assignment, original P1 spec blob 969243b860d5c8520dc6f6bccd43cf2ce96af2b5, correction spec blob 680dc6a72a4f4886f24285995170473c72c710fe, original Accepted review summary, exact old and corrected test code, and accepted production read path.

The original accepted source's mode8 at lines 1752–1757 injects an unrelated GUID-named by-challenge edge then demands getAgentInbox return LedgerIntegrityError. That demanded global unknown-prefix detection contradicts the explicit original bounded-reader discovery NON-GOAL, disclosed in the prior Accepted review and composed QA checkpoint seq9978. In accepted production, getAgentInboxAt verifies owner-linked indexes and canonical blocked waits; it does not globally enumerate arbitrary unknown Challenge-prefix directories.

Verified accepted test source 9c71f863... is a Git ancestor (merge-base --is-ancestor exit 0). The correction has two intermediate commits, ab95f831... and 6b850517..., but the aggregate Git diff is exactly one modified file, Client.Tests/ChallengeStateMachinePropertyTests.fs. No production, manifest, document, or other tests changed. The corrected worktree is clean, and git ls-remote origin independently resolves the immutable managed review ref to exactly 6b8505171909ca1112254b83375ed1a7af8f6224.

## Detailed mode8 inspection

The new mode8, lines 1752–1785, retains the actual unknown Challenge-prefix injection and performs a real ordinary getAgentInbox call on the second Git clone. It permits either:
1. Ok containing the pre-existing generation-1 blocked assignment (the actual accepted production behavior); or
2. typed LedgerIntegrityError if a future reader detects additional corruption.

It rejects other errors and inappropriate successful inbox projections. It captures before and after remote HEAD, ledger snapshot Commit, Header.NextSequence, and full git ls-tree -r content. Matching trees include all audit, notification, receipt, wait and index files. The assertions require absence of canonical reader-side mutation, not unrestricted prefix discovery. The test remains explicitly present as TestCase(8) between TestCase(4) and TestCase(25); it is not skipped, ignored or filtered. The other 21 modes, mode15 pristine direct writer, mode16 known-future-generation case, seeds 9000–9003, source-pin and assignment-history checks, typed CAS loser, UUID replay, generated model/property checks and failure categorization are unchanged by byte-identical source outside this one branch. There is no global production reader change.

## Source-bound actual .NET 11 Release evidence

| Run | Exit | Observed outcome | Evidence |
| --- | ---: | --- | --- |
| Worker 7 original test blob 050452c69ecc8c24b6f69aedf3ad22af659d870d composed with accepted production | 1 | Fault matrix 21/22 pass; mode8 sole red, actual Ok and same remote HEAD | Job d93ac8693da1432ab717b7ff3fffedc7; original mode8 line1757 and source=0a596bfb... in failure |
| Worker 7 corrected test blob 3b1888e71ace2cd916d4beaad0f2b10c56a7369c with same production | 0 | Entire Challenge suite 82/82 pass including every mode4–25 and model/source-pin tests | Job 892db9afcfbb489f93e800f0fe10dff0; independently inspected retained completed job and individual mode passes |
| Worker 7 Server.Tests | 0 | 555/555 pass, 0 fail, 0 skipped | Job 58a1e046d3e74359a7a9a3e323254b9c; inspected completed job |
| **Reviewer 2 independently executed Release build + matrix** | **0** | **22/22 mode4–25 pass, including mode8**, real .NET11 runner duration 39.47 s | Job 6df9c39f27bb4acdbbf595f434ea6f30; dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests |

Reviewer 2 independently checked the disposable QA worktree Git HEAD is the exact unchanged accepted production SHA 0a596bfb505f50bbf4b56bb93dffa1ac4d64920b. Its test-project manifest overlay only adds the accepted ChallengeStateMachinePropertyTests.fs entry; its source file hash-object was exact corrected blob 3b1888e71ace2cd916d4beaad0f2b10c56a7369c both before and after the independent Release test. The test fixture builds a genuinely disposable bare Git remote with two clones for each probe, not a mocked result. The corrected source branch itself is clean. Main local HEAD remained d702352ef705f9991ac1cb14dc15a13cce78aa44.

**Limits of evidence:** Reviewer 2 independently ran the 22-mode matrix and examined the exact author-executed red/green and Server jobs, not an independent full 82-test or Server rerun. The optional unfiltered Client.Tests job 2fac167001434897a1ee4a122b16d557 finished **Canceled** (with activity persistence warnings), not passed; no full unfiltered Client green is claimed. Historical accepted-source red and corrected green jobs are source-bound by their fixture path, reported failure mode and verified blob provenance, but the old run was not independently repeated in a second QA checkout. Tests are not live deployment or publication evidence.

## Findings and decision

**No demonstrated in-scope blocking finding.** The old mode8 condition over-specified unknown-prefix discovery outside the original task. This minimal test-only correction keeps the injection as a visible diagnostic, strengthens no-write assertions, preserves other mode/property assertions, and passes independent and recorded Release checks. No net-new specification, threat model, corrective Work, or review-of-review is justified. This verdict does not adjudicate or direct further scope.

**Verdict: Accepted**, solely for exact frozen test-source SHA 6b8505171909ca1112254b83375ed1a7af8f6224. No main publication, integration, production re-review, Scheduler/Health activation, deployment or full Client suite pass is implied.
