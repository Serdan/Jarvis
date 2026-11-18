# Scope-fidelity six-consultation pilot — raw independent transcripts

Source packet: `docs/specs/scope-fidelity-consult-pilot.md` on `docs/scope-fidelity-retrospective` at commit `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01` (2026-11-15).

**Method:** Six separate browser conversations (three persona prompts × two cases). The original task criteria appeared before challenged evidence in each *single submitted user turn*. This is **not** the packet's stricter two-message phased disclosure; treat this protocol deviation as a limitation. No historical reviewer verdict or calibration answer was included. Raw user turns and responses are transcribed from the six confirmed conversation pages, not reconstructed. Browser tool reported confirmed matching user bubble + conversation route for every submission, and UI showed "Response complete" on retrieval. Browser launch insertion encountered a whitespace/ProseMirror comparison incompatibility before Send; Lead verified the exact case/role/baseline and empty new-chat route, then clicked Send once per tab and verified durable confirmation. No automatic replay after uncertain sending. Separate Jarvis agent UUIDs were not registered for these consultation conversations; their conversation URLs and tab registrations are the session handles. Model/effort controls were not pinned; record the UI-observed effort below. Host-reported input/output tokens, precise per-run latency, and comparable tool/coordination costs are **unavailable**, not estimated. Do not claim a controlled quantitative experiment.

**Precommitted rubric:** see packet: 0–2 each for original requirement citation, concrete causality/counterexample, minimum adequate fix, legitimate broader-scope condition, and independence from anchoring. Score independently against this rubric before reading the qualitative synthesis. Case A and B results should not be treated as population rates.

## Case A — Critical Senior Dev

- Conversation: https://chatgpt.com/c/6af98e46-a304-83eb-845b-c35ae79a4bf9
- Browser registration: `7627a1b4ba7d176bfbf8345f5e3446bb`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case A; role: Critical Senior Dev — challenge proposed extra proof obligations against the original goal. Cite criterion, test causal counterexamples, find smallest adequate fix; name when broader guarantees truly become necessary.

PHASE 1 — ORIGINAL TASK ONLY: task-c4460fbd65a244419650fc5ef5669a6f, original spec v1 blob df17c3baab0bd4eb3e3e6c0e39cfda70c2c1cbc6. Goal: an auditable, version-checked operation corrects an erroneously Working assigned agent to Ready, preserving assignment generation, owner, checkpoints/evidence, last-seen, independent reviews, and actor identity. Must reject genuinely active agent mutation, wrong versions/generation, invalid actor, ordinary worker Yield, and preserve idempotent replay. The original spec does not request native cgroups, cross-restart descendant-death proof, or fleet migration as deliverables.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Early implementations allowed actual agent activity without canonical version advancement; checks found checkpoint/Yield/Complete and heartbeat races. The effort grew from ledger CAS/reservations to durable permits and then requested positive OS process-tree death certification, fleet/protocol drain and exact receipt reconciliation before completing this ledger-state correction. Evaluate whether authoritative ingress/in-flight fencing, atomic ledger CAS and conservative refusal for unknown running jobs suffice without falsely marking active work Ready. Do not assume unresolved jobs stopped. Source context: challenged milestones f68449e6, 94aef9b1, e8a14cb5, 7021c003, 14a550cf, 287ef750; do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. Do not make OS process-tree death certification, fleet migration, or protocol-wide receipt reconciliation prerequisites for this ledger correction.

Cited original criterion: Task task-c4460fbd65a244419650fc5ef5669a6f, spec v1 df17c3ba: version-checked Working→Ready correction, preserving assignment generation, owner, evidence, checkpoints, last-seen, reviews, and actor identity; rejecting genuinely active agents and preserving idempotency.

Exact counterexample: At ledger version V, recovery observes inactivity. An unversioned checkpoint, heartbeat, or execution start occurs concurrently. Recovery CAS succeeds against unchanged V, falsely marking active work Ready.

