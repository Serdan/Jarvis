# Scheduler property — Project effect fence and replay state model (2026-11-22)

## Authority and exact source boundary

Canonical assignment: scheduler-property-project-effect-model-20261122, generation 1, pinned spec bbfa80507371662399fdd2e1e59f6787605d0b90. This work is Phase A, test-only. Worktree baseline dcac884fc061869572b12a03befc12f11c009f2f includes Accepted Stage A a2e244bf747c4dec89907dd6207bf3b4c4098888. Stage A is disposable and deliberately not actual native Scheduler, BrowserBridge or account-CAS acceptance.

Frozen StageB B1 source 5cf8f99542a9653a013683e32e56a2c32a706164, review b93c1c95bd5745dc97c45b4642721465 (Reviewer 3): ChangesRequested, not implementation truth or end-to-end proof. The exact frozen source was inspected with git show against Client/Modules/NativeProjectEffectFence.fs. In that source, update (lines 231–259) builds its RequestJson at line 244 from candidate, effectId, expectedStatus, nextStatus; recordFakeTerminal proof is checked only at lines 268–277. On same-UUID exact-intent replay, LedgerStore can short-circuit proof validation. Reviewer P1: terminal-proof-omitted-from-intent. Reviewer P2: fake-boundary-coverage-gap (the accepted project-side transition is not composed with fake send/no-send and separate fake account release). These are findings against the frozen rejected source only. Do not change Worker22 production or independently treat StageB as fixed.

