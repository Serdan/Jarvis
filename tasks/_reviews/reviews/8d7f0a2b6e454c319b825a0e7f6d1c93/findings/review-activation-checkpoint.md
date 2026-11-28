# Reviewer 9 activation checkpoint

Review request: `8d7f0a2b6e454c319b825a0e7f6d1c93`.

## Exact source and correction delta

- Frozen head: `7035fe2bc2463c6a8d754a83964166a9a88386c5`.
- Frozen review base: `cba405f32ecbf27161d7838fff9cebb69ffc79f1`.
- Immediate parent: `2bb370139db1be3eb3edcb0c9aa5be15e8662805` (the previously reviewed submission).
- Exact correction delta from that parent: 2 files changed, 79 insertions and 34 deletions; only `qualification-scope.md` and `IngressTests.fs` changed. The original 3-file Stage A footprint is unchanged.
- `git diff --check` passes. The registered worktree was clean at the frozen head before the test run; source reads and diff inspection are read-only.

## Prior exact-SHA test evidence independently verified

- Retained TRX: `/tmp/jarvis-admin-actor-20604-7035fe2bc/admin-actor-7035fe2bc-20604.trx`.
- SHA-256 independently recomputed: `debdf2fa1c52640faf22270b01ca828d92363ba0e15aae0a176ed69ef45f0734`.
- Parsed TRX counters: 21 total/executed, 7 passed, 14 failed, zero errors/timeouts/aborts/inconclusive/not-executed.
- All seven passing cases are the registered legacy-route controls across RegisterAgent, AssignTask, ReissueAssignment, AdoptTaskSpec, ReissueQueuedAttempt, RecoverAgentAssignment and TransferOwner.
- All fourteen failures are the seven intended legacy subject-as-transport-actor assertions and seven intended explicit initiator/subject wire assertions. No cross-label privacy override failure remains.

## Engineering test job (do not poll while running)

- Job ID: `b6bbc19564204ab9931551688ecb5e11`.
- Command: `dotnet test tests/AdminActorSubjectIngress20261127/AdminActorSubjectIngress20261127.fsproj --no-restore --logger "console;verbosity=minimal"`.
- Worktree: `Jarvis::admin-actor-worker4-20261127`.
- Purpose: independently verify the fresh isolated Stage A qualification on exact head `7035fe2bc2463c6a8d754a83964166a9a88386c5`.
- Required terminal evidence: exit code, complete test counters and per-case outcomes, plus TRX path/hash if generated or reported. Do not poll, shorten, or rerun this engineering job. Retrieve and reconcile its terminal result once when available.

## Current static-review evidence and limits

- The canonical original task spec permits Stage A fixture-only qualification and explicitly keeps Stage B/shared production correction held. It requires actor-private GetJobResult and calls for quick/slow/terminal/unknown and AsyncLocal isolation.
- The prior independent review `bf6383d055c9482b9f4e6297d813d64b` identified three fixture corrections. The current diff removes the unsupported cross-label read expectation and asserts unrelated-actor denial; carries the two typed invalid-state controls and no-mutation baselines into explicit mode; and adds explicit successful receipt plus target/caller ledger postconditions.
- Source inspection of `Client/Modules/JobManager.fs` confirms `ToolOperations.authorize` rejects an actor label different from `entry.Handle.AgentId` with `Tool job belongs to another initiating agent.` The current unrelated-label denial assertion is consistent with the original actor-private requirement.
- The exact baseline still exposes subject-only administrative tool signatures. The explicit `agentId`/`targetAgentId` cases are therefore intentionally RED against this frozen baseline; no production change is part of this review.
- The scope note explicitly defers quick/slow/terminal/unknown and concurrent AsyncLocal isolation to full qualification, without claiming broad task completion. These remain unqualified original-task obligations.
- The referenced Lead challenge `d498f96d71d34f34963b983cf0dd5129` could not be independently read by Reviewer 9 (canonical access denied to non-issuer/non-owner); the cited sequence 20566 is not a checkpoint on this task. The source's claimed adjudication provenance is therefore supported here by the frozen scope note and prior review record, not by direct challenge inspection.

## Continuation

Once the engineering job is terminal, retrieve its result once and compare it with the independently verified prior TRX. If it matches and static review remains clean, write the final review summary and submit the verdict for this exact SHA. If it differs, inspect the terminal evidence and record any reproducible issue before deciding.