# Reviewer 4 — independent Scheduler Phase-2 re-review: ChangesRequested

**Frozen review:** `435bd6824cd74c23b989709d667a656c`; task `scheduler-phase2-native-dispatch-20261121`; reviewed source **`ef854433852b95a76b31d7af1465c5e08262a3a7`**; base `d702352ef705f9991ac1cb14dc15a13cce78aa44`. Independently claimed sequence **9767**, distinct from Worker 6's implementation. Earlier `4c3ce640aa1b45329715017243001e85` (frozen `6360e1a5...`, ChangesRequested decision 9728) was neither reclaimed nor altered.

## Provenance and patch

Registered isolated `scheduler-phase2-native-dispatch-worker6-20261121` worktree: `git rev-parse HEAD` equals exact frozen SHA before and after checks; clean `git status`. Both `origin` source ref and immutable `jarvis-review/.../435bd682.../r1` remote review ref resolved to that SHA. `git diff --check` base→frozen exited 0. Since the preceding reviewed revision, only `scripts/jarvis-phase2-snapshot-adapter.mjs`, its Node tests, and `docs/scheduler/phase-2-snapshot-adapter.md` changed (+78 adapter lines, +115/-1 tests, +8/-3 docs). Read AGENTS.md, Reviewer role, canonical task/spec, prior finding, new code, tests, documentation. No source editing or publication.

The **previous missing-canonical-Challenge P1 case is fixed**: historical by-challenge indices now require schema/ID/task-scope-compatible canonical Challenge and earlier Created event, plus block and requeue events. Authored test preserves valid historical indices and rejects absent, foreign, mismatched and late Challenges. No live Scheduler/launch effect; default-off preparatory module still returns unknown authenticated project membership and worker/reviewer capacity.

## Actual independent gates on frozen SHA

- **Node v26.13.0:** `node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs` — **72/72 pass**, exit 0; job `4abe993bf49642e99745b14a915795e1`. Real disposable bare-Git account/two-project fixtures and default-off checks executed.
- **.NET SDK 11.0.100 Release Client:** filter `WorkQueueSchedulerHeadBindingTests|WorkQueueSchedulerHostLoopTests` — **21/21 pass**, exit 0; job `8af4ae9908f64f2f836c239dc17072f4`.
- **.NET SDK 11.0.100 Release Server:** filter `Scheduler` — **55/55 pass**, exit 0; job `a9b10262f6a542188637f76c5aca8f8e`.
- **Independent ephemeral adverse test** against actual module/real disposable bare Git: valid historical N3 index remained complete and a nonexistent historical Challenge failed closed, but the injected out-of-order requeue rejection **failed**: 2 pass, 1 fail, OS exit 1, marker `REVIEWER4_OUT_OF_ORDER`.
- **Tightened independent adverse test** with project `ledger.json.nextSequence=101` and genuine canonical Challenge creation: gen3 block seq96, gen3→4 requeue **seq99**, while current gen4→5 requeue **seq98**; adapter returned `completeness.challenges:true`, Challenge diagnostics `[]`, `waitIndexGeneration:4`, `sideEffects:0`. Rejection assertion failed, **0 pass / 1 fail**, OS exit 1; marker `REVIEWER4_CHRONOLOGY_PROOF`. This was confirmed after correcting an initial test-harness-only missing import.

## P1 blocker and verdict

**`findings/historical-requeue-sequence-inversion.md`** records detailed reproduction and repair requirement. At `scripts/jarvis-phase2-snapshot-adapter.mjs` lines 393–419, a historical N block and N→N+1 requeue are checked only against one another. They are **not ordered against the already-proven subsequent-generation requeue**; the reader therefore accepts a generation-3→4 event at seq99 after generation-4→5 at seq98, an impossible generation chronology, and reports false Challenge completeness. Require chronologically ordered generation lineage and a bare-Git regression, while retaining authentic old histories and the newly fixed nonexistent-Challenge check.

**Decision: ChangesRequested, P1.** Green authored Node and .NET tests do not override independent red negative-space evidence. This is a default-off read-only preparation blocker, **not** a claim of live browser effects or production approval. Authenticated cross-project capacity/membership, original-browser reservation/dispatch authority and Phase-1 integration remain separate unmet dependencies. No source mutation, merge, push, deploy, activation or discretionary notification; only this one review claimed and documented.
