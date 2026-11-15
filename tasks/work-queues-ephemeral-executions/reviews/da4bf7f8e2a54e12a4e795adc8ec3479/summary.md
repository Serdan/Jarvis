# Reviewer 2 — Independent frozen architecture review

**Verdict: ChangesRequested — one P1 blocker in the external integration-effect exception.**

- Review request: `da4bf7f8e2a54e12a4e795adc8ec3479`
- Epic: `work-queues-ephemeral-executions`
- Frozen source: `05518449e126f2a312e2646cb73786a9be7337c8` on `docs/work-queues-design-corrections`
- Main baseline: `1481bfa03aea4def721d2b24835bff9b9076160a`
- Scope: documentation-only architecture; no implementation, source modification, integration, release, scheduler activation or deployment authorized.

## Verification and independent inspection

Verified the registered review-corrections worktree is clean and `git rev-parse HEAD` equals the exact frozen SHA; `git diff --check 1481bfa..05518449` exits 0. Read the complete 567-line architecture spec, independently examined the full prior-F05 corrective diff `c78ab56..05518449`, the previous Reviewer 1 finding and negative-space cases 1–73. These cases are **specified future fixtures**, not tests run against an implementation.

## F05 correction assessment

The original decided-review counterexample **is addressed** in the reviewed document:

1. `SetHardPrerequisites` checks authoritative canonical review/integration semantics in the same project CAS, not merely `work.status=Claimed`; a committed verdict or final integration receipt rejects new unsatisfied hard edges as `DomainAlreadyFinalized` (§5, §7).
2. Gate-first `DecideReview` and normal `CompleteIntegration` compare current graph, claim and gate generation; they cannot emit a verdict/downstream producer after the gate wins. Decisive-domain-first marks `DomainFinalizedTerminalPending`, preventing ordinary Yield/Block/reopen (§7, §10).
3. A gated *undecided* review safely releases both canonical review ownership and projected work claim with the same CAS. A *decided* review gets terminal-only CompleteWork or independently evidenced non-agent terminal recovery; account token release follows exact project proof, not a verdict alone (§7, §10).
4. Cases 66–70 and 72–73 cover both review/integration CAS orders, lost verdict reply, final-integration-before-edit, stalled decided review and two-clone claim/version fences.

## Blocking finding

**F06 / P1 — External push crosses a newly committed hard dependency gate; downstream readiness is not fenced.** An integration push dispatched before the gate can succeed *after* `DependencyGatePending` commits. The special evidence-bound reconciler correctly cannot undo Git and must register the exact external effect, but as specified it can turn W into ordinary canonical `Integrated`/`DomainFinalizedTerminalPending` and publish downstream work while the new hard prerequisite B is still unsatisfied. Remote ancestry cannot establish which independent CAS/push completed first. Merely asking Lead to create successor work *if necessary* does not provide an atomic hold/waiver/remediation for already-dispatched downstream work. The hard edge's canonical disposition is likewise unspecified.

Detailed interleaving, normative repair and missing after-gate-commit tests: `findings/f06-inflight-push-dependency-gate.md` (§7 lines 246–251; §5 166–176; cases 69–71).

## Other architectural boundaries reviewed

The document consistently retains no model Manager, deterministic blocking-first FIFO with complete compatible demand-credit matching, advisory stage-level SCC warnings, single account-global capacity CAS and C01 abort-only project tombstone, UUID creation at first actual session connection, one-shot browser dispatch, registered/no-claim/unregistered lease recovery, one canonical source writer and reachable WIP handoff, primary Role-message completion versus ancillary Task/Role handling, passive Task messages, exact frozen SHA integration, and fenced account/project migration epochs. I found no additional blocking defect in those reviewed contracts. Prior review history remains superseded/retained as the spec says.

## Disposition

**ChangesRequested.** Specify a canonical atomic exception disposition for gate-crossing external integration effects that preserves the recorded remote result **while holding downstream readiness or requiring an explicit versioned waiver/remediation**, and add the after-gate push/late-acknowledgement two-client cases. Submit a fresh immutable source SHA and review after correcting F06. Do not alter the frozen source or treat this review as production authorization.

**Review documents and verdict only; no implementation or source changes.**