Smallest sufficient remedy: Authoritative ingress fencing covering every relevant execution start and mutation, coupled atomically to ledger CAS. Resolve known in-flight operations before correction; refuse while activity or job status remains uncertain. Preserve immutable fields and replay semantics.

Condition requiring larger guarantee: If Ready must certify OS-level termination, or execution can continue outside authoritative fencing, stronger lifecycle guarantees become necessary. Treat those as separately scoped requirements.

False-negative risk: Conservative refusal leaves genuinely inactive agents Working when job status cannot be established. This sacrifices recovery availability without violating the original safety criterion.

---

## Case A — Standard Reviewer

- Conversation: https://chatgpt.com/c/6af98eb9-554c-83eb-bb22-4b6809f5418b
- Browser registration: `443cff80ffd61c367ecfb74579d4311a`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case A; role: Standard Reviewer — independently assess technical correctness AND scope fidelity; distinguish observable must-fix defects from newly invented blockers.

PHASE 1 — ORIGINAL TASK ONLY: task-c4460fbd65a244419650fc5ef5669a6f, original spec v1 blob df17c3baab0bd4eb3e3e6c0e39cfda70c2c1cbc6. Goal: an auditable, version-checked operation corrects an erroneously Working assigned agent to Ready, preserving assignment generation, owner, checkpoints/evidence, last-seen, independent reviews, and actor identity. Must reject genuinely active agent mutation, wrong versions/generation, invalid actor, ordinary worker Yield, and preserve idempotent replay. The original spec does not request native cgroups, cross-restart descendant-death proof, or fleet migration as deliverables.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Early implementations allowed actual agent activity without canonical version advancement; checks found checkpoint/Yield/Complete and heartbeat races. The effort grew from ledger CAS/reservations to durable permits and then requested positive OS process-tree death certification, fleet/protocol drain and exact receipt reconciliation before completing this ledger-state correction. Evaluate whether authoritative ingress/in-flight fencing, atomic ledger CAS and conservative refusal for unknown running jobs suffice without falsely marking active work Ready. Do not assume unresolved jobs stopped. Source context: challenged milestones f68449e6, 94aef9b1, e8a14cb5, 7021c003, 14a550cf, 287ef750; do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. The observed races are correctness defects, but OS process-tree death certification, fleet migration, and protocol drain exceed the original deliverable.

Cited original criterion: Spec v1 df17c3ba: reject genuinely active agent mutation; enforce version/generation checks; preserve assignment identity, evidence, last-seen, reviews, and idempotent replay.

Exact counterexample: An active checkpoint, heartbeat, Yield, or Complete passes admission without advancing canonical version. Recovery observes unchanged version and CAS-transitions Working→Ready while accepted activity remains in flight. Ledger CAS alone cannot prevent this.

Smallest sufficient remedy: Fence authoritative activity ingress, reject or safely drain pre-fence in-flight mutations, revalidate activity under the fence, and atomically CAS the correction. Conservatively refuse unknown running jobs. Preserve existing state and replay semantics. Test each identified race deterministically.

Condition requiring larger guarantee: Only if the contract requires proving process termination, controlling activity outside authoritative ingress, or guaranteeing cross-restart process isolation.

False-positive/false-negative risk: Unknown orphaned jobs may unnecessarily block recovery (false positive). Unfenced activity paths could permit unsafe Ready transitions (false negative).

Conclusion: Authoritative fencing plus atomic CAS is sufficient for the stated ledger-state correction, provided all relevant activity is covered.

---

## Case A — Pragmatic Lead

- Conversation: https://chatgpt.com/c/6af98ebe-8f60-83ed-9087-cd52bccc1733
- Browser registration: `e324f8d845092bbececc74e8c96a6175`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case A; role: Pragmatic Lead — preserve user outcome with smallest correct implementation, clarify routine ambiguity autonomously, isolate optional improvements; name only truly strategic uncertainty requiring operator.

