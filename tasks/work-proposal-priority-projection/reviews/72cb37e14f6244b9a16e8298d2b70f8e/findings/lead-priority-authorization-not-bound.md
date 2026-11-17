# P1 — Lead decision proof does not authorize the numerical priority

**Frozen source:** `2d1b1a07c44cd420eb2a418fcc2895343ebc72c1`
**Affected:** `Server/WorkProposal.fs:27-34, 84-90, 168-176, 208-225`
**Target contract:** `docs/specs/work-stateful-orchestration-2026-11-17.md` §§3, 5 and `work-proposal-priority-projection` task.

## Finding
`VerifiedLeadProposalDecision` binds receipt ID, Work ID, record version, spec pin, action and a verified boolean, **but not the Lead's authorized priority**. Both `Approve(decision, priority)` and `SetPriority(decision, priority, reason)` accept a priority supplied separately by the caller. `verifyDecision` checks the receipt's action/version/spec/Work identity, but never checks the separately supplied priority (or SetPriority reason) against the decision evidence.

For the same `Proposed` record at version N and the exact same receipt `{Action=Approve, ExpectedVersion=N, ...}`, invoking `apply N (Approve(proof, 1)) record` and `apply N (Approve(proof, 999)) record` both succeed and publish different `LeadPriority` values under the same claimed Lead authorization. The existing tests only check a valid decision and mismatched *action/spec*, not a mismatched priority. The same omission affects `SetPriority` at an Open record version.

The model/adapter can consequently publish a rank not actually selected by Lead while presenting a verified Lead receipt. This breaks explicit Lead-assigned priority provenance, rather than merely UI formatting.

## Required correction
Bind the full authorized mutation to the trusted decision: exact priority (and changed-priority reason if part of the authorized action), Work ID, expected version, approved spec and action. Either derive the mutation's values from the verified decision or reject command values that differ. The adapter must verify that exact payload before marking the decision canonical.

Add negative tests showing the **same otherwise-valid verified receipt** cannot authorize priority 1 and 999 on the same frozen Proposed state, and cannot authorize a different SetPriority value; retain positive version-conflict, replay and pinned-spec tests. Do not introduce live Scheduler or DB transport in this pure lane.