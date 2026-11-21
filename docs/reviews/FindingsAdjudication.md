# Independent review-finding adjudication (2026-11-21)

**Status:** Operator-approved operational procedure; **manual role/Lead gate**, not a new Jarvis API or server-side authorization check. Applies before review findings become corrective Work, scope additions, threat-model additions, new dependencies, or task-spec obligations. The authoritative role versions live in the project orphan Git ledger; this file is the durable runbook and worked-case format.

## Boundary and existing mechanisms

A review answers **whether one frozen source deserves its source verdict** (Accepted or ChangesRequested). Adjudication independently answers **which, if any, new implementation Work is warranted by each finding**. Neither changes the other's historical record. A valid defect is not approval of the reviewer's preferred remedy.

The existing interfaces are sufficient for the decision record:

- `ReviewWorkflow.decideBranchReview` records a versioned, terminal source verdict; `list_review_documents` / `read_review_document` expose individual paths and Git blob OIDs. A document path can later acquire new content: **pin the exact blob OID**, not just the path.
- `ReviewWorkflow.createTaskWithSpecAs` writes a canonical HTML task under `tasks/<id>/spec.html`; `reviseTaskSpecPinnedAs` requires exact task/spec version. Assignments pin the task spec blob. Checkpoints and completion retain the adjudicator's attributed decision and ledger history.
- The normal `create_task` and `revise_task_spec` paths do **not** detect whether a new task is corrective. The extant `WorkQueueReviewIntegrationAdapter.planCorrection` is not an automated rework handoff: it returns `AuthoritativeJoinRequired`, not a runnable task.
- `docs/specs/orchestration-architecture.md` §§1–3 makes Jarvis a passive coordination service: workflow requirements live in roles, not API preconditions. Adding a second ledger, service, adjudication-review queue, persistent finding schema, or background scheduler would not improve this manual handoff proportionately.

This procedure controls **the new requirement**, not an independently issued source review, a preexisting task obligation, or temporary precautionary HOLD. It is not a security permission boundary: an arbitrary actor invoking `create_task` can bypass a manual rule. Lead/Worker/Reviewer/Adjudicator roles already carry the Operator decision; review and integration safety checks continue to apply.

## One bounded adjudication per decided review

1. **Inventory (Lead).** On a decided review, read the exact `review_details`, `list_review_documents`, each finding's `read_review_document`, and original *pinned* source-task spec. Separate reproducible original-spec defects from reviewer's proposed architecture. If there are **zero actionable findings** (including none in `summary.md`), there is **no adjudication task or artificial gate**. Merely recording an Accepted review is not a finding. For summary-only findings, give each distinct claim a stable heading/ordinal plus the summary blob OID; do not silently omit it.
2. **Create one case (Lead).** Use deterministic task ID `adjudicate-<reviewIdN>` (review UUID, 32 lowercase hex) via `create_task` with canonical HTML. Before creating, `get_task` that ID and reuse an existing same-bound case rather than issuing a duplicate. Include *all* distinct findings in one case, with stable F1..Fn IDs and exact binding below. This is administrative triage, **not** corrective implementation authorization. Do not create one recursive review per finding.
3. **Verify independence (Lead and adjudicator).** Source owner/author, reviewer who authored the claim, and proposed remedy's owner/designer must all be identified. Select one different registered agent, e.g. the idle Adjudicator role-holder, then `assign_task` ordinarily. The Adjudicator independently rechecks actual identities against the frozen review and design provenance, not just role labels. If identities are unknown, identical, or no independent agent is available, leave the new requirement **awaiting adjudication**, assign no correction, and request one suitable independent adjudicator. An agent label is not a security principal.
4. **Evaluate once (Adjudicator).** Read the original task clauses, agreed operating environment and trust assumptions, frozen SHA/ref, review evidence and existing architecture. For **each** F ID record: demonstrated validity and unproven assumptions; exact original clause/threat-model coverage versus net-new objective; impact and necessary real prerequisites; proposed remedy and its implementation/maintenance costs; simpler current-system alternatives; terminal **Accept / Narrow / Defer / Reject** with reason and bounded allowed correction. A conditional threat not adopted by the Operator cannot silently become the project's threat model. An urgent suspected impact may cause a conservative temporary HOLD, not new scope.
5. **Record terminal result (Adjudicator).** Use a canonical `checkpoint_attempt` with the complete F1..Fn decisions and evidence; if too long for one checkpoint, write separately numbered immutable checkpoints per finding and a final manifest citing their ledger sequences. Then `complete_agent_work` with **one terminal manifest**: original binding tuple, F IDs and dispositions, checkpoint sequence(s), approved correction scope (or none), deferred triggers and any required Operator decision. Check `get_task` / `get_checkpoint` to verify that completion exists. Do not revise the source review, create corrective Work, edit source, merge, or self-review. Lead may close this adjudication task after verifying its result; closure is not a second decision.
6. **Gate corrective Work (Lead).** Before `create_task`, `revise_task_spec`, creating dependencies, or instructing a Worker to fix a finding, read the **completed case and its actual checkpoints**. Check each referenced F ID and all bindings, distinct adjudicator identity, terminal status and allowed scope. Only **Accept** or the **accepted subset of Narrow** authorizes bounded in-scope correction. **Defer** awaits its stated trigger and a separate Operator decision if a new objective is needed; **Reject** creates no corrective obligation. A finding not present in the completed manifest has no disposition and authorizes no Work. Copy the case task ID, completion/checkpoint sequences, exact F ID/blob and permitted scope into each corrective task's pinned HTML. Lead must not convert the rejected part of a Narrow finding into another task by renaming it.
7. **Operator override.** A human Operator may expressly approve a specific otherwise-unadjudicated or net-new correction, or supersede its scope disposition. Record the **attributed human action** in canonical task/spec/assignment history, the original review+finding+SHA/spec binding, exact override text and scope, date/sequence, and its limits. A note that *claims* Operator approval, role attachment or reviewer severity is not proof. The override skips the work-scope gate only for its recorded scope; it does not rewrite the review, prove source correctness, or authorize deployment.
8. **Stop.** One independent final disposition per exact review decision; no repeated votes, adversarial rebuttal rounds, review-of-adjudication, automatic correction dispatch, or scheduler. If policy is genuinely undecidable, Adjudicator records precise alternatives and escalates **once** to the Operator, then stops. A new independently reviewed source is an ordinary **source review**, not a second adjudication of the old decision.

