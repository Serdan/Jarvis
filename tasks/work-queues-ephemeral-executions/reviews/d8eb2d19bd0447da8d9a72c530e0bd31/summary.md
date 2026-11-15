# Reviewer 1 — Independent frozen architecture review

**Verdict: ChangesRequested — one P1 architectural blocker.**

- **Request:** `d8eb2d19bd0447da8d9a72c530e0bd31`
- **Task:** `work-queues-ephemeral-executions`
- **Frozen source:** `c78ab56ed0dbf6e499f8a3f2c7263cfbfc06dfc4` on immutable review ref `refs/heads/jarvis-review/work-queues-ephemeral-executions/d8eb2d19bd0447da8d9a72c530e0bd31/r1`
- **Target/main baseline:** `1481bfa03aea4def721d2b24835bff9b9076160a`
- **Scope:** proposed docs-only architecture; no implementation, merge, rollout, scheduler activation, restart, migration or deployment authorized.

## Independent evidence

Verified review worktree HEAD equals frozen SHA, clean working tree, and `git diff --check 1481bfa..c78ab56` exits 0. Read the complete 553-line `docs/specs/work-queues-and-ephemeral-executions.md`, including §§1–18 and acceptance cases 1–65, the predecessor review `f8c5216d9488464c9cb2e175fed3b16c` and its F01–F04 findings, and the corrective delta from `9f1cd0f...` to `c78ab56...`. All acceptance cases are specifications, **not executed test results**.

## Blocking finding

**F05 / P1 — A decided frozen review remains Claimed after its canonical verdict, but §7's new unsatisfied-hard-dependency gate treats all Claimed stages as recoverable to Blocked/Available.** Inserting a hard edge after `DecideReview` but before `CompleteWork` would either reopen an immutable decided review, conflict with §10's prohibition on post-verdict Block/Yield, or strand its claim/launch/token. A later `DecideReview` against a gate must also be explicitly fenced, and the same terminal-pending distinction matters for integration receipts. Detailed interleaving, remedy and test matrix: `findings/f05-post-verdict-dependency-gate.md`.

## Previous F01–F04 correction assessment

1. **F01 addressed for ordinary Role-message handling.** Dedicated per-message W is a primary claim; `CompleteMessageWork` atomically records reply/Handled + W Completed + launch terminal, with account release only after project proof. Separate ancillary Role/Task leases cannot terminate a different primary activation. NoWork/claim, batching/credit and lost-reply cases 50–54 are now specified.
2. **F02 addressed.** Task messages without an active eligible Task actor are passive `AwaitingTaskActor`, including blocked/closed/cancelled tasks; they neither admit a launch nor reopen the Task. Role(Lead) provides an explicit separate triage route; cases 55–56 cover it.
3. **F03 addressed at the stated cooperative protocol boundary.** One high-entropy session key per actual provider execution, retained only on verified same-execution retry; different keys lose competing InitializeAgent CAS. The spec correctly disclaims the ability to detect deliberately copied equal keys without provider attestation. Cases 57–59 cover both failure modes.
4. **F04 partly addressed but incomplete across domain terminal states.** Active source-writer A receives `DependencyGatePending` on Lead's hard-edge CAS; old CompleteWork/Yield cannot bypass it; verified WIP and safe `ResolveDependencyGate` decide Blocked/Available; tests 60–63 cover source mutations. The missing **post-verdict, pre-CompleteWork review** state creates F05 and prevents acceptance.

## Other invariants inspected

- No model Manager: deterministic non-agent Scheduler/Reconciler; two-tier blocker-first FIFO, compatible injection/rematching and stage-generation SCC diagnostics. Blocked and incomplete queues never authorize dispatch.
- Account-global capacity CAS, project-demand admission/abort-only C01 tombstone, no-claim/registered/unregistered recovery, one-shot browser journal and UUID-on-first-connect; token release requires exact project terminal evidence.
- Exact frozen review/source lineage, one source writer, remotely verified WIP snapshot before safe yield, exact accepted SHA integration, explicit task closure, task/role conversation ownership and old Manager migration fences.
- Multi-project epoch cutover requires verified writer gates, old Manager drain, full project/account proof and explicit operator authorization. No permission/agent-identity claims are invented.

## Disposition

**ChangesRequested.** Correct F05 in a new committed spec SHA: define semantic terminal-pending guards and gate checks for decisive domain writes, include both CAS orderings and lost-response/integration variants in deterministic negative-space tests, then submit a new independent frozen review. Do not reinterpret this verdict as authorization for implementation or release.

**No reviewed source modified; review documents and verdict only.**