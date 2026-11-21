# P1 — Uncorroborated historical by-challenge index still passes canonical Challenge completeness

**Task:** `scheduler-phase2-native-dispatch-20261121`  
**Review request:** `4c3ce640aa1b45329715017243001e85` (Reviewer 4)  
**Exact frozen source:** `6360e1a591c19b36cc028f45677697d7b367f50a`  
**Code:** `scripts/jarvis-phase2-snapshot-adapter.mjs` lines 216–232, 299–329, 339–365.

## Independent adverse reproduction

Using the *actual frozen module* and the authored test file's real three disposable bare-Git account/two-project fixture helpers, imported **in memory** with an additional independent assertion; no worktree/source edits:

1. Begin from a valid `N=4 → N+1=5` requeue. The canonical `tasks/alpha/attempts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/challenge-wait.json` lists only Challenges `cccccccccccccccccccccccccccccccc` and `dddddddddddddddddddddddddddddddd`, whose canonical Challenge records and matching immutable generation-4 owner/dependency indices exist. The audited requeue is valid and the active assignment is generation 5.
2. Add a well-formed **historical generation 3** index at `challenge-waits/by-challenge/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/alpha/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/3.json`, containing `schemaVersion:2`, `taskId:"alpha"`, `attemptId:"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"`, `ownerAgentId:"Worker 1"`, `assignmentGeneration:3`.
3. There is **no** `challenges/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/challenge.json`, the index's Challenge ID is not in the canonical wait dependencies, and there is no canonical historical wait/audit corroboration for this unrelated Challenge.
4. Commit/push that fixture to its disposable local bare project Git remote and run `captureCanonicalSnapshot` using the independently supplied exact authority heads.

**Actual result:** `snapshot.projects[0].completeness.challenges === true`; challenge-scoped `diagnostics: []`; the projected wait has `waitIndexGeneration:4`. `phase2DefaultOffDiagnostic(view).sideEffects === 0`. The independent negative assertion expecting `completeness.challenges === false` **failed** (`true !== false`), OS exit **1**; in-memory adversarial invocation completed **38/39 tests passed, 1 failed** (the injected negative). Its stderr logged `REVIEWER4_EVIDENCE` with the exact orphan path, `challengeExists:false`, `completeness:true`, `diagnostics:[]`, `waitingIndexGeneration:4`, `sideEffects:0`. The unmodified authored Node combined suite separately passed **64/64**, exit 0.

## Cause and impact

The new global scan correctly detects absent canonical waits, future/new-generation indices and basic owner/attempt/generation contradictions. However, its `ReverseIndexIdentityMismatch` predicate at lines 355–360 validates a `by-owner` locator against the owner but **never validates the `by-challenge/<challengeId>` locator** against an actual canonical Challenge/dependency or relevant historical source. Lines 362–365 accept *any* lower generation as historical after those incomplete checks. Consequently an arbitrary uncorroborated old Challenge index reports **complete** canonical Challenge evidence.

This is the same P1 false-completeness class as the previous review's orphan reverse-index blocker, but outside the now-repaired current/N+1 cases. The adapter is presently default-off and performs zero dispatch; this finding does **not** claim a live browser effect. It does block representing this snapshot as complete and integrating it as trusted Scheduler preparation.

## Required correction

Reconcile historical by-challenge indices to their canonical Challenge identity and appropriate historical authority (at least reject a missing/identity-conflicting `challenges/<id>/challenge.json`; require whatever valid historical proof the ledger contract actually exposes), rather than treating every older task/attempt/owner-matching record as automatically legitimate. Add a real disposable bare-Git negative regression for an arbitrary historical by-challenge index with no canonical Challenge, while preserving genuine historical indices and valid N4→N5 requeue/default-off/no-effect behavior. Do not change the frozen source or conflate a green authored suite with this adversarial acceptance gate.
