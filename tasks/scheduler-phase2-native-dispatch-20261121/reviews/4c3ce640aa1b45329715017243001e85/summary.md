# Reviewer 4 — independent Scheduler Phase-2 re-review: ChangesRequested

**Task:** `scheduler-phase2-native-dispatch-20261121`; request `4c3ce640aa1b45329715017243001e85`; frozen immutable reviewed source `6360e1a591c19b36cc028f45677697d7b367f50a`, base `d702352ef705f9991ac1cb14dc15a13cce78aa44`. Claimed independently from shared Reviewer queue (ledger seq 9720), not the already-decided `69beddf0cc464c51a27d836cd20d211b`. Read the preceding Reviewer 2 ChangesRequested summary and full unmatched-reverse-index finding.

## Provenance and scope

Actual registered isolated worktree `HEAD=6360e1a...` and remote `origin` source/review refs each resolved to that same exact SHA; worktree remained clean after tests; `git diff --check` exit 0. Base-to-frozen scope is exactly three new files: `scripts/jarvis-phase2-snapshot-adapter.mjs`, `scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs`, `docs/scheduler/phase-2-snapshot-adapter.md`. Independently inspected changed code against prior frozen `01265eec90356c0e753d0cb7c5e36f3304b1c348`, canonical task contract, source lease boundaries and earlier adverse findings. No implementation edits, merge, publication, deployment, live launch, runtime activation or other review claim.

## Executed checks, exact frozen source

- Node combined authored `node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-phase2-snapshot-adapter.test.mjs`: **64 pass / 0 fail, OS exit 0**. Job `7d1724a3f7e04752adf4f41f73912ed0`, including real disposable bare-Git account/two-project fixtures, corrected N4→N5 and absent-wait reverse-index cases, original manual/agent/token regressions.
- .NET SDK **11.0.100**, **Release** Client filtered `WorkQueueSchedulerHeadBindingTests|WorkQueueSchedulerHostLoopTests`: **21/21 pass, exit 0**, job `b27d97e09b5d49c2a885fb923d536809`.
- Release Server filtered `Scheduler`: **55/55 pass, exit 0**, job `3fa33282e308474ba309165aa666cfc6`.
- **Independent additional negative-space:** ephemeral in-memory test against frozen module using fresh disposable bare Git remotes and unchanged fixture utilities. **38 pass / 1 fail, OS exit 1**, job `35a14b8ccf084968b4cda3cccced11d8`. The one failure is an additional expected *rejection* assertion, not a test harness error. Stderr contains `REVIEWER4_EVIDENCE` with exact data.

## P1 blocker

**`findings/uncorroborated-historical-challenge-index.md`**: On a valid audited N4→N5 requeue with only canonical Challenges `cccc.../dddd...`, adding an unrelated `by-challenge/bbbb.../alpha/aaaa.../3.json` index referencing **no existing canonical Challenge** silently produces `completeness.challenges:true`, `diagnostics:[]` for Challenges, and `waitIndexGeneration:4`. Expected a fail-closed incomplete result. At `scripts/jarvis-phase2-snapshot-adapter.mjs` lines 339–365, the global scan verifies task/attempt/owner/generation but never corroborates the by-challenge locator's Challenge ID; any lower generation is presumed valid history even when the Challenge does not exist. This is a residual P1 false-completeness evidence-boundary defect distinct from the two repaired N+1/no-wait cases. Correct historical identity/authority handling and add the exact regression without rejecting genuine valid older records or requeue history.

**Verdict: ChangesRequested.** Authored and .NET gates passing does not override the independent demonstrated negative-space false-completeness result. This is only a default-off, read-only preparatory adapter (`enabled:false`, `sideEffects:0`, no candidates). Authenticated account-wide project membership, browser worker/reviewer capacity, reservations, sessions and Phase-1 integration remain unproven; do not treat this review as production Scheduler approval. Structured review feedback only; no discretionary notifications.