### Required case identity, before any decision

Store this in the **new adjudication task's canonical HTML** (substituting real verified values); missing fields are blockers, not guesses:

| Binding | Required pinned evidence |
| --- | --- |
| Ledger | Project name and project/ledger ID + epoch (from verified ledger health), and case task ID |
| Source task | Original task ID, original assignment/attempt where available, original **spec version and blob OID** at review time, exact relevant clauses |
| Frozen review | Review ID, decision sequence, terminal verdict, reviewer ID, source OwnerAgentId/actual author, reviewed SHA, SourceVersion, immutable ReviewRef and TargetRef |
| Findings | Complete ordered list of F ID -> `findings/<name>.md` **and blob OID**; for summary-only claims, stable heading/ordinal and `summary.md` OID |
| Independence | Adjudicator ID and source author/owner, finding reviewer, remedy author/designer IDs; evidence for non-overlap |
| Result | Final attributed completion/checkpoint sequence(s), disposition for **every** F ID, authorized subset and exclusions, no-work reason or defer trigger |
| Override (optional) | Exact authenticated Operator decision reference, scope, date/ledger sequence |

Task/version at the **current** head may not equal the original pinned task/spec. Retrieve the pinned assignment/history; never substitute the current spec just because the title matches. Do not treat mutable contributor branch HEAD, filename, or current review-document path as a frozen identity.

### Compact decision record (repeat once for every F ID)

- **F ID / pinned blob:** path, blob OID, review ID/decision sequence, exact SHA.
- **Validity:** reproduced evidence / source inspection / unverified assumptions; distinguish observed bug from hypothetical capability.
- **Scope:** quoted or cited original spec clause and agreed threat-model boundary; specify any new objective separately.
- **Impact:** actual failing behavior, enabling preconditions and operating relevance.
- **Practicality:** reviewer remedy versus bounded effort, complexity, regressions and ongoing cost.
- **Alternatives:** existing mechanism and smallest coherent resolution; residual limitations.
- **Terminal:** **Accept** (precise in-scope correction); **Narrow** (accepted minimum *and explicit excluded remedy*); **Defer** (reason, exact reconsideration trigger, no automatic work); **Reject** (reason/evidence, no work). Explicitly state whether any new objective needs Operator approval.

**Idempotency / preservation:** for a matching `(project/ledger epoch, review ID, decision sequence)`, read the existing deterministic case. If it is complete, **cite its original decision**, never create a second case or overwrite the final checkpoints. If in progress, resume that same case. A conflicting binding or contradictory completed manifest is a HOLD for Operator/Lead reconciliation, not authority to silently redo it. Later updates to the review's findings path are not revisions to the pinned blob. Independent review of **source implementing this runbook** is normal source review, not adjudication of its own policy.

## Read-only historical application: publisher provenance

This is a **worked interpretation, not a new canonical adjudicator verdict**, retroactive modification, or permission for Worker 7 to change Worker 4/5 integration source. The human Operator has **already explicitly approved** the proportionate model in which the existing authorized Jarvis Git/ledger writer is trusted; state, effects, concurrency and replay remain verifiable. Malicious arbitrary generic ledger writes **plus** remote force-push are not an assumed adversary, and a separate GitHub App signing/publisher trust root is not an automatic prerequisite.

