# Scheduler Phase-1 Challenge projection — independent re-review

**Disposition: No remaining blocking findings.** The two prior ChangesRequested items are corrected in frozen source `34facab5db97b2f238d2415c8f7ae6b59866b754` relative to base `95a471fc565b57fe309c65fc85f371693dbe9067`.

## P1 — Immutable wait-index generation versus current attempt generation

**Canonical baseline evidence**

- `Client/Modules/ReviewWorkflow.fs:347-366`: `AttemptChallengeWait` contains no assignment-generation field. The separate `AttemptChallengeWaitIndex.AssignmentGeneration` is explicitly an immutable reverse-index locator.
- `Client/Modules/ChallengeWorkflow.fs:815-877`: automatic wake verifies the locator against the blocked attempt, increments the attempt assignment generation, writes the wait as Requeued and records the lifecycle event. The write set does not rewrite the wait index.
- `Client/Modules/ReviewWorkflow.fs:862-899`: owner-release reconciliation also increments the attempt generation and records the requeue event without changing the old locator.
- `Client/Modules/ReviewWorkflow.fs:203-218`: the canonical lifecycle event carries `PreviousGeneration` and `NewGeneration`.

**Corrected planner evidence**

- `scripts/jarvis-ledger-planner.mjs:397-424`: attempts are joined by project/task/attempt identity; current attempt generation and immutable wait-index generation are kept separate.
- `scripts/jarvis-ledger-planner.mjs:431-440`: the normalized wait requires an explicit `waitIndexGeneration`; dependency-linked waits require a positive integer, while manual waits require null.
- `scripts/jarvis-ledger-planner.mjs:461-487`: Pending/Eligible waits require index generation = current blocked attempt generation. Requeued provenance validates the canonical action, phases, owner IDs, positive sequence, operation/time evidence, PreviousGeneration, NewGeneration = PreviousGeneration + 1, index generation = PreviousGeneration, and current attempt generation = NewGeneration.
- `scripts/jarvis-ledger-planner.mjs:517-528`: the requeued owner assignment must match the task, attempt and current attempt generation before the state can be accepted.
- `scripts/jarvis-ledger-planner.mjs:549-566`: the projection reports both generations separately and blocks the current assignment key on any hold.

**Regression and adapter contract**

- `scripts/tests/jarvis-ledger-planner.test.mjs:429-479`: the positive fixture keeps the wait index at generation 4 while attempt, owner and source lease advance to 5; the lifecycle event records 4 → 5. It asserts Requeued, both reported generations, one worker candidate and no requeue intent. Malformed event provenance is rejected with no candidate.
- I additionally ran an isolated negative probe: event 3 → 4 and current attempt generation 4, but immutable wait-index generation 4. The planner rejected this as `InvariantViolation` and emitted zero worker candidates, confirming that a valid event transition cannot mask a mismatched index.
- `docs/scheduler/phase-1-ledger-planner.md:147-162` documents canonical-to-normalized mappings, including the lifecycle fields and the rule that the immutable index remains at N while attempt/owner/source lease move to N+1. The sample trace at lines 195-238 shows the concrete 4 → 5 transition.

**Assessment:** P1 is resolved. The canonical generation transition and the distinct immutable-index mapping are represented and validated; the planner observes an already-completed canonical requeue and does not create another.

## P2 — Zero-dependency manual Challenge block

**Canonical baseline evidence**

- `docs/DurableAgentChallenges.md:81` explicitly defines an empty dependency list as a manual block.
- `Client/Modules/ChallengeWorkflow.fs:1730-1759` persists the manual wait and deliberately creates no reverse-index entries for an empty dependency list.
- `Client/Modules/ChallengeWorkflow.fs:1797-1868` implements the explicit recovery path. It requires the wait owner to be the actor, checks the blocked attempt and owner state, then records the Requeued wait and a new assignment generation.
- `Client.Tests/ChallengeWorkflowTests.fs:383-420` covers the canonical manual zero-dependency block and explicit requeue behavior.

**Corrected planner and regression evidence**

- `scripts/jarvis-ledger-planner.mjs:416-440` identifies a zero-dependency wait as manual and requires a null wait-index generation.
- `scripts/jarvis-ledger-planner.mjs:458-460, 489-504` prevents empty dependencies from being vacuously satisfied and projects a Pending/Blocked manual wait as `ManualBlocked` / `ChallengeManualBlock`.
- `scripts/jarvis-ledger-planner.mjs:484-487, 517-528` validates the owner-attributed manual requeue and the generation transition before accepting Requeued.
- `scripts/jarvis-ledger-planner.mjs:560-566` records the hold against the current attempt key; the candidate loop at lines 843-852 suppresses the blocked attempt.
- `scripts/tests/jarvis-ledger-planner.test.mjs:481-526` verifies the manual hold, zero worker candidates, no requeue intent, successful owner-attributed requeue and rejection of a different actor.

**Assessment:** P2 is resolved. A legitimate manual hold is no longer classified as ledger corruption and cannot become runnable through empty-list satisfaction. Only the canonical owner-attributed requeue releases it.

## Verification and evidence limits

- Combined Node suite: 51/51 passed.
- Planner and test `node --check`: both passed.
- `git diff --check` from declared base to frozen source: passed.
- Additional immutable-index/provenance mismatch probe: rejected as expected.
- The canonical F# workflow was inspected at the declared base but was not executed in this re-review; no F# test pass is claimed.
- Reviewed source worktree remained clean at the exact frozen SHA.

No source changes were made by the reviewer. No integration, merge, deployment, schedule mutation, agent launch, or legacy Manager/Health activation was performed. Acceptance applies only to this frozen old-base source; current main at `d702352ef705f9991ac1cb14dc15a13cce78aa44` is not descended from this reviewed commit, so separate ancestry verification and recomposition/re-review are required before integration.