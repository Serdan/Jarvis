# P1 — Failed heartbeat leaves in-flight command running unfenced, then masks its terminal result

**Frozen:** `e8a14cb55c45ec297c46fd74f72e14127a0697d9`, `Client/SignalR/Client.fs`.

## Code-path proof

- `withActivityLeaseHeartbeat` (approx lines 214–244) starts a background renewal every two minutes. When `renew()` returns `Error`, the background task sets `renewalFailure` and cancels its **own timer token only**; the delegate `work(): Task<'t>` keeps running until its natural completion. There is **no cancellation token or stop gate passed to work**.
- An already admitted request is protected only through the canonical lease stored in `ReviewAgent.ActivityLeaseUntilUtc` (five-minute window). If renewal fails and that lease expires while work is still executing, an administrative correction with otherwise fresh versions is allowed by `ReviewWorkflow.fs` `activityLeaseRemaining` check. This reintroduces the unsafe correction-while-working state after a transient network or CAS failure.
- After `work()` eventually completes, `processIncomingCommand` (approx lines 799–823) discards its original `returned` on any heartbeat failure and substitutes `Error(Client.ValidationError("Working activity lease expired or could not be renewed ..."))`. A command may already have successfully written a source file or committed a canonical ledger mutation, but the caller receives only the lease-validation failure, **without its real success payload, operation receipt, or result-derived `ChangedAgentIds`**. The latter disappear from `AgentStateChanges`, apart from any unrelated earlier implicit activation.
- Existing new test `WorkingLeaseHeartbeatSurfacesRenewalFailure` checks `failure = Some "Lease CAS rejected"` after an uninterrupted task returns 42, which actually demonstrates that the admitted work continues despite failed heartbeat; it does not test state-protection or safe terminal evidence when work commits.

## Required

Specify and enforce an in-flight liveness contract under failed renewal. Either ensure a canonical activity fence cannot expire before the active operation is safely stopped (with effective cancellation/failure discipline) or use an independent outstanding-operation record/correction veto. Do not report a committed operation as a generic failed request and discard its terminal evidence; explicit uncertain-outcome/recovery semantics and original operation IDs are required if result delivery cannot be authoritative.

Add a deterministic test with injectable clock and blocked long-running mutation: start with valid lease, force renewal failure, advance past canonical expiry while tool remains admitted, attempt operator correction; it must **not** change Working to Ready. Release the mutation and assert correct committed outcome/evidence or typed uncertain-outcome recovery, not an ambiguous generic validation error. Include the path where a renewed activity lease fails after the mutation has already committed.

**Severity: P1** — remaining in-flight correctness hole and possible loss of committed mutation evidence. Source-path proof; no claim of observed production corruption.
