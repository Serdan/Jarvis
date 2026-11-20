# Independent re-review — Scheduler Phase 1 Challenge projection corrections

**Verdict: Accepted**

- **Review request:** `c2d0f7a1e8b44a9f93d51b0a6f72e4c8`
- **Frozen source:** `34facab5db97b2f238d2415c8f7ae6b59866b754`
- **Declared base:** `95a471fc565b57fe309c65fc85f371693dbe9067`
- **Target ref:** `refs/heads/main`
- **Reviewer:** Reviewer 1

## Scope and provenance

Independently inspected the exact frozen source, the correction delta from the previously reviewed commit, the Phase-1 task contract, the canonical Challenge workflow in the declared baseline, and the corrected planner/tests/docs. The source worktree was clean at the exact frozen SHA; the declared base is an ancestor. The correction delta changes only the three intended planner, test and design-document files.

The two prior ChangesRequested findings are resolved. No remaining blocking findings.

## P1 — Canonical N → N+1 Challenge requeue provenance

The normalized projection now distinguishes the immutable wait-index generation from the current attempt assignment generation. The wait is joined to the attempt by project/task/attempt identity, not by requiring those generations to be equal. For a dependency-linked Requeued wait, the planner validates the canonical action, Blocked → Queued phases, owner identity on both sides, positive ledger sequence, operation/time evidence, PreviousGeneration = immutable waitIndexGeneration, NewGeneration = PreviousGeneration + 1, and current attempt generation = NewGeneration. It also verifies the current owner assignment against the attempt generation and requires all linked dependencies satisfied. Pending/Eligible waits still require the immutable index generation to equal the blocked attempt generation.

The canonical baseline confirms the contract: AttemptChallengeWait has no generation field; AttemptChallengeWaitIndex carries AssignmentGeneration and is an immutable reverse-index locator (ReviewWorkflow.fs:347-366). The automatic wake path increments the attempt generation and writes the Requeued wait and lifecycle event without rewriting that index (ChallengeWorkflow.fs:815-877); owner-release reconciliation does the same (ReviewWorkflow.fs:862-899). The lifecycle event records PreviousGeneration and NewGeneration (ReviewWorkflow.fs:203-218).

The corrected regression has a before/after trace with wait index generation 4, attempt/owner/source-lease generation 5, and lifecycle provenance 4 → 5. It asserts a single Ready worker candidate and no planner requeue intent. Malformed provenance is rejected without a candidate. I also ran an independent negative probe with a valid 3 → 4 lifecycle transition and current attempt generation 4 paired with wait-index generation 4; the planner correctly returned InvariantViolation and zero worker candidates because the immutable index did not equal PreviousGeneration.

The updated Phase-1 document now specifies the canonical-to-normalized mapping and the raw index/lifecycle join, including the distinction between historical index entries and the current wait.

## P2 — Zero-dependency manual hold

A zero-dependency wait with a null wait-index generation is now represented as ManualBlocked / ChallengeManualBlock while Pending and the attempt remains Blocked. Empty dependencies are not treated as vacuously satisfied. The hold is added to the blocked-attempt key, so it produces no worker candidate and no planner requeue intent.

The canonical baseline explicitly defines an empty dependency list as a manual block (DurableAgentChallenges.md:81) and deliberately writes no reverse-index entries for it (ChallengeWorkflow.fs:1730-1759). Its only recovery path is the explicit owner-attributed requeue operation (ChallengeWorkflow.fs:1797-1868), which validates the wait owner and advances the assignment generation. The corrected planner requires the manual requeue actor to equal the wait owner, validates the canonical requeue provenance and N → N+1 transition, and verifies the new owner assignment. The new regression verifies the hold, no candidate/no intent, successful owner-attributed requeue, and rejection of a different actor.

## Verification

- `node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-ledger-planner.test.mjs`: **51 passed, 0 failed**.
- `node --check scripts/jarvis-ledger-planner.mjs`: passed.
- `node --check scripts/tests/jarvis-ledger-planner.test.mjs`: passed.
- `git diff --check 95a471fc565b57fe309c65fc85f371693dbe9067..34facab5db97b2f238d2415c8f7ae6b59866b754`: passed.
- Additional independent wait-index/provenance mismatch probe: rejected with InvariantViolation and zero worker candidates.
- The canonical F# workflow was source-inspected but not executed in this re-review; no F# test pass is claimed. The reviewed correction changes only JavaScript planner/tests and documentation.
- Final worktree check: exact frozen SHA and clean.

## Integration boundary

The local main worktree is at `d702352ef705f9991ac1cb14dc15a13cce78aa44`; the reviewed frozen commit is **not** an ancestor of that main snapshot. This Accepted verdict applies only to the immutable source at the declared old base. It is not merge or integration authority. Re-verify ancestry and recompose/re-review against the current main before integration.

No source modifications, merge, deployment, live schedule changes, agent launches, or legacy Manager/Health activation were performed.