Exclusive tracked write lease: scripts/tests/scheduler-property-project-effect-20261122/** and docs/testing/scheduler-property-project-effect-20261122.md. No changes to Worker4 account CAS, Worker22 StageB, shared fsproj, main, live ledger, browser, deployment, migration, or schedules.

## Reference state machine and provenance

Immutable candidate identity consists of task, attempt, generation, source lease, owner, project ledger, epoch, original reservation receipt, effect UUID and dispatch UUID. A project-head CAS and fake account version authorize one claim. Claim conflict or changed identity fails closed. A durable Dispatching marker must precede every fake provider submission. ACK timeout, restart, reconnect, clock rollback or changed owner cannot authorize a second send. Unknown provider outcome preserves original reservation. VerifiedTerminal requires original effect/session and complete valid fake proof. VerifiedNoSend requires independent project no-send and provider no-session proofs. Exactly one fake-account release and completion follow only verified terminal or verified no-send.

All command outcomes are typed strings/enums, including ProjectConflict, AccountConflict, CandidateConflict, ClaimConflict, AlreadyStarted, NotTerminal, OperationIdReused, AlreadyApplied, and Released. The complete terminal proof is included in operation identity. Exact replay differs from same UUID with changed terminal marker/status/session/effect. The deliberate mutant omits the proof from hash identity, reproducing wrong AlreadyApplied and triggering oracle mismatch. Another mutant creates release before proof or false no-send and trips safety invariants.

Reference models live in model.mjs and Program.cs, not production adapters. The fake provider and separate fake account are non-authenticating. The Git fixture stores test state in state.json, not Jarvis's real ledger transaction. Separate fake-account release is a simulated boundary, not atomic production CAS. Phase A success cannot be stated as Phase B implementation proof.

## Adversarial cases

| Case | Expected behavior |
|---|---|
| Two Workers/Reviewers, same candidate, different UUID | one claim; original owner/lease/generation/effect |
| Stale project head, epoch, lease, source, reservation/account | typed denial, zero sends, capacity retained |
| Three project Git clients at same remote head | one ordinary non-force push, rival rejected before effect |
| Durable dispatch, lost ACK, restart/reconnect/clock rollback | one fake submission, never a second StartAuthorized |
| Provider Unknown, foreign session or forged terminal | no release, no completion |
| Same terminal UUID but changed proof | OperationIdReused, not AlreadyApplied |
| Exact same UUID and proof replay | AlreadyApplied without state change |
| Verified terminal, original fake receipt | release once, repeats denied |
| Verified prestart no-send | project and provider proofs both required, zero sends |
| Mutated hash and unsafe release/no-send | negative oracle catches fault |

Deterministic xorshift32: pure Node model runs 256 seeded 48-command traces and a delete-chunk shrinker; standalone .NET11 Release model runs 512 seeded 48-command traces. Node additionally runs six seeded multi-step traces against three real disposable project Git clones, a separate disposable fake-account bare Git remote and a fake provider, asserting exact persisted state equivalence per command. RealGitFixture creates new temporary bare remotes and removes them in finally. A competing two-client push test proves non-fast-forward rejection without a force push. No real account, BrowserBridge or GitHub remote writes occur.

On seeded failures, the Node harness emits the original seed and shrink candidate. The pure shrinker is directly tested. A failed Git trace's pure shrink candidate is diagnostic only; it is not represented as a minimal Git-history shrink.

## Terminal executed evidence

| Test | Jarvis job | Terminal |
|---|---|---|
| Node property.test.mjs in exclusive directory | 3dad37db4da4454eaf71e17911cc9b3a | exit 0, 14/14, zero fail/skip |
| dotnet run -c Release, exclusive ProjectEffectModel.csproj (net11.0) | 388279932c2f466eb5055aaa00b4736d | exit 0, 8/8 independent model checks |
| Accepted StageA two separate native-project-effect*.test.mjs | b111e927de7c4aa3a5078abe675effeb | exit 0, 33/33, zero fail/skip |

Earlier job b3ba57badf0f428f95d11464bc23b6d7 failed 11/12 from a test-mutant setup error: the mutant was active only on replay, not initial operation recording. Corrected rerun cb9df9f4329043e7be79f7924ed92317 passed 12/12, superseded by 14/14 expanded run. This was not a production defect. All these are terminal console/Node evidence, not TRX reports. The .NET11 job tests only the model, not Worker22 F# StageB.

## Handoff and conditional Phase B

Hold source-bound actual StageB implementation traces until a fresh StageB B1 correction is independently Accepted and source custody verified. Then adapt generated commands to the actual guarded F# project-side seam against disposable Git clones, preserving its owner and making no edits to its source. Distinguish Stage B1 from B2 production account/provider integration. Passing a model, external Git ancestry, fake receipts or the old ChangesRequested review cannot authorize B2, main publication, real account release, browser send, schedules, migration or deployment.

Submit this test-only branch to the shared independent review queue. Reviewer should verify seeds, source pins, Git race loser, mutated-proof oracle, fake-account negative release, exact allowed paths and that no test or document claims production source acceptance.


## Adjudicated Phase-A correction — 2026-11-22

Independent Adjudicator 3 terminal checkpoint **10877** on the frozen
`731fa5245bf7a278756aa5fee832bbfdd950c12c` review
`13a3c9879f254ee6b88547b2b31b5cc4` authorized P1 **ACCEPT** (unpersisted
external receipt permits false no-session/release) and P2 **NARROW** (seeded
post-send, Unknown/retry/terminal/release assurance). Both are test-harness
defects; neither is evidence of an actual production defect.

The test-only `provider-boundary.mjs` persists a single provider session/receipt
**outside** the project and fake-account Git state before returning ACK or
Unknown. A restarted provider object reads the same receipt. The project
transition no longer accepts a static `VerifiedNoProviderSession` string without
consulting this independent store. Release rechecks the independent provider.
A matching terminal command consults the provider receipt and records its
observed terminal session in disposable project state; replay includes the
complete terminal proof in the operation identity. The separate fake-account
release also rejects contradictory provider receipts.

`effect-traces.test.mjs` constructs reproducible guided seed histories with
three disposable project Git clients, a separate fake-account bare repository,
and the independently persisted fake provider. Sent traces include committed
start, accepted send with **lost ACK and unpersisted project result**, restarted
receipt recovery, duplicate send attempt (not a new submission), false no-session
denial, denied Unknown release, forged and premature terminal denial, eventual
valid terminal, same-UUID changed-proof denial, exactly-once project/account
release, and retry after release. Truly unsent histories include a verified
provider-absence control and valid no-send release. Per-seed mandatory coverage
counters ensure these paths are reached rather than merely included in an
unreachable random alphabet. The independent safety oracle reads the provider
and account stores and rejects a false no-session even if the project adapter
produces internally plausible self-consistent state. A deliberate false-no-send
mutation is killed by this oracle, and a delete-chunk shrinker must reproduce a
smaller counterexample. Git persistence equality is still **not** F# production
differential testing.

The original 256 x 48 Node abstract traces and 512 x 48 .NET abstract traces
remain separately labeled **pre-send abstract/reference exploration**. Their
historical zero generated provider sends must not be mistaken for the new
seeded post-send evidence. The .NET11 standalone simulator also now reads a
separate file-backed fake receipt after lost ACK/restart and runs 128 seeded
48-noise-step post-send and true no-send histories with typed denials. Its
`Move` refuses static no-session claims without independent provider evidence.

**Release terminal evidence:** .NET job
`188af3ed315945b9b1e6c980760f61b6`, exit 0, **9/9**. Generated .NET
coverage: accepted sends 64, lost ACK 64, duplicate retries 64, denied false
no-session 64, valid terminal 64, valid releases 128 (64 sent and 64 truly
unsent), provider restarts 128. The fake provider and account are strictly
disposable. They do not authorize any real BrowserBridge/account send.

**Phase B HOLD:** StageB B1 corrected candidate
`67e2ed9d` remained **ChangesRequested** at this correction's authority
checkpoint. This task does not verify production F# `NativeProjectEffectFence`,
and no accepted native B1 adapter is available under the original lease. No
claim of source-bound native B1 proof, B2 end-to-end integration, trusted writer
incorporation, main publication, deployment or unattended scheduling is made.
Phase B requires a fresh independently Accepted StageB correction and exact
source custody before a real disposable F# comparison can legitimately run.

**Node terminal evidence after correction:** job
`6f0923944ed44c0bb58132e6fa2be628`, exit 0, **17/17** tests,
zero failures/skips. The 14 generated Git histories (seeds 101–114) recorded:
7 independent accepted sends with lost ACK, 7 duplicate-send retries,
14 denied false/no-session claims, 7 denied premature releases,
7 verified terminal transitions, 7 true no-session transitions,
14 project releases, 14 matching separate-account releases, and
84 provider restarts. The adversarial false-no-send adapter mutation failed
the independent provider oracle; deterministic delete-shrink/replay passed.
The first pre-correction Node job `31998430fa5247df8d36376c12ae5c5c`
was red (15/17) for a test-generator proof argument bug and mutant assertion
ordering; fixed before the passing terminal execution. None of these jobs
invoked production browser/account sends.


## Adjudicated stale-dispatch authorization correction (2026-11-22)

**Authority:** independent Reviewer4 ChangesRequested decision 10944 on frozen test-only
`c4494998c29089b624d5b458361fa973a9a14639`, finding
`findings/stale-dispatch-after-nosend-release.md`; independent Adjudicator2
**NARROW**, checkpoint 10983, authorizes ONLY this original Phase A test/docs
lease. This is a defect in the *disposable reference boundary*, not evidence of
a bug in production F# StageB or a real browser/provider send.

**RED-first canary:** Jarvis job `77c339f14e834e70942d710c521b9f3a`
ran the new held-Dispatching-snapshot test before the provider correction, exited
1: 3/4 tests passed, the held snapshot sent *after* VerifiedNoSend, project
Released and FakeAccountReleased; expected `DeniedByNoSessionSeal`, observed
`Unknown`. That result establishes the original late-send counterexample.

**Bounded repair:** `DurableFakeProvider` owns a separate persisted
`noSessionSeals[effect]` alongside receipts, independent of both project Git
and fake-account Git. The verified no-send reference transition checks the
supplied project/provider/effect proof and attempts a prospective seal *before*
committing VerifiedNoSend. If a durable send receipt already exists, sealing
fails and neither project nor fake account may release (send-wins). If sealing
persists first, all later sends for that effect, including a retained
`Dispatching` snapshot across provider recreation, return
`DeniedByNoSessionSeal` without accepting a receipt (seal-wins). Project
release and the separately versioned fake-account release require the provider
seal, not mere absence of a session. A denied/incomplete project transition may
leave a conservative provider seal; this is fail-closed safety, not a claim of
cross-store atomic liveness.

**Reachable tests:** seeded even-numbered post-send traces retain a frozen
`Dispatching` snapshot before no-send, commit VerifiedNoSend and project +
separate fake-account releases from three disposable project clones, restart
the provider, then try the stale snapshot. A separate explicit canary inspects
provider acceptedCount=0, retained account release and independent invariants.
Odd-numbered seeded traces send first, retain Unknown and the account
reservation, and reject the no-session proof/release. Independent negative
mutation deliberately bypasses the test-provider seal; a persisted late
receipt then fails `FalseNoSendDurableProvider`. Deterministic seed 102 replay
plus a delete-chunk shrinker retain the safety counterexample; the shrunk
history need not retain account release because the contradiction is already
observable upon VerifiedNoSend. Prior terminal-proof hash and false no-send
mutants remain in the suite.

The standalone .NET11 simulator independently mirrors file-backed provider
receipt versus prospective seal across restart, tests 64 send-wins and 64
seal-wins deterministic seeds, and verifies a deliberate late-send mutation is
rejected by its independent invariant. **Release model terminal proof:**
`dotnet run --project ProjectEffectModel.csproj -c Release` exited 0,
**11/11** standalone checks; it is *not* a native F# implementation test.

**Residual, deliberately unproven:** the file-backed providers serialize
sequential test events and do not implement production multi-process atomic
compare-and-swap, live provider authorization, browser send, real in-flight
calls, crash windows between provider/project/account stores, distributed
quiescence, native F# B1 or B2 differential checks, or deployment authority.
A passing Phase A model is not an acceptance or cutover receipt. Phase B stays
HOLD pending separate exact-source native adapter proof and authorization;
no shared fsproj, production implementation, main, live account, browser,
deployment, migration or scheduler activation was changed.

**Unfiltered post-correction Node terminal evidence (before freeze):**
Jarvis job `2ac537375ba848aaa075251b3b09ac78`,
`node --test property.test.mjs effect-traces.test.mjs` exit 0,
**19/19** passed, 0 failed/skip. Generated 14 three-project-clone histories
covered 7 durable sends, 7 lost ACKs, 7 retries, 14 denied false no-session,
7 denied premature releases, 14 project/account releases, 84 provider
restarts and **7 retained stale Dispatching sends denied after seal**.
Seed 102 seal-bypass mutation caused independent oracle RED and reproduced
on the shrunk counterexample. Source commit and fresh frozen review will be
recorded separately in the canonical task checkpoint.


## Conditional Phase B accepted-source native differential probe

The canonical StageB task scheduler-native-project-effect-stageb-20261121 is Closed/Finished. Independent Reviewer 1 Accepted decision 10938, review c82ba0618d8b41f395b12ef1adab22b1, exact immutable source SHA 74fcda395857669c5db0f80ef997b4f2d6e37fcd based on dcac884f. Before the probe, the registered scheduler-project-effect-stageb-worker22-20261122 worktree had that HEAD and a clean status. StageB B1 remains guarded by STAGE_B_DISPOSABLE_TEST_ONLY, never registered into the live Client.

The original leased effect-traces.test.mjs conditionally runs the accepted native F# implementation when JARVIS_STAGEB_SOURCE_ROOT names the clean source worktree. It refuses a different HEAD or dirty tree. The runner creates an automatically removed standalone .NET11 Release project from the exact accepted StageB fsproj, F# module, and harness, inserting only a temporary F# test extension. It retains real Client.LedgerStore, three disposable project Git clones and one separate fake-account Git repository. No Worker22 files, shared fsproj, production source, main, or external account/provider state is changed.

Historical frozen 6af5edbfa ran two fixed native templates selected by seed parity; the generated Phase A command histories were NOT passed to the native F# fixture. It did prove two guarded smoke cases (Unknown and pre-dispatch no-send), not an executed generated differential. Reviewer2 ChangesRequested decision 12099, finding native-generated-trace-not-replayed, correctly identifies that evidence gap. The two historical fixed-template terminal receipts below remain evidence of that older harness only, not evidence of the subsequent repair.

This is a projected invariant comparison, not exact state equivalence. In particular StageB B1 can accept pre-dispatch fake no-send, while the broader Phase A oracle requires an independent prospective provider no-session seal. B1 does not authenticate provider sessions or prove provider quiescence. Actual external in-flight effects, account capacity CAS, cross-store atomicity, B2, BrowserBridge, schedules, migration, activation and deployment remain HOLD.


### Phase B executed source-bound terminal receipts

RED-first job 04aac6a83e434a5eb27ae138874b5a5e: outer Node test runner exit 0 (6/6, zero failed/skipped), deliberately mutated temporary native .NET11 Release harness exit 1 with the expected UnsafeLateProviderAccept on seed 102. That is the intentional negative oracle, not a production failure. StageB HEAD/source verification passed, no native source edit.

Unmutated GREEN job ff54b94063844d3d97f97c1286f4682f: outer Node runner exit 0 (6/6, zero failed/skipped), nested source-bound native .NET11 Release exit 0 (24/24 PASS, including all 23 originally accepted StageB harness tests and the eight-seed differential projection). Seeds: 101, 102, 103, 104, 105, 106, 107, 108. Accepted immutable native source SHA 74fcda395857669c5db0f80ef997b4f2d6e37fcd. Exactly four send/Unknown traces and four pre-dispatch no-send traces. No broad Client suite rerun. No BrowserBridge/provider send, real account state, main, deploy, B2, scheduling or cutover.

Historical interpretation: the old extra native run establishes guarded B1 fake-port project CAS and separate fake-account release behavior under two fixed safety scenarios. It did not establish a generated Phase A/B1 differential, independent provider-owned prospective no-session seals, provider authentication, multi-process atomicity or trusted production integration. The frozen 6af5edbfa review remains ChangesRequested, not retroactively accepted.

## 2026-11-23 adjudicated generated-history Phase B repair

Independent Adjudicator3 terminal checkpoint **12132** ACCEPTED Reviewer2's P2 evidence finding (decision **12099**) against **6af5edbfa**. Operator GO permits only this existing task's `effect-traces.test.mjs` and documentation. No changes to Stage B production F#, its harness/project, the Client fsproj, provider, account writer or live schedules.

RED first: native-history-presence regression requires every generated seed's claim command identity in the *compiled disposable* source. Job `74c64bd5818f4a1ca25be7d54c1ef686` exited **1**, outer Node **5/6 pass, 1 fail**, specifically `Generated native history missing seed 101 command identity`; existing five Phase A controls passed. The negative is a test-evidence defect, not a B1 production defect.

The replacement fixture projects **the actual generated command array** for every seed 101–108, not a fixed sent/parity branch. It passes each event's original operation name, stable deterministic UUID, source client index, native action, and independently predicted typed result, native project status, fake-account status and fake callback count into a temporary F# fixture on **accepted StageB 74fcda395857669c5db0f80ef997b4f2d6e37fcd**. Each projected event runs against one of three separate disposable project clones; the release uses independently cloned fake-account Git state. Its `NATIVE_PHASEB_EVENT` transcript must match the JavaScript projection event by event. Exact UUID terminal retry is distinguished from changed-proof reuse, and a deliberately bypassed callback must violate `UnsafeLateProviderAccept` in the negative variant.

**Coverage limit:** This is a *partial typed safety projection*, not a full event-history equivalence result. The generated Phase A protocol separates start from provider send, permits prospectively sealed no-send after start, stores provider ACK-loss/terminal evidence outside project Git, and releases the project separately from fake-account Git. StageB B1 combines guarded start with fake callback, allows fake no-send only *before* dispatch, has no independently authenticated provider session/receipt/seal and performs fake account release without a separate Project Released transition. The harness enumerates every skipped or non-equivalent event as `NATIVE_PHASEB_UNSUPPORTED`, including provider restart, lost ACK, prospective seal, stale external callback, terminal timing/session authentication and separated account release; it does not silently translate those into source-backed guarantees. The conditional FULL Phase B generated-model parity gate remains **open**, regardless of partial safety-projection success. No production bug is claimed from intentional protocol differences.

**Source-exact positive terminal:** Jarvis job `2529f14a17734f50be3c1a5ed2e59a2d`, outer focused Node runner exit **0** (1/1), nested accepted native F# `.NET 11 Release` exit **0** with **24/24 PASS** (original accepted 23 tests plus generated per-event projection). The source check confirmed clean immutable **74fcda395857669c5db0f80ef997b4f2d6e37fcd**. The outer runner compared every emitted `NATIVE_PHASEB_EVENT` (seed, projected ordinal, original command identity, typed outcome, project state, separate fake-account state, fake callback count, client index) with the independently computed seeded JS expectation. No mismatched events.

Eight source histories are distinct; the count of explicitly non-equivalent/skipped boundary events per seed 101–108 is **24, 21, 22, 24, 22, 22, 24, 18**. A count includes projected safety actions that deliberately do not represent the same Phase A protocol event, not merely omitted events. It is **not** a claim that the 8 full histories replayed with complete semantic parity.

**Independent synthetic negative terminal:** Jarvis job `09bfa0d67d4b4a8a99bb6bfb866dddd0` with `NATIVE_DIFF_MUTANT=1` passed the outer focused Node expectation **1/1** (exit **0**) while deliberately modified **temporary fixture** failed the nested .NET11 Release run (exit **1**) at `UnsafeLateProviderAccept seed=102 index=10 op=s102-attemptLateSend-10 calls=2 expected=1`. No production source was mutated. This demonstrates the event-level callback oracle detects an extra fake send rather than accepting a misleading aggregate parity result.

Prior interim test-compilation failures were fixture formatting and fake-port result-type errors; the native runner then passed internally, with an outer ordinal-vs-original-index comparison mismatch that was corrected before the terminal green job. They are not evidence of production B1 defects.

**Focused Node abstract/Git/provider terminal:** Jarvis job `7e7e15458db74a2b9578f4ea10f8540a`, `node --test property.test.mjs effect-traces.test.mjs`, exit **0**, **19/19 passed**, zero failed/skipped. All 14 generated Phase A Git/fake provider histories retained seven lost ACK sends, seven retries, 14 denied no-session attempts, 14 project and matching fake-account releases, 84 provider restarts and seven stale-dispatch late sends denied by the independent prospective seal. Its Phase A RED mutation, shrink and exact operation-hash controls also passed.

Immutable repair SHA and fresh independent review remain to be recorded. Full Client suite, real account/provider effects, B2, live scheduling, main/push/deploy or cutover remain excluded.
