# F06 / P1 — Gate-crossing integration push can release downstream work while a new hard prerequisite remains unmet

**Review source:** `05518449e126f2a312e2646cb73786a9be7337c8` (docs-only; frozen review `da4bf7f8e2a54e12a4e795adc8ec3479`).  
**File:** `docs/specs/work-queues-and-ephemeral-executions.md`.  
**References:** §5 lines 163–176, especially 166 and 176; §7 lines 246–251; §10 lines 312–315; acceptance cases 69–71 and 73 (lines 535–539).  
**Severity:** P1, design/acceptance blocker. This is separate from the now-corrected decided-review F05 race.

## Concrete interleaving

1. Integration work W is Claimed for a frozen Accepted SHA. The Integrator persists the exact original merge/push intent and submits that external Git push; the push request is still in flight. Canonical integration is not yet finalized.
2. Lead commits `SetHardPrerequisites(W, requires B)` with B unsatisfied. The project CAS legitimately wins first and marks W `DependencyGatePending`. Under the normal rule, no integration completion or downstream work may proceed until this new hard gate is resolved.
3. The *already dispatched* push succeeds **after** the gate commit (or had succeeded earlier but acknowledgement/remote observation was delayed). The project CAS cannot undo the Git side effect.
4. The spec's special evidence-bound reconciler sees the exact accepted SHA in remote target ancestry, records canonical `Integrated`, changes W to `DomainFinalizedTerminalPending`, and terminally completes W. It says the new prerequisite is not applied retroactively and Lead should publish successor/correction work *if necessary*. The ordinary `Integrated` producer (§5, §10) can now release downstream work D on integration evidence even though B remains unsatisfied and no explicit canonical gate-disposition/waiver/compensation receipt has been required.

**Problem:** Verified remote ancestry proves the push happened, **not** whether its effect preceded or followed the independent project-ledger gate CAS. The exception is necessary to record external reality, but its current success transition has no required atomic disposition for the newly committed hard dependency or for dependent-stage admission. A hard prerequisite can therefore be bypassed by an external push that finishes after the gate wins. Leaving the unsatisfied edge attached to a Completed W also makes the graph's dependency/terminal interpretation ambiguous. A discretionary subsequent Lead action is too late to prevent already-runnable downstream stages.

## Required correction

- Model the *external effect* and *workflow authorization* separately. On a gate-crossing pre-dispatch push, the reconciler must idempotently record the exact integrated remote effect **and** a durable outstanding gate/exception disposition. It may terminalize the old launch and release its account token after project proof; it must not erase the fact that B was committed first.
- Define the project-CAS rule that atomically quarantines/blocks any downstream integration-policy work dependent on W, or atomically publishes a mandatory successor/remediation stage with the unsatisfied prerequisite and makes downstream depend on that successor, until B is satisfied or an explicit versioned Lead/operator waiver/replan closes the exception. Make sure the original gate is either retained as an auditable exception or replaced by a versioned historical/successor edge without leaving a silently inconsistent live graph. Do not use remote commit time as proof of cross-ledger ordering.
- Specify race-safe terminal recovery when `ResolveDependencyGate`, the original push-journal reconciliation, `CompleteIntegration`, and a lost Git acknowledgement interleave. Exactly one canonical integration outcome/producer and one launch receipt must survive; failed/unknown push retains RecoveryBlocked. No blind push replay.
- Extend deterministic two-clone acceptance cases: (a) push dispatched before gate but remote side effect lands **after** gate CAS, (b) side effect already landed but its response is lost until after gate, (c) remote status remains unknown, (d) graph/gate-resolution retry races with special integration reconciliation. Assert no downstream work becomes runnable ahead of the unresolved hard prerequisite or an explicit recorded waiver/remediation; token release remains independently proof-bound.

## Not a finding

The new `DomainFinalizedTerminalPending` guard, verdict-first `DomainAlreadyFinalized` rejection, gate-first `DecideReview` CAS failure, decided-review terminal-only recovery, and exact SHA/idempotent verdict producer address the original F05 review-side counterexample. The problem is specifically the permitted post-gate **external integration effect** and lack of a mandatory downstream gate disposition.

**Disposition:** ChangesRequested until the gate-crossing external-effect path and negative-space tests are made normative in a newly frozen design revision. No source edits or implementation authorized by this review.