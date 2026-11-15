# F05 — P1: DependencyGatePending can reopen a decided review or strand its terminal receipt

**Frozen source:** `c78ab56ed0dbf6e499f8a3f2c7263cfbfc06dfc4`
**File:** `docs/specs/work-queues-and-ephemeral-executions.md`
**References:** §5 line 166; §7 lines 244–246; §10 lines 315–328; acceptance cases 31, 60–63.

## Counterexample (legal under the stated transitions)

1. Reviewer R claims frozen review work item W. The canonical review and W are Claimed together (§10).
2. R commits `DecideReview(Accepted)`. The canonical verdict and downstream integration-policy producer are committed, **but W is explicitly still Claimed/verdict-recorded/terminal-pending** and R's launch still occupies its token (§10). `CompleteWork(W)` has not run.
3. Lead commits `SetHardPrerequisites(W, requires B)` while B is unsatisfied. §7 tests only whether the target stage is Claimed (gate) or already Completed (reject); W remains Claimed, so it enters `DependencyGatePending`, and `CompleteWork(W)` is refused.
4. `ResolveDependencyGate` is specified to end the launch and mark W **Blocked or Available**. That violates §10's invariant that a decided review must never be reopened/requeued. Refusing that resolution instead leaves the claimed W/launch and global token stuck, despite an irrevocable canonical verdict. The newly produced integration stage may already be visible.

This is **not** the ordinary `CompleteWork`-first race in test 61: `DecideReview` is an independent authoritative domain commit that wins *before* work terminalization. §10 specifically preserves this gap to avoid inferring launch termination from a verdict.

## Required correction

- Define a **semantic terminal boundary** for `SetHardPrerequisites`, not merely `work.status == Completed`. A review with an already-recorded canonical verdict (and an integration item with finalized canonical integration evidence) must either reject a newly unsatisfied hard prerequisite on its existing stage generation and direct Lead to an explicit successor/correction stage, or have an explicitly consistent non-reopening finalization path. Do not force a decided review to Blocked/Available.
- In the opposite CAS order, when a hard prerequisite gate wins before `DecideReview`/`CompleteIntegration`, specify and enforce that **all decisive canonical domain writes** and follow-up producers check the gate/graph generation, not only generic `CompleteWork`. An integration push already in flight must be journaled/reconciled as an external side effect; do not claim to undo it with a project CAS.
- Specify recovery for lost `DecideReview` acknowledgement, gate insertion and `CompleteWork` racing across devices. Preserve one immutable verdict, its exact frozen SHA and one downstream producer; release the global token only on an independent terminal receipt.

## Negative-space fixtures

1. **Decide-first, dependency-second:** Commit Accepted verdict while W remains Claimed. Attempt unsatisfied hard-edge insertion before `CompleteWork`; reject/redirect to successor or safely finalize without requeueing decided review. Assert exactly one verdict and token eventually releasable.
2. **Dependency-first, Decide-second:** Commit `DependencyGatePending` on claimed undecided review; racing `DecideReview` fails its graph/gate CAS and creates no Accepted/ChangesRequested downstream event until explicit safe policy resolution.
3. **Lost acknowledgement / two clones:** Original verdict push succeeds but its reply is lost while Lead attempts new prerequisite and R retries CompleteWork. Reconcile same receipts, never duplicate integration demand or trap a terminal-pending review.
4. **Integration equivalent:** `CompleteIntegration` remote ancestry/receipt is final while work claim remains open, then a new hard edge arrives; avoid retroactive reopening. If hard edge wins first, do not authorize a fresh merge/CompleteIntegration from stale generation; resolve an already-unknown push before requeue or release.

**Disposition:** **P1 / blocking.** F04's new source-writer gate is directionally correct, but its currently claimed review/integration terminal-pending states are not covered by the stated edit/resolve rules. Requires a new frozen source and independent review; documentation-only finding.