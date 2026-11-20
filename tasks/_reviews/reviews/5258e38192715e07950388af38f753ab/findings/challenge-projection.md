# Independent review findings — Scheduler Phase 1

**Verdict:** ChangesRequested  
**Reviewed request:** `5258e38192715e07950388af38f753ab`  
**Frozen source:** `5258e38192715e07550388af38f753aba267d9c6`  
**Baseline:** `95a471fc565b57fe309c65fc85f371693dbe9067`  
**Review scope:** Pure planner, normalized snapshot contract, Challenge projection, deterministic selection, tests and Phase-2 boundary. No source edits or runtime changes were made.

## P1 — Canonical Challenge requeue generation transition is not represented or verified

The normalized Challenge contract and tests do not model the generation transition performed by the canonical workflow.

Evidence in the frozen baseline:

- `Client/Modules/ReviewWorkflow.fs:347-366`: `AttemptChallengeWait` has no assignment-generation field. The generation lives in the separate `AttemptChallengeWaitIndex`, whose locator is an immutable reverse-index record.
- `Client/Modules/ChallengeWorkflow.fs:815-842, 872-877`: the canonical wake path verifies the locator against the blocked attempt, then sets the next attempt generation to the prior generation + 1 and marks the wait `Requeued`. The writes update the wait, attempt, owner, assignment issue and lifecycle event; they do not rewrite the immutable wait index.
- `Client/Modules/ReviewWorkflow.fs:862-899`: owner-release reconciliation does the same generation increment and writes the requeued wait/attempt plus a lifecycle event.
- `Client/Modules/ReviewWorkflow.fs:203-218`: the canonical lifecycle event records `PreviousGeneration` and `NewGeneration`.

Planner behavior:

- `scripts/jarvis-ledger-planner.mjs:409-429` uses `wait.assignmentGeneration` to find the attempt and requires it to equal `attempt.assignmentGeneration`, before it can classify a wait as `Requeued`.
- `scripts/jarvis-ledger-planner.mjs:456-460` validates requeue provenance only by operation ID, sequence, actor and timestamp; it does not validate the canonical previous/new generation transition.
- `scripts/tests/jarvis-ledger-planner.test.mjs:412-443, 445-477` model the requeued wait and attempt with the same generation (4) and provenance without previous/new generation. This is not the canonical transition.

I reproduced the mismatch against the frozen planner without modifying files. With the canonical index generation 4 and post-requeue attempt generation 5, the planner reports `InvariantViolation` for the wait while still emitting the Ready worker candidate. If the fixture instead invents wait generation 5, the planner reports `Requeued`, but the original index generation and lifecycle transition are no longer checked.

**Required correction:** Represent both the immutable wait/index generation and the post-requeue assignment generation (or define an equally explicit adapter projection). Carry and validate the canonical lifecycle event's `PreviousGeneration`/`NewGeneration`, tying the prior generation to the wait index and the new generation to the current attempt/assignment. Add a test for a real N → N+1 canonical requeue and update the sample trace. Also document the raw-to-normalized mappings used by the future adapter (canonical wait `Waiting`, attempt phase `Queued`, review status `Unclaimed` versus normalized `Pending`/`Ready`).

This is an acceptance gap because the task explicitly requires the planner to recognize an already-Requeued canonical wait and the resulting Ready assignment without creating another requeue intent.

## P2 — A valid zero-dependency manual block is classified as an invariant violation

The existing canonical workflow explicitly supports manual waits with no linked Challenges:

- `docs/DurableAgentChallenges.md:81, 85` defines an empty dependency list as an explicit manual block and retains the owner-attributed `requeue_challenge_blocked_attempt` recovery path.
- `Client/Modules/ChallengeWorkflow.fs:1730-1759` deliberately treats an empty normalized dependency list as an outstanding manual wait, persists the `Waiting` record, and creates no reverse indexes.
- `Client.Tests/ChallengeWorkflowTests.fs:383-420` has the regression `ManualZeroDependencyBlockCanBeRequeuedExplicitlyAndRejectsStaleOrDuplicateIds`.

The planner rejects this valid state at `scripts/jarvis-ledger-planner.mjs:422-429` because it treats `dependencies.length === 0` as invalid. A read-only reproduction returns `InvariantViolation` / `ChallengeReconciliationInvariantViolation` for the legitimate manual block. It does remain fail-closed (zero worker candidates), but reports valid operator-controlled state as ledger corruption.

**Required correction:** Project this as an explicit manual-block state/hold, distinct from an invariant violation, and add a fixture proving that it remains blocked until canonical owner-attributed requeue. If the normalized contract intentionally excludes manual waits, document and enforce that exclusion at the adapter boundary rather than misclassifying them.

## Verification

- `node --test scripts/tests/manager-heartbeat.test.mjs scripts/tests/jarvis-ledger-planner.test.mjs`: **50 passed, 0 failed**.
- `node --check scripts/jarvis-ledger-planner.mjs`: passed.
- `node --check scripts/tests/jarvis-ledger-planner.test.mjs`: passed.
- `git diff --check BASE..HEAD`: passed.
- Frozen worktree HEAD matched the reviewed SHA, the baseline is an ancestor, and the worktree remained clean.
- The supported hermetic Client.Tests runner did not establish a canonical F# test pass in this environment: it exited 2 with **0 executed tests**. Direct `dotnet test --list-tests` only reported a successful build and no discovered tests. This is not counted as a pass; the canonical workflow evidence above is source-inspected.

The implementation is appropriately default-off and pure: no dispatch, claim, requeue, reservation write, deployment, schedule activation or migration was introduced. The remaining issue is correctness of the Challenge projection contract, not an observed live side effect.
