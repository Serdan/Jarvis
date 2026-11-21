# P2 advisory — strengthen negative-space proof fixtures

Scope: exact frozen source `8e1ce76995bd2377d1d4e4ec58dd831e671e1889`; this is **nonblocking coverage debt**, not evidence of a production failure.

The nine real bare-remote Client.Tests cases exercise first and advancing writes, two-clone convergence, replay, out-of-order time, missing proof, account/project/device/generation mismatches, a bad timestamp/event ID, unknown publication, unreachable remote suppressing tool effects, and persisted proof after tool failure. Server.Tests exercise explicit capability denial and the authenticated event envelope.

The current tests do **not** deliberately corrupt an existing activity JSON blob, forge/mismatch its Git operation receipt or origin, advance its sequence out of range, or drive an actual SignalR disconnect/re-registration while a command is queued. Those invariants are explicitly checked in `AgentActivityLedger.readActivityRecordAt` / `verifyOperationAt` and `validateDispatch`, but would benefit from tamper and reconnect fixtures against disposable bare remotes. A CAS test also starts two writers concurrently without proving a specific retry occurred; fault injection would make this stronger.

Suggested future regressions: mutate the canonical activity blob in a disposable ledger and require `ActivityProofUnavailable` on read; inject out-of-range sequence and changed operation-origin receipt; enforce a stale generation after actual re-registration; force an external-head race and assert retry convergence. Do not couple these to live services or the retired Health/Manager agents.

No source changes requested by this advisory.

Future policy gate: `AgentActivityLedger.readAt` accepts a caller-supplied `serverNowUtc` to calculate `Age`; it validates UTC and time order, not the clock's origin. This is safe as a passive watermark reader while native Health recovery is still blocked. **The later deterministic recovery consumer must not supply the local device wall clock as an authoritative server time.** Require an authenticated/trusted time source (or consume the durable last-activity fact without computing an idle age) before enabling policy; add an explicit forward-skew regression. This is not evidence that the current inactive Health policy misfires.
