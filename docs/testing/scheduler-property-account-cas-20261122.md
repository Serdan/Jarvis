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


## Adjudicated review correction — 2026-11-22

Immutable frozen review f0d3fc9cfb4f4e1092fc3521b7d20ac0 (ChangesRequested) identified two **test-assurance** gaps at a2e57bf59a7b967fafbcb821640bf41851d92738. Independent Adjudicator 3 checkpoint 10877 authorized P1 ACCEPT (genuine generated/native comparison) and P2 NARROW (reachable fail-closed boundaries) in this test/docs lease. Neither finding established a production defect, and no production files or shared project files are modified.

The additional independent reference implementation in Differential.fs generates **25-step traces** from seeds 17 and 31 with 4 source-authenticated Worker/Reviewer candidates covering two disposable project Git remotes and two disposable account clients. Seed 17 is executed twice to compare normalized native results, seed 31 once for another candidate order. The native source remains 5607baf9913f4976add13c984e0182906d4fe8c7 and frozen original fixture remains e6988ac42f4191f807b57f04dcb6ddec160b503f. The generator verifies both Git blob lineage checks before concatenating the original fixture with Property.fs and Differential.fs. No production, shared fsproj, or original source edits are necessary.

**Every native differential step** makes a genuine reserveVerifiedDisposable, transitionVerifiedDisposable, or releaseVerifiedDisposable invocation through the original source-linked fixture. A separate model predicts typed outcomes and reservation stages independently of native responses. After every native call, assertions compare typed commit/replay/reject/capacity results, original immutable reservation UUID, dispatch/effect identities, status/generation, released terminal identity, per-role occupancy, canonical uniqueness, account NextSequence, state version, remote Git ref HEAD, and persisted account operation receipt existence/hash/sequence/replay. A committed account transaction changes HEAD exactly once; rejected/replayed transactions do not. Original model's 384 x 24 abstract steps remain **abstract**, not reclassified as native checks. Seeds reproduce command and operation identities, not freshly generated disposable project Git SHA bytes.

The test-only threat matrix includes forbidden project ProjectHead, forged original terminal operation origin OID, forged provider-terminal blob OID, wrong original project fence in a real committed project terminal operation, and an absent original terminal operation. Each should fail closed without an account release or receipt, while a separately authenticated positive terminal operation releases capacity and permits the other candidate. The default-off account Git interposition also exposes a post-push lost acknowledgment and a pre-push failed publication; validation must reconcile any post-push commit/receipt and never mistake an unknown transport result for proof of no send. The preexisting real two-client Worker/Reviewer barriers independently test competing CAS claims. No forged browser, provider, account, or scheduler live action is involved.

**Comparator mutation control:** the first native committed observation is deliberately relabeled as a replay in the recorded observation stream. The independent comparator must detect the mismatch, and a chunk-removal reducer must shrink it to one mismatched recorded observation. This reducer replays *recorded results*, not Git mutations; the reproducible seed itself reruns the native Git trace independently. No claim of production hardening, mainline incorporation, or complete scheduler end-to-end acceptance follows from these tests.

### Terminal corrected-source evidence

- **.NET 11 Release success:** Jarvis bounded job `6c76f15d60bf443cad31b1886850866c`, `dotnet test scripts/tests/scheduler-property-account-cas-20261122/PropertyHarness.fsproj --configuration Release --no-restore --filter FullyQualifiedName~SchedulerAccountCas --logger console;verbosity=normal`, terminal **exit 0, 6/6 passed**, 2.3652 minutes. Original 4/4 and new 2/2 tests all passed in the same source-bound assembly.
- Source generator job `aec584f398f4436c8ac12e8ba2537ead` exit 0 confirmed exact frozen source/fixture blobs and accepted-ancestor lineage. Newly generated test compilation unit was local disposable output, not a source artifact for review.
- Native paired traces: seed **17 twice**, seed **31 once**, each 25 steps. Output reported `matched=25` for each seed, and authentic account `NextSequence=14` at completion. Each step checked typed result, original UUID, current native reservation state, both role occupancies, independent bare remote HEAD, receipt and exact account sequence/state-version evolution. Distinct source Git HEAD bytes across executions are expected and intentionally not conflated with model state.
- Source-bound mutation: first recorded native committed result replaced with a fake replay, comparator detected it, and trace reducer shrank a 25-record observation stream to **1** mismatching observation. This is an **observation mutation**, not a claim of live Git mutation testing. Existing 384 x 24 pure model sweep passed with 2,099 accepted / 6,545 rejected / 572 replayed and separate 4-to-2 model mutation shrinking; no claim of 9,216 native operations.
- Negative-space: new pre-push FailBeforeExecution was rejected without account HEAD or receipt change; post-push ExecuteThenLoseAcknowledgment reconciled the actual authenticated receipt, prevented duplicate reservation and replayed exact original UUID; forged project head, terminal operation-origin, terminal blob, project fence and missing terminal operation all failed native release before the matching valid terminal did release capacity. Existing independently bounded CAS two-client barriers remained green.
- **Known bound:** no direct OS-level account lock acquisition denial was injected; the existing genuine two-account-clone CAS collision/barrier plus controlled Git pre-/post-push faults are the reachable contention cases in this scope. The production account CAS remains default-off. This test suite is not evidence of live scheduler cutover, Phase B send safety, mainline integration, or universal state-space completeness.

The corrected source still requires a **fresh independent immutable review**; a passing owner-run Release suite alone does not change the original review's ChangesRequested verdict.