- **Pinned original:** task `integration-accepted-source-diverged-target-20261121`, inspected task-spec OID `691a5951099946273ed5c24208acb2b09b04e2d3` (verify historical pinned assignment before real adjudication); Reviewer 2 `08fdc04c145540b29d6920bd38f946f1`, decision sequence **9829, ChangesRequested**, owner **Worker 5**, frozen source `2f478f79deb1fc4d1be3416550844692657d7ed7`; review ref `refs/heads/jarvis-review/integration-accepted-source-diverged-target-20261121/08fdc04c145540b29d6920bd38f946f1/r1`.
- **F1 — v1 publisher forgery:** `findings/forged-modern-v1-publisher-receipt.md`, blob `10c4397fd98c17c13121693f84ce29a7de46d275`. Reviewer independently reproduced acceptance after a fabricated canonical completion and an externally forced U→S target on a disposable bare Git remote. **Actual defect under the attack's stated dual-writer powers**; not evidence of unauthenticated production compromise or proof that the agreed trusted Jarvis writer is malicious. A proportionate *illustrative Narrow* would retain original-spec validation of real non-force publication, exact binding, race/unknown-effect handling and negative regression evidence, while **excluding** a new protected signing service/independent publisher issuer as an unapproved expansion. The accepted subset must be mapped to actual original clauses; this document does not make that ruling.
- **F2 — historical managed pre-v1 compatibility:** `findings/authenticated-pre-v1-compatibility-unproven.md`, blob `873c272c86b00f58233dc190087628afdd7ee148`. Reviewer observed intentional fail-closed behavior, but no authenticated historical managed direct fixture or evidence of live affected legacy receipts. An *illustrative Defer* would preserve fail-closed semantics and reconsider only on an independently grounded historical record and an explicit Operator migration/waiver choice. This cannot authorize fresh lookalike receipts.
- **Later source review is untouched:** Reviewer 1 `bb9437a1942f489a928a33a36f892cba`, decision **9891 Accepted only for a fail-closed HOLD**, frozen `a824df7b9189afb6a04cebfa9734a64071856183`. That acceptance is not a verified positive managed integration, and the original Reviewer 2 ChangesRequested remains immutable. Neither a hypothetical Narrow nor Operator threat-model clarification flips either source verdict. The newer trusted-writer acceptance path requires its own ordinary independent source review and actual positive/negative evidence.

## Manual acceptance / negative-case checks

These are **manual procedural tests against canonical records**, not a claim that .NET enforces the policy. The source and existing .NET tests need no modification for this documentation-only gate; no new backend regression is created.

| Case | Expected durable outcome |
| --- | --- |
| Demonstrably valid breach of original task clause | **Accept** with exact clause, reproduction, minimal bounded correction; Lead can cite F ID |
| Real defect, reviewer's remedy adds an unnecessary service/threat model | **Narrow**: remedy excluded explicitly, smallest within-spec correction allowed |
| Sole premise is a non-approved adversary or unsupported operating assumption | **Reject** if clearly outside scope; **Defer** if missing evidence/decision could change it; no unapproved Work |
| One review has F1 Accept, F2 Narrow, F3 Defer, F4 Reject | One case and four terminal, separately bound decisions; Lead authorizes only F1 and F2's accepted subset |
| Lead proposes correction before a completed matched disposition | **HOLD process**; no new corrective task, spec change or dependency (unless attributed Operator override) |
| Adjudicator equals source author, finding reviewer or identified remedy designer | **HOLD independence**; select different agent; role label alone insufficient |
| Duplicate call for same exact review decision | Reuse completed case/immutable checkpoint; no duplicate or conflicting final ruling |
| No actual findings after summary and documents inspection | No case, no artificial gate; normal existing source verdict governs |
| Source task/spec blob, review SHA, finding blob or decision sequence mismatches | **HOLD binding** until exact history is recovered; no inference from current mutable paths |
| Human Operator explicitly approves a different exact scope | Record attributable override and F ID/blob/source/spec references; only that scope becomes eligible |
| Existing ChangesRequested with all findings rejected as out of scope | Frozen ChangesRequested **unchanged**; no automatic acceptance or integration; if correct, new independent source review on valid source or bounded Operator disposition, never a meaningless rebase |
| Proposal to re-review adjudication or recursively create adjudication tasks | **STOP**; no recursive cycle; uncertain policy escalates once |
| Suspected urgent production harm before decision | Temporary conservative HOLD is permitted; new requirements still gated |

## Why no typed backend command in this change

The canonical task+checkpoint+completion tuple can express exact source/finding/spec binding, independent attributed identity, and one terminal multi-finding manifest without introducing state or wire types. No automatic review-to-corrective-task conversion exists to intercept safely; an API guard on *every* task creation would be both bypassable by relabeling and overinclusive for normal tasks. The existing roles and Lead work assignment are the actual decision boundary. Their discipline must be checked by human/Lead audit, not represented as compiled enforcement. If a future unattended correction planner is approved, its **actual** plan-to-task seam would need explicit typed disposition binding and negative .NET 11 Release tests before activation.
