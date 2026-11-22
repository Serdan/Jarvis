# P1 — Unpersisted fake-provider send permits a false no-send and release

**Severity:** P1, blocks Phase A model/oracle acceptance. **Scope:** test-only model defect, not evidence of an actual production F# StageB defect.

**Original requirement:** Canonical spec `bbfa80507371662399fdd2e1e59f6787605d0b90` Phase A: "provider send outcome Unknown", "no release on unknown outcome", "independent project no-send and provider no-session proofs", "no assumed rollback of uncertain external effects", "fake account/provider boundary". The reference model is intended to supply a sound negative oracle for those cases.

**Exact frozen source:** SHA `731fa5245bf7a278756aa5fee832bbfdd950c12c`:
- `scripts/tests/scheduler-property-project-effect-20261122/model.mjs:126-135`: `fakeProviderResult()` increments provider submissions only in a returned clone of the *project state*, not in a separate fake-provider store. A failed/uncertain acknowledgement before the returned state is persisted loses the model's sole send evidence.
- `model.mjs:101-120`: `noSend` trusts the project's `providerSubmissions === 0` and static caller-supplied `proof.provider === "VerifiedNoProviderSession"`; no fake-provider observation or independent proof is checked. `release` then treats `VerifiedNoSend` as sufficient.
- `model.mjs:256-270`: `releaseFakeAccount` verifies project `completed`, which follows the unsupported no-send state, and releases the separate fake account.
- `property.test.mjs:252-264`: positive no-send/release test does not first exercise an accepted provider effect whose result is lost.

**Independently reproduced** (read-only Jarvis job `d9e240cdd6cd4a22a486999404915393`, exit 0): perform model claim and committed start; invoke `fakeProviderResult(s, "ackLost")` (fake provider accepted and returned state has `providerSubmissions=1`) but simulate result loss by retaining the original persisted `s` (submissions=0). Submit `noSend` with current `goodNoSend()`, then `release`. Actual outcome:
`fakeProviderAccepted=1; projectPersistedSubmissions=0; noSend=VerifiedNoSend; release=Released; completed=true`.

The model's admitted inability to authenticate a fake proof makes this a **harness soundness gap**. It cannot distinguish a truly absent provider submission from an unobserved accepted submission and therefore cannot independently verify its own claimed no-send safety invariant under lost ACK. The separate account Git repository does not repair the absence of independent provider state.

**Bounded remediation candidate:** Model provider submission and verified no-session evidence in an independently persisted disposable fake-provider boundary (not the project ledger). After an accepted send with lost acknowledgement, the project must remain uncertain and the separate provider must deny no-session; no-send and release must fail. Exercise that negative and valid pre-send no-send in generated multi-client/restart traces, with both project and fake-account release checks. Preserve source lease and no production effects. Net-new architectural changes to real StageB/B2 are not a condition of this test-only finding.