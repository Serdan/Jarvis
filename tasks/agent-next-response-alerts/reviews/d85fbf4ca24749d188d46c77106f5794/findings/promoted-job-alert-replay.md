# P1 — Consumed one-shot STOP alerts are replayed by completed job polls

**Frozen head:** `3e55b04161acc216131d6d56aad09c32eb3a0195`
**Files:** `Client/SignalR/Client.fs:1120,1199-1233`; `Client/Modules/JobManager.fs:365-375,538-559`; `Server/McpTools.fs:486-500,923-937`

A slow command that auto-promotes to a client-owned tool job still runs `processIncomingCommand` to completion. That function consumes queued operator alerts and puts them into the terminal `AgentCommandResponse.Instructions` **before** the result is returned to a ChatGPT caller. The pending handle has empty instructions. `ToolOperations.get` retains and returns this exact completed `ToolResponse` on each subsequent `GetJobResult` request (for up to 30 minutes). `completedToolEvidence` then unconditionally reattaches `original.Instructions` on every poll.

**Concrete failing sequence:** Start a command that exceeds the 3-second promotion threshold; queue `STOP — yield` before its background execution finishes; let the command finish and consume the alert. Poll `get_job_result(jobId)` twice after completion. Both polls include the same `response-alert` STOP block, although the ledger's pending queue is empty and no new alert was issued. Alternatively, if the agent calls some other tool before polling the completed job, that next visible response lacks the already-consumed STOP. This breaks the documented one-shot/next-response contract (`docs/ResponseAlerts.md`) and may repeatedly halt agents when a completed job is retried for recovery.

The new follow-up test `polledToolJobsRestoreExactInstructionsAndMutationEvidence` covers a *fresh poll-time* alert but uses only `project` instructions in `job.ToolResponse`, missing the promoted-job case above.

**Required:** Move single-use alert delivery to a caller-visible response boundary (or otherwise ensure previously consumed original-job alert blocks are neither replayed nor stranded), while retaining immutable terminal error/mutation evidence and preserving newly consumed poll-time alerts. Add regression coverage for: STOP in the stored original job response; two successive completed polls; and a completed unpolled job followed by an unrelated agent tool call.

**Independent checks:** On this frozen commit, focused Release .NET 11 tests passed: Server.Tests 2/2 and Client.Tests ResponseAlert 5/5. These tests do not exercise this replay sequence.