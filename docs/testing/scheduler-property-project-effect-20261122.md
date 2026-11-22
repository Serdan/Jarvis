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
