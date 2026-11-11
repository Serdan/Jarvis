# P1 — Worker-owned checkpoint, yield and completion calls bypass canonical activity admission

**Frozen source:** `e8a14cb55c45ec297c46fd74f72e14127a0697d9`; independent review `6fd76f4209ba4f75a86b05426e93803e`.

## Precise path

- `Client/SignalR/Client.fs:109–116`: `activityFencedByCommand` returns **true** for same-actor `TaskWriteCommand` actions `CheckpointAttempt`, `YieldAgent`, `CompleteAgentWork`. The comment assumes these owner writes complete promptly.
- `activateReadyOnActivity`, in its **already Working** branch around lines 190–206, calls `recordWorkingActivity` only when `not (activityFencedByCommand agentId command)`. For those owner writes it returns `Ok([],None)` without checking/renewing a canonical lease.
- `processIncomingCommand`, around lines 770–805, applies `withActivityLeaseHeartbeat` only when `not (activityFencedByCommand localAgentId command)`. Thus those three commands never start a heartbeat either.
- `Client/Modules/ReviewWorkflow.fs:2094`: administrative correction rejects an agent only if its **currently stored** `ActivityLeaseUntilUtc` is still live. Old/legacy Working agents may have no lease (the new field is intentionally optional on old JSON), and existing leases do expire after five minutes. There is no admission or in-flight marker to invalidate the operator's observed versions at the start of those three worker calls.

## Deterministic concurrency scenario

1. An agent has a real assigned Working attempt with a null or elapsed canonical lease. Operator observes exact current task/attempt/agent versions/generation.
2. The **real owner** starts `checkpoint_attempt`, `yield_agent`, or `complete_agent_work`. The client admits the call without renewing the lease or changing the canonical agent version; its remote ledger command may block or wait, since completion time is not guaranteed.
3. Before that owner's own transaction commits, the operator's `CorrectAgentWorkingState` transaction sees the same versions, Working state and expired/missing lease, and **succeeds** at setting the actively working agent to Ready.

The owner write may then fail optimistic CAS, but this does not undo the incorrect administrative judgment that a genuinely active agent was stale. This is the core acceptance invariant from the previous rejected review, not merely a later version-conflict symptom.

## Required

All genuine Working-agent ingress, including these owner transitions, must establish a canonical or otherwise authoritative in-flight fence **before** executing the operation, without invalidating an API caller's exact expected-agent-version assertions. For example, separate lease epoch/version from agent's normal CAS counter or coordinate an atomic pre-write reservation with final operation. A declaration that certain Git-backed operations finish promptly is not an enforceable timing guarantee.

Add real concurrent negative tests starting from **legacy/no lease and just-expired lease**: launch a blocking `CheckpointAttempt`, `YieldAgent`, and `CompleteAgentWork` using the genuine ingress path; attempt correction with otherwise fresh versions while each remains in flight; assert correction fails and no administrative audit event is created. Test multi-clone visibility.

**Severity: P1** — residual admission hole in the central purpose of this feature, not a reported production incident.
