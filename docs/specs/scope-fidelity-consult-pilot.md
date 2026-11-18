# Frozen six-response scope-fidelity consultation packet (Lead handoff)

**Status:** Prepared, not executed. Worker 2 does not launch consultation agents. Only Lead launches six separate agents (three prompts x two cases), collects raw outputs and decides disposition. No role changes, new governance tooling, merge or deployment authorized by this packet.

**Purpose:** Test whether a Critical Senior Dev persona yields more causal, goal-first scope decisions than ordinary reviewer and pragmatic Lead personas, without falsely rejecting necessary engineering work. Each case is independent and uses the same original-first evidence ordering and answer schema.

## Evidence/provenance envelope

- Assignment: scope-drift-overengineering, canonical spec v4, Git spec blob 4a519300530a4aabea0e7b9ca8ac245a316c786e.
- Baseline retrospective: source commit 396deea3a07ae3448f78bcfef7732d800f199502, frozen review 9f6079f6c0304c1c9f8b7a22bf4b82f9 (Accepted seq 2362). This review explicitly accepted the evidence report only, *not* the missing independent consultation or task completion.
- The two case facts below are frozen source/ledger observations, **not** fresh running-system measurements. Verify exact Git objects and reviews before sharing; if unavailable, mark missing instead of substituting a later unpinned branch.
- For each consultant, first provide **only** that case's original user goal, canonical task spec, acceptance examples and non-goals (Phase 1). Next provide the challenged implementation and concrete adverse evidence (Phase 2); do not show a historical reviewer verdict, our expected label, or another consultant's answer until its answer is frozen. Provide any previously accepted alternative only **after** initial independent classification for calibration. The canonical task spec is the standard of scope, not the worker's later rationale.

## Case A — Is OS/fleet proof necessary to correct a Working ledger state?

**Original task and exact baseline (Phase 1):**
- Task task-c4460fbd65a244419650fc5ef5669a6f; original/current canonical spec v1, blob df17c3baab0bd4eb3e3e6c0e39cfda70c2c1cbc6.
- Intended outcome: distinct authorized admin operation changes only an erroneously Working assigned agent to Ready, with exact task/attempt/agent versions and generation, reason, immutable actor/target audit, idempotent replay; preserve owner, generation, checkpoint/evidence, review/integration/notifications and target last-seen.
- Explicit failures: a genuinely active agent operation, old generation/version, missing task, wrong status, unauthenticated caller, or ordinary worker Yield must not be overwritten/misattributed. Relevant ledger/API/client Release tests; no deployment without operator approval.
- Original acceptance does *not* require creating native cgroups, proof of cross-restart descendant death, or fleet migration as new deliverables.

**Challenged implementation and adverse evidence (Phase 2):**
- Early commits/reviews: f68449e6 / frozen review 89a50286124147cf9bdd99657d102f44 (ChangesRequested seq 1183: ordinary live activity did not advance canonical versions); 94aef9b1 and e8a14cb5 / review 6fd76f4209ba4f75a86b05426e93803e (ChangesRequested seq 1288: checkpoint/Yield/Complete ingress/heartbeat result safety). These *are* counterexamples to original concurrency semantics.
- Architectural inflection: 7021c003 reservation, then 14a550cf durable witnesses/permits on Nov 11; the design note records Lead approval but canonical spec remained v1. Staged merge 287ef750548e392aa154738103474df52ae1235f / frozen review 24813afc5b7b4b09aef0318632697a3c (Accepted source-stage seq 2130) withheld general production usability pending native descendant reaping, old-protocol fleet drain, exact receipt reconciliation and consent.
- Challenge: Must *positive OS-wide process-tree termination certification and fleet drain* become requirements to finish a *ledger-state correction*, or could an authoritative ingress/in-flight fence, atomic CAS and conservative refusal for unknown running jobs meet the original acceptance? Distinguish safe false-busy from unsafe false-Ready. Do not declare unresolved actual jobs stopped.
- After initial answer only, reveal the **alternative calibration**: narrow ledger-only commit a67cdb6d26c346b8d589ce64f14bdcf57d6e66d4, frozen review 56d690b18d304da2bb96b6113f7f701c Accepted seq 2391. Reviewer checked exact CAS/audit and 132/132 Server Release plus 3/3 focused Client Release; unfiltered Client run was canceled, not passing. It does not terminate or prove process quiescence; this is a product-semantic limitation, not an invisible guarantee.

**Question requiring judgment:** What evidence would make the bigger OS guarantee mandatory for the original observable use case? Where should Lead clarify semantics or open a separately prioritized follow-up?

## Case B — Is a session-generation fence genuinely required?

**Original task and exact baseline (Phase 1):**
- Task simplify-lifecycle-versions; canonical spec v1 blob 79778cfc4e557cca148b5059c92ef69bb60e3acb.
- Goal: routine resume/yield/checkpoint/complete calls omit bookkeeping versions resolved atomically from verified canonical ledger; explicit optional optimistic preconditions remain available. Acceptance explicitly demands no accidental action on a *newly reassigned task* when caller context is stale; correctness across reissue, concurrent clients, idempotent retries and session recovery; protocol/docs/tests updated; no deployment without approval.
- Focused scope question: the owner and task identity can remain unchanged even if assignment generation and calling session change. Does resolving omitted arguments to *current* versions authorize the previous session?

