# Independent P1 re-review — bounded canonical Working activity lease

**Verdict: RequestChanges — two P1 remaining liveness/admission defects.**

Frozen `e8a14cb55c45ec297c46fd74f72e14127a0697d9`, previous rejected review `89a50286124147cf9bdd99657d102f44` at frozen `f68449e6975f06b787d82554ebfd65a4b565bfa9`. Base `ecb59e2272c7e6c4e127345f5034695c2f6e5465`, branch `feature/admin-working-ready-correction`.

## Strengths and scope

- Added optional historical-compatible `ReviewAgent.ActivityLeaseUntilUtc`, bounded canonical five-minute activity lease, renewal when less than two minutes remain, and a two-minute heartbeat for long-running ordinary calls; operator correction now rejects an unexpired canonical lease even at latest agent version. Lease changes do not reset `WorkingTurnStartedAtUtc`, `StateEnteredAtUtc`, task/attempt/checkpoint/owner, or forge worker checkpoint evidence.
- `recordWorkingActivityAt` executes under verified LedgerStore transaction checking same active Started assignment, owner and Working state, and increments canonical agent version. Ingress invalidates the local version cache on lease write. Cross-clone regression exercises stale CAS and live lease, bounded no-extra-write on a quick repeated self-read, local observer read without target attribution. Malformed canonical lease is conservatively treated as active for administrative correction.
- Review inspected exact committed diff `f68449e..e8a14cb` rather than the owner worktree's subsequently dirty `Client/Modules/ReviewWorkflow.fs` and `Client.Tests/ReviewWorkflowTests.fs`. An isolated reviewer detached worktree at `/tmp/jarvis-reviewer1-admin-frozen-e8a14c` was created to build/test exact frozen SHA; no owner source modified.
- `git diff --check ecb59e2..e8a14cb` exit 0; `git merge-tree --write-tree main e8a14cb` exit 0 (tree `4be2f296944f7063cb7d7e5805b7b9aa3d6d208c`); main compatibility at inspection point clean.
- Independent frozen **Server Release: 128/128 passed**, job `75135900b5f4422dbb1a28ae2d46739f`. Worker 3 reported independent focused 7/7 passed. Independent frozen full Client job `236cbaec27344d4ca8cf21110a16e3f3` exposed an unrelated `BoundedProcessTests.pipe_holding_descendant_is_deadline_bounded_and_reaped` failure ("Descendant must actually have started"), and was still running at review decision; do **not** misrepresent it as a passing suite or attribute this external process-launch timing test to the lease patch. The two source-path blockers are review-decisive without a passing full suite.

## Blocking finding 1 — P1

`findings/p1-owner-transition-skips-activity-fence.md`.

`Client/SignalR/Client.fs` exempts `TaskWriteCommand` `CheckpointAttempt`, `YieldAgent`, and `CompleteAgentWork` from **both** canonical ingress lease establishment and in-flight heartbeat, based on an unproven assumption that they finish promptly and modify the agent version on completion. Existing Working agents with no lease (intentional backwards-compatibility) or expired leases can initiate a genuine owner operation without a canonical admission/version write. While its remote ledger transaction is still in progress, administrative correction can pass exact CAS and see missing/expired lease, switching active worker to Ready. A later worker CAS conflict is too late to prove the correction accurately classified idle work. Include **all** admitted actual-worker commands in a coherent fencing contract, without breaking exact-version parameters, and concurrency tests for all three transitions and legacy/expired leases.

## Blocking finding 2 — P1

`findings/p1-heartbeat-failure-unfences-work-and-masks-outcome.md`.

In `withActivityLeaseHeartbeat`, failed renewal cancels only background renewal timer, **not** the admitted `work()`. Its canonical lease can then expire while work continues; admin correction can win. After work completes, `processIncomingCommand` replaces an original success/commit result with generic lease `ValidationError`, discarding its response payload and result-derived changed agent IDs. Retrying an uncertain source/ledger mutation based on the generic failure can be unsafe; previous tool-response correctness requirements demand precise evidence. Establish an in-flight veto/actual cancellation or other authoritative fence that survives transient renewal failure, and preserve terminal/uncertain outcome semantics. Test failure after a real mutating operation has started and after it commits.

## Decision and boundaries

**RequestChanges**, no integration/deployment. Re-submit a new frozen source after both defects are corrected and negative-space concurrency tests are added; independently confirm the full Release suites and measure lease Git read/write cadence. Cross-device and pre-existing legacy Working cases matter. The frozen commit is technically isolated; later uncommitted source changes are not part of this verdict.
