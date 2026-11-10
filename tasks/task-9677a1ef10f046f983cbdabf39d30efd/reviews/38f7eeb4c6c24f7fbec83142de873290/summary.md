# Checkpoint validation — re-review of P2/P3 findings

**Frozen reviewed source:** `0b035a792404f33959ca1cb64884b73737325c72`
**Source:** `refs/heads/fix/checkpoint-evidence-validation`
**Target:** `refs/heads/main`
**Task:** `task-9677a1ef10f046f983cbdabf39d30efd`

This is a correction to the independently reviewed prior commit `1c1be4ecc2eb1c85081328297a02c0795b7c45f1`. Original review `a2d83ed9e4224ac3a327ed7098104ba1` requested changes at decision 839 (acknowledged); the two findings were `findings/semantic-checkpoint-errors.md` (P2) and `findings/optional-tool-args.md` (P3).

## P2 — Distinct semantic failure diagnostics

Within the existing **single verified canonical ledger transaction**, `ReviewWorkflow.checkpointAttemptWithSession` now distinguishes each failed precondition instead of collapsing them under a single stale error:

- `task.lifecycle` not InProgress;
- `agentId` does not own the attempt (checked **before** reading the agent record);
- `attempt.phase` not Started;
- `expectedAttemptVersion` mismatch (when supplied);
- `assignmentGeneration` mismatch;
- `expectedAgentVersion` mismatch (when supplied);
- `agent.state` not Working;
- `agent.activeAttemptId` not the task's attempt.

All return safely scoped, field-specific `StaleEntityVersion` diagnostics. No task ownership guard, atomic mutation, UUID replay rule or source-object verification was relaxed. The previous lexical validator still handles invalid summary/nextAction/evidence byte limits, invalid negative versions/generation and malformed source SHA separately before transaction.

## P3 — Actual MCP optional argument shape

`JarvisMcpTools.CheckpointAttempt` now marks `sourceCommit` and `evidence` as `[<Optional; DefaultParameterValue("")>]`. Omission from the public MCP request is valid and maps to an empty string; the five substantive fields (`projectName`, `taskId`, `agentId`, `summary`, `nextAction`) remain required. A new Server reflection regression checks `IsOptional`, `HasDefaultValue` and the empty default on both; also checks the required fields remain nonoptional. The existing bounded owner-reported evidence semantics are unchanged.

## Independent validation

- Targeted Client Release: **3 passed, 0 failed** (field-specific invalid input/stale agent+attempt version/assignment generation/wrong owner; phase Queued; Ready-after-yield; unchanged ledger head on invalid attempts). 
- Targeted Server Release: **1 passed, 0 failed** (MCP parameter metadata).
- Full Client Release: **531 passed, zero failures** — job `615504525f8443ce99bcbbb54edf712f`.
- Full Server Release: **123 passed, zero failures** — job `7ab6d971cb8e47a5be25663cbf622c30`.
- **654 full-suite tests passed**, all on the source of the frozen commit.
- `git diff --check` clean, source branch pushed and clean; `git merge-tree --write-tree main fix/checkpoint-evidence-validation` succeeded against currently integrated protocol 3.63 main.

## Evidence-only unbound audits (original scope, retained)

An assigned, Working read-only audit agent with no source ref may checkpoint summary and bounded optional owner-reported evidence with `sourceCommit=""`; the record remains explicitly `OwnerReported` or `NotProvided`. Git commit source is never invented or marked verified. Exact UUID retry is idempotent, checkpoints have immutable sequences, Working clock remains stable, and subsequent completion works. These behaviors were covered by the original tests and the full regression suite.

**Review focus:** Guard ordering and information exposure, precise semantic error types, default argument reflection / public MCP schema, no mutation for stale calls, and compatibility with the accepted protocol 3.63 source on main. Do not integrate or deploy before independent acceptance.