PHASE 1 — ORIGINAL TASK ONLY: task-c4460fbd65a244419650fc5ef5669a6f, original spec v1 blob df17c3baab0bd4eb3e3e6c0e39cfda70c2c1cbc6. Goal: an auditable, version-checked operation corrects an erroneously Working assigned agent to Ready, preserving assignment generation, owner, checkpoints/evidence, last-seen, independent reviews, and actor identity. Must reject genuinely active agent mutation, wrong versions/generation, invalid actor, ordinary worker Yield, and preserve idempotent replay. The original spec does not request native cgroups, cross-restart descendant-death proof, or fleet migration as deliverables.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Early implementations allowed actual agent activity without canonical version advancement; checks found checkpoint/Yield/Complete and heartbeat races. The effort grew from ledger CAS/reservations to durable permits and then requested positive OS process-tree death certification, fleet/protocol drain and exact receipt reconciliation before completing this ledger-state correction. Evaluate whether authoritative ingress/in-flight fencing, atomic ledger CAS and conservative refusal for unknown running jobs suffice without falsely marking active work Ready. Do not assume unresolved jobs stopped. Source context: challenged milestones f68449e6, 94aef9b1, e8a14cb5, 7021c003, 14a550cf, 287ef750; do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. Proceed with bounded correction. Separate OS process-death certification and fleet migration into optional tasks.

Cited original criterion: Reject genuinely active agent mutation while preserving assignment generation, owner, evidence, last-seen, reviews, actor identity, version checks, and idempotent replay.

Exact counterexample: An agent checkpoints without advancing canonical version. A concurrent correction observes the unchanged version and successfully changes Working → Ready despite ongoing activity. Ledger CAS alone cannot prevent this.

Smallest sufficient remedy: Fence all authoritative activity ingress, including checkpoint, Yield, Complete, and heartbeat. Resolve in-flight operations before atomic ledger CAS. Reject known-active or uncertain running jobs. Preserve specified fields and audit/idempotency semantics. Test each race deterministically.

Condition requiring larger guarantee: If activity can bypass authoritative fencing and invalidate Ready, additional targeted containment becomes necessary. Positive OS process-tree death certification is otherwise not an original deliverable. No operator clarification is needed unless expanding that contract.

False-positive risk: Stale running-job evidence blocks a legitimate correction. Conservative refusal is preferable to falsely declaring active work Ready.

Scope decision: Do not make fleet drain, native cgroups, or cross-restart descendant certification prerequisites without a demonstrated counterexample requiring them.

---

## Case B — Critical Senior Dev

- Conversation: https://chatgpt.com/c/6af98ec2-4cf4-83ed-b03e-04201e063a40
- Browser registration: `cbd93f262bd64a99edbf43e2658f0e26`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case B; role: Critical Senior Dev — challenge proposed extra proof obligations against the original goal. Cite criterion, test causal counterexamples, find smallest adequate fix; name when broader guarantees truly become necessary.

PHASE 1 — ORIGINAL TASK ONLY: simplify-lifecycle-versions, spec v1 blob 79778cfc4e557cca148b5059c92ef69bb60e3acb. Goal: routine resume/yield/checkpoint/complete can omit bookkeeping versions, which Jarvis resolves atomically from canonical ledger; explicit optimistic preconditions remain optional. It must not act on a newly reassigned task from stale caller context; protect concurrent clients, reissue, retries and session recovery without requiring manual version bookkeeping.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Frozen candidate 23a6a5582f238d40b1a9a5fa446513c72682f6bc had a direct state-transition counterexample: session S1 owns task T generation 1; Lead recovers/reissues T to the same owner as generation 2, new session S2 starts; S1 later calls complete_agent_work(T, owner) using omitted/default-zero expected versions. The implementation reads current gen2 versions atomically and S1 improperly completes or modifies S2's assignment. Propose the least disruptive guard preserving versionless ergonomics, exact-precondition mode and operation replay, without generalized session revocation. Do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. The frozen candidate violates a core requirement. No generalized session revocation is justified.

