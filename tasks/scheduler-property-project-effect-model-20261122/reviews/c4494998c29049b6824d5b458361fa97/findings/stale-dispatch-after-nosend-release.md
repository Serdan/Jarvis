# P1 — stale dispatch snapshot is accepted after project no-send and account release (Phase A oracle)

**Verdict impact:** ChangesRequested on frozen test-only source `c4494998c29089b624d5b458361fa973a9a14639`; not a claim about F# StageB production. **Scope:** original task `scheduler-property-project-effect-model-20261122`, canonical spec `bbfa80507371662399fdd2e1e59f6787605d0b90`; adjudicator checkpoint 10877 explicitly confines corrective work to the original Phase A fake-provider / generated post-send test and docs lease.

**Original obligation:** The project-side reference and independent boundary must disprove false completion/release when account/project/provider evidence is stale or ambiguous, assert no send on stale/untrusted proofs and no release on unknown external effect, and exercise multi-client replay/restart with real disposable project Git clones. The provider no-session evidence must be independent, not a self-consistent project assertion.

**Source and root:** `scripts/tests/scheduler-property-project-effect-20261122/provider-boundary.mjs:23-36` authorizes `send(project,effect)` solely from the **caller-supplied** `project.phase === "Dispatching"` and matching effect, without checking that the project fence remains live and has not since been closed/released. `model.mjs:110-129` permits `VerifiedNoSend` and `release` if the provider currently has no receipt, without fencing a previously authorized but delayed send. The generated `effect-traces.test.mjs:148-149` late-send path always uses `f.actual()` (fresh head), so it never probes a retained stale dispatch snapshot.

**Independent reproduction (no source changes):** Jarvis job `c28e84c6c0f04b51a2ff4f04cf0636c0`, exit 0. At exact detached HEAD, create `new RealGitFixture()`. Apply canonical claim using client 0; start on client 1; **cache** `const stale=f.actual()` at phase `Dispatching`. Apply `noSend` with `goodNoSend()` on client 2, `release` on client 0, then `f.releaseFakeAccount()`. Finally invoke `f.provider.send(stale,CANDIDATE.effect,"ackLost")`; inspect persisted project, account and provider receipt; call `checkInvariants(f.actual(),f.provider)`.

Observed, in order:
```text
claim Claimed
start StartAuthorized
cachedPhase Dispatching
noSend VerifiedNoSend
release Released
accountRelease FakeAccountReleased
lateSend Unknown providerAccepted 1 projectPhase VerifiedNoSend completed true
oracle REJECT FalseNoSendDurableProvider
```
The fake provider has **durably accepted** one effect after the project and separate fake account recorded a successful no-send/release. The independent invariant catches the contradiction **only after** the false completion. The ordinary 17/17 Node and 9/9 .NET checks miss this ordering, so their green cannot establish the promised stale-client/future-send safety property.

**Proportional corrective acceptance:** Within existing test/docs-only lease, add a reachable seeded interleaving where an authorized in-flight/retained Dispatching snapshot attempts provider send after no-send verification and/or release, assert independent fake-provider and fake-account outcome safety, and ensure a mutation produces a RED. Model whatever *disposable* fencing/proof makes no-session safe against future accepted sends (not merely absence at observation time). Maintain exact source distinction and Phase B HOLD; do not prescribe production API, implementation, deployment or new authority. This is a test-harness soundness finding; no real provider, browser or account effect was observed.