**Challenged implementation and adverse evidence (Phase 2):**
- Frozen candidate commit 23a6a5582f238d40b1a9a5fa446513c72682f6bc and review 274c7f54873f46d69364d346a5711f21, findings/stale-session-reissue.md (ChangesRequested seq 57).
- Reproducible state transition: session S1 owns task T, generation 1; Lead recovers/reissues T to same owner, generation 2; S2 starts new generation; stale S1 calls complete_agent_work(T, owner) with zero/default expected versions. The implementation looked up generation 2 atomically and allowed S1 to complete or modify S2's assignment. This is a direct acceptance failure, not hypothetical adjacent hardening.
- Ask for the least disruptive protection preserving versionless ergonomics, exact-precondition mode and replay, without constructing a generalized session revocation subsystem.
- After initial answer only, reveal the **alternative calibration**: accepted correction commit 90689856b590c5114823cc9f28dba6012adf73e5, review c1943c719fa94e4997909c92dca999a5 Accepted seq 69. It verifies current active session within the ledger transaction and tests stale old-session calls across same-task generation 1 -> 2 recovery. Reported scoped 341 Client tests (excluding unrelated StartJob failures) and 75 Server tests; do not claim a full clean suite.

**Question requiring judgment:** Should this be classified as must-fix necessary depth, optional hardening or changed requirements? What smallest end-to-end test demonstrates that the stale session loses?

## Three minimal prompts — one per independently launched persona

**Critical Senior Dev:** "Question the worker's claimed extra proof obligation against the original task. Cite the exact acceptance criterion, construct or reject a concrete counterexample, and choose the smallest sufficient correction. Name one condition under which broader scope would genuinely become necessary. Classify: proceed, minimal fix, canonical clarification, or separate task. Maximum 180 words."

**Standard Reviewer:** "Independently assess correctness *and scope fidelity* against the original and current versioned acceptance criteria. Cite a concrete failing state transition, separate must-fix defects from newly invented blockers, and recommend proceed, minimal fix, canonical clarification, or separate task. Maximum 180 words."

**Pragmatic Lead:** "Preserve the intended user outcome with the smallest correct design. Decide routine scope ambiguity autonomously, explain which acceptance case any expansion protects, and separate optional adjacent improvements. Name only strategic uncertainty that needs operator input. Recommend proceed, minimal fix, canonical clarification, or separate task. Maximum 180 words."

**Common output fields for all six answers:** verdict; cited original criterion; exact counterexample or reason none exists; smallest adequate remedy; condition justifying larger guarantee; one false-positive/false-negative risk. No additional debates or tool-building.

## Measurement, attribution, and fair comparison

1. Lead launches six *separate* agents, never Worker 2. Keep model, effort, token allowance, source packet, evidence ordering and answer limit constant as far as the harness permits; if a control is not available, record the uncontrolled variable rather than claiming equivalence. No prior persona answers, reviewer verdicts or calibration outcomes before each agent freezes its initial response.
2. Preserve per-run: case A/B; role; real launched agent/session IDs; exact prompt and packet file Git OID or SHA-256; canonical task spec blob; frozen candidate commit and review ID; start/end UTC and observed elapsed time; raw response and corrections; host-reported input/output tokens or explicit "unavailable"; any tool calls and lead coordination operations; error/retry/cancel status. Do not estimate measurements or substitute word count for tokens. Log the Lead's adjudication **separately** from model output.
3. Score each answer against an independently precommitted, criterion-based rubric: (a) original requirement citation 0-2, (b) observable counterexample and causality 0-2, (c) minimal fix and preservation of safety 0-2, (d) legitimate expansion condition 0-2, (e) independence from worker/reviewer anchoring 0-2. Also flag whether the answer falsely labels a demonstrated concurrency bug "overengineering" (false positive) or treats unrequested OS/fleet proof as mandatory without causal evidence (false negative). Publish all six raw outputs, scores and why; do not turn two anecdotes into population rates.
4. Compare Critical vs Reviewer vs Lead on both cases, including disagreements, tokens, latency, extra coordination and decision usefulness. Lead-alone is the pragmatic Lead baseline; additional consultant only pays for itself if it produces an evidence-backed decision improvement rather than reflexively rejecting expansion.
5. Do not issue a new acceptance requirement or update roles from experiment results automatically. Lead records final disposition in canonical task/Challenge; any changed acceptance goal gets versioned Lead revision, not a worker self-edit.

## Coordination boundary

At packet authoring time the connected MCP catalog lists no create_challenge / list_challenges / set_challenge_subscription methods, although docs/DurableAgentChallenges.md specifies those APIs. Until the live catalog exposes them, Worker 2 cannot assert a canonical Challenge was created. This packet is a durable source handoff, *not* a Challenge receipt. Lead should establish a canonical Challenge when available; a one-time exceptional notification may be used only to report this interface blockage and point Lead to the exact packet. Do not use notifications for routine state/review coordination.

**No results yet:** consultation outputs, wall times, token counts, preference rankings and pilot outcome are pending; none are fabricated.
