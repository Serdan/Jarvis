# Reviewer 1 — Independent frozen architecture review

**Verdict: Accepted (documentation only).**

- Epic: `work-queues-ephemeral-executions`
- Review request: `a65e09f0b6d94ca6b6c2d81dd5a3487e`
- Exact reviewed source: `90ed868d04524d0483ccd979cffb5029d8cf7027`, `docs/specs/work-queues-and-ephemeral-executions.md`, branch `docs/work-queues-design-corrections`
- Earlier F06/P1 rejected source: `05518449e126f2a312e2646cb73786a9be7337c8`; review `da4bf7f8e2a54e12a4e795adc8ec3479`
- Main baseline: `1481bfa03aea4def721d2b24835bff9b9076160a`

## Independent inspection

Confirmed the registered correction worktree's `HEAD` matches the exact frozen SHA and is clean. Read the complete current 587-line architecture document and compared the corrective diff with `05518449`; `git diff --check 05518449..90ed868d` returned 0. Read the prior F06 finding and rejection summary. Reviewed the normative graph/claim/launch transitions, source/journal recovery, integration producers, operator message/role and global capacity boundaries, and specified acceptance cases 1–80. These are future deterministic fixtures, **not** executed implementation tests.

## Why the previous P1 is closed

- **Gate CAS is authoritative:** `SetHardPrerequisites` on a still-unfinalized in-flight integration adds a versioned `IntegrationGateHold` in the same project ledger CAS as the unsatisfied hard edge, tied to original push intent and graph revision. All downstream producers, WakeWork, claim/admission, credit rematching and cross-project coverage must fail closed until canonical authorization (§5, §7).
- **Physical Git effect is separate:** An original pre-gate push may land after the gate, or only be acknowledged later. `ReconcileGatedExternalPush` records `ExternalEffectObservedPendingGate`, never ordinary `Integrated`/`IntegrationAuthorized` and never replays the push; uncertain side effects retain RecoveryBlocked/source/token fences. Original launch termination and account release require independently proven project terminal evidence (§7, §9).
- **Explicit one-time gate closure:** `ResolveIntegrationGateException` requires verified satisfaction of every original prerequisite or an audited versioned waiver/replan, disposes edges in one project CAS, and emits exactly one final integration outcome and one `IntegrationAuthorized` producer. Remote ancestry alone does not wake downstream work. Races compare graph, domain, claim and hold versions (§7, §10, §12).
- **Negative space:** Cases 74–80 cover gate-first push, delayed acknowledgement, unknown push, cross-client racing reconciliation, waiver, post-prerequisite closure, already claimed dependents and incomplete cross-project indexes. Prior F01–F05 corrections remain consistent with the new F06 contract.

**No blocking findings.** See `findings/f06-closure-assessment.md` for interleavings and one **nonblocking** Phase A precision recommendation: explicitly restate safe old-activation/mutating-job reconciliation as a typed prerequisite for exception resolution, already mandated by §7 line 259 and §9 source-safety rules.

**Disposition:** Accept the exact frozen source **as a design specification only**. This does not approve implementation, merging, migration, scheduler activation, release, deployment, restart or any live permission change. Subsequent implementation packages still require reviewed exact-source integration, their own tests/reviews and explicit operator authorization for production activation.