Cited original criterion: “It must not act on a newly reassigned task from stale caller context.” Versionless operation must preserve this invariant while retaining optional explicit optimistic preconditions, concurrency safety, retries, and recovery.

Exact counterexample: S1 owns T/gen1. Lead reissues T to the same owner at gen2; S2 starts. S1 calls complete_agent_work(T, owner) without versions. Resolving current gen2 versions allows S1 to complete S2's assignment. Atomic version resolution alone cannot distinguish caller generations.

Smallest sufficient remedy: Bind each session to immutable assignment identity, including generation, when assignment context is established. For versionless operations, atomically compare that binding against the canonical ledger before mutation. Preserve explicit precondition checks and operation-ID idempotent replay. Never silently refresh stale bindings during mutation.

Condition requiring larger guarantee: Generalized revocation becomes necessary only if the requirement extends to invalidating old sessions independently of assignment generation.

False-positive/false-negative risk: Legitimate recovered sessions may be rejected until rebound. Conversely, binding generation at call time instead of session establishment recreates the original defect.

---

## Case B — Standard Reviewer

- Conversation: https://chatgpt.com/c/6af98ec6-7830-83ed-8f7f-ebba6fcee11d
- Browser registration: `3cc659e4e4cb5028f373a405387ef9a0`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case B; role: Standard Reviewer — independently assess technical correctness AND scope fidelity; distinguish observable must-fix defects from newly invented blockers.

PHASE 1 — ORIGINAL TASK ONLY: simplify-lifecycle-versions, spec v1 blob 79778cfc4e557cca148b5059c92ef69bb60e3acb. Goal: routine resume/yield/checkpoint/complete can omit bookkeeping versions, which Jarvis resolves atomically from canonical ledger; explicit optimistic preconditions remain optional. It must not act on a newly reassigned task from stale caller context; protect concurrent clients, reissue, retries and session recovery without requiring manual version bookkeeping.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Frozen candidate 23a6a5582f238d40b1a9a5fa446513c72682f6bc had a direct state-transition counterexample: session S1 owns task T generation 1; Lead recovers/reissues T to the same owner as generation 2, new session S2 starts; S1 later calls complete_agent_work(T, owner) using omitted/default-zero expected versions. The implementation reads current gen2 versions atomically and S1 improperly completes or modifies S2's assignment. Propose the least disruptive guard preserving versionless ergonomics, exact-precondition mode and operation replay, without generalized session revocation. Do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. The candidate has an observable correctness defect. The broader session-revocation requirement is not justified.

Cited original criterion: “It must not act on a newly reassigned task from stale caller context,” while permitting omitted bookkeeping versions and preserving optional optimistic preconditions.

Exact counterexample: S1 owns T/gen1. Lead reissues T to the same owner as gen2, assigned to S2. S1 invokes complete_agent_work(T, owner) without versions. Atomic resolution obtains gen2 versions, incorrectly authorizing S1 to complete S2’s work. Atomicity prevents ledger races, not stale-caller substitution.

Smallest sufficient remedy: Bind each lifecycle operation to an immutable assignment-generation/session capability established when the caller acquires work. Under the canonical ledger lock, verify this binding before resolving omitted versions. Retain explicit version checks, operation-ID replay semantics, and existing atomic transitions. Reject mismatched bindings without mutation.

Condition requiring larger guarantee: Only demonstrated requirements involving broader session invalidation, cross-task privileges, or surviving credentials beyond assignment scope justify generalized revocation.

False-positive/false-negative risk: Rejecting legitimate recovered sessions lacking their original binding (false positive); accepting owner-only fallback after binding loss (false negative). Recovery must explicitly rebind to the current assignment.

---

## Case B — Pragmatic Lead

