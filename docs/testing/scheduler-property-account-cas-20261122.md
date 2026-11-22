# Account CAS reservation state model — Worker 20

Task: scheduler-property-account-cas-model-20261122
Assignment: Worker 20 / attempt 0d2fca76745c44fe8c2a824d91998d0f
Scope: exclusively new scripts/tests/scheduler-property-account-cas-20261122/** and this document.

## Source boundary

- Accepted independent account-CAS correction: 5607baf9913f4976add13c984e0182906d4fe8c7; review b73e6c0d8f2145a99c6740e2f3a851bd (Accepted).
- Main at worktree-creation inspection: dcac884fc061869572b12a03befc12f11c009f2f; git merge-base --is-ancestor 5607baf main returned 1. Thus **not integrated on main** at that observation.
- Isolated registered test branch started exactly at 5607baf; no original source edits, no Worker4 or Worker22 branch writes, and no shared Client.Tests.fsproj edit.
- Production Client/Modules/NativeSchedulerAccountReservation.fs blob: fd9c9591c74ce906e15dfc191c2dbac0995b8dc3, identical at 5607baf and in the source-bound test worktree.
- Frozen original Client.Tests/NativeSchedulerRosterPolicyTests.fs blob: e6988ac42f4191f807b57f04dcb6ddec160b503f.
- SDK: .NET 11.0.100, net11.0, Release, NUnit 4.3.2 / Adapter 4.6.0.

## Harness and oracle

The only production implementation called is the pinned original Client project. A separate standalone F# test project under the exclusive lease compiles the frozen disposable Git fixture plus new property tests. It does not modify the shared test project or source files.

The independent reference model has immutable candidate/operation/dispatch/effect identities, account and per-project Worker/Reviewer quota pools, reservation statuses, terminal IDs, distinct UUID receipt replay, and abstract canonical HEAD/sequence evolution. The generator uses 384 deterministic seeds with 24 actions each (9,216 operations). The model invariants reject duplicate canonical keys/dispatch/effect/terminal identities, pool overcapacity, head/sequence regression, and mutations after rejected or replayed actions. It includes a deliberately faulty duplicate-insertion oracle and an input-trace shrinker.

Model Release result: seed sweep **passed**, 2,099 accepted / 6,545 rejected / 572 replayed, failure trace shrunk from four steps to two.

Real disposable fixtures use two account clones plus two independently authenticated project remotes, actual task/attempt and review-claim Git records, signed roster policies and account CAS transactions. Separate source-bound tests cover simultaneous Worker/Reviewer CAS barriers, UUID replay/changed payload, canonical duplicate denial, Reviewer version 2+2^32 alias, stale reviewed source, quota and project boundaries, Unknown occupancy, authenticated terminal evidence and slot release, and forged claim/coverage/fetch negative paths. The real-Git tests passed: two deterministic Worker/Reviewer CAS barriers (3 seconds), multi-step Worker/Reviewer replay/terminal trace (11 seconds), and zero-quota, forged evidence, stale authority and unavailable fetch negative cases (7 seconds). Final four-test Release run: **4/4 passed, terminal exit 0**, 25.4845 seconds.

## Reproducibility / limitations

Run scripts/tests/scheduler-property-account-cas-20261122/run.sh from the isolated branch for source/fixture blob and ancestry checks, offline-capable NuGet restore, generated unit construction, net11 Release NUnit filtered run and TRX. The shell wrapper invokes `dotnet fsi --exec scripts/tests/scheduler-property-account-cas-20261122/prepare.fsx`; the latter was executed directly by the approved Jarvis dotnet tool because shell execution is denied. A deliberately wrong source blob `0000000000000000000000000000000000000000` was **rejected**, terminal exit 1 and `SOURCE_PIN_MISMATCH`, before writing a file. An ordinary positive source generator invocation passed, producing exact output SHA256 `5690aae3caeff4ffdb3180ca85a9e87fb57c764c22488f933df493977751a7d9`, identical to the generated source used in the final 4/4-passing Release test. The ancestry missing-source negative control was not exercised. Generated source is ephemeral and is not part of the committed artifact.

The standalone fixture needed **eta-expansion of two function aliases in its generated copy only**, because original function-value alias static initialization failed in the single-file standalone test assembly. The production source and frozen original test file are unchanged. An original baseline replay test then passed (1/1) under the standalone Release project, before interpreting the new property test results.

Pure-model 9,216 operations are not misrepresented as 9,216 live Git transactions. All real Git remotes are private disposable fixtures. No live account, provider session, browser send, production scheduling, ledger deployment or migration has been exercised. In particular, Stage B's ChangesRequested verdict remains unchanged; this is not an end-to-end Scheduler proof.

## Raw execution identifiers

- Initial compile failure (harness annotation, not production): job fcbbe2a69fe44a87a13dd25b11b209bb.
- Initial standalone runtime fixture initialization diagnostic: job 121877341b5c44b09b829ed9a98050ff.
- Frozen baseline replay after eta-expansion: job 29084d2d878b4433b3ba52442803530f — exit 0, 1/1.
- First four-case Release: job 25c06d78483641c7b5b9f6bbea34d6b3 — 3/4; final case exposed test fixture sequencing error (fetch fault occurred before coverage setup).
- Corrected isolated negative case: job 128f01abba1244e38c7c6b2d1b20a6c0 — exit 0, 1/1.
- **Final full source-pinned Release**: job `87d9865a37a14367bbea432e89b9fe84` — terminal exit 0, 4/4 passed, TRX `scripts/tests/scheduler-property-account-cas-20261122/evidence/scheduler-account-cas-property.trx`, 25.4845 s.

Source-exact Release TRX and typed negative controls are retained for immutable review. The review verdict is not implied by passing tests. Neither a passing model alone nor an unreviewed harness is acceptance.