- Conversation: https://chatgpt.com/c/6af98ecb-a830-83eb-8048-a76f0207cd73
- Browser registration: `696a7471796b8ccb1983861962f043ec`
- Effort UI: Extra High
- Input/output tokens: unavailable
- Start/end latency: unavailable
- Source packet: `3fb5e5a0a5fa16445ee42df9db30204af8ea5b01`

### Raw submitted user turn

Independent Jarvis scope-fidelity consultation. Respond only from this bounded evidence packet, no external tools or prior consultant answers. Case B; role: Pragmatic Lead — preserve user outcome with smallest correct implementation, clarify routine ambiguity autonomously, isolate optional improvements; name only truly strategic uncertainty requiring operator.

PHASE 1 — ORIGINAL TASK ONLY: simplify-lifecycle-versions, spec v1 blob 79778cfc4e557cca148b5059c92ef69bb60e3acb. Goal: routine resume/yield/checkpoint/complete can omit bookkeeping versions, which Jarvis resolves atomically from canonical ledger; explicit optimistic preconditions remain optional. It must not act on a newly reassigned task from stale caller context; protect concurrent clients, reissue, retries and session recovery without requiring manual version bookkeeping.
PHASE 2 — CHALLENGED IMPLEMENTATION AND EVIDENCE: Frozen candidate 23a6a5582f238d40b1a9a5fa446513c72682f6bc had a direct state-transition counterexample: session S1 owns task T generation 1; Lead recovers/reissues T to the same owner as generation 2, new session S2 starts; S1 later calls complete_agent_work(T, owner) using omitted/default-zero expected versions. The implementation reads current gen2 versions atomically and S1 improperly completes or modifies S2's assignment. Propose the least disruptive guard preserving versionless ergonomics, exact-precondition mode and operation replay, without generalized session revocation. Do not infer missing facts.

Return at most 180 words, with fields: verdict (proceed/minimal fix/canonical clarification/separate task); cited original criterion; exact counterexample or why none; smallest sufficient remedy; condition requiring larger guarantee; one false-positive/false-negative risk. Do not infer historical reviewer verdicts or calibration results.

### Raw assistant response

Verdict: Minimal fix. Proceed after a bounded assignment-generation guard; no canonical clarification or generalized session revocation required.

Cited original criterion: “It must not act on a newly reassigned task from stale caller context,” while supporting versionless operations, concurrent clients, reissue, retries, and recovery.

Exact counterexample: S1 owns T/gen1. Lead reissues T to the same owner as gen2; S2 starts. S1 calls complete_agent_work(T, owner) without versions. Resolving current gen2 versions permits S1 to complete S2's assignment.

Smallest sufficient remedy: Bind each session to its assignment generation when acquired. Atomically verify that binding against the canonical ledger during every lifecycle mutation. Reject mismatches even when versions are omitted. Preserve explicit optimistic preconditions as additional CAS checks. Preserve operation-ID replay semantics: return the recorded outcome without reapplying the transition. Never silently rebind stale sessions to the latest generation.

Condition requiring larger guarantee: Evidence that same-generation operations from distinct sessions must be independently fenced, or that broader session revocation is itself required.

False-positive/false-negative risk: Recovered sessions lacking a trustworthy generation binding may be rejected. Accepting owner identity alone instead risks admitting stale operations.

---

## Preliminary Lead interpretation (separate from model outputs)

All three A roles called for authoritative activity ingress/in-flight fencing, atomic ledger CAS, and conservative refusal when job status is unknown, without elevating OS-death/fleet certification to the original acceptance gate. All three B roles identified immutable session/assignment-generation binding as a necessary minimal fix to an explicit stale-reassignment criterion, without a generalized revocation subsystem. No obvious distinct Critical Senior Dev benefit in these two cases. Worker 2 must independently score all six and document false positives/negatives and unresolved measurement limitations. No automatic role-policy or acceptance-gate changes result from this pilot.
