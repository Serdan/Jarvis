# Independent review — checkpoint validation and evidence-only audit support

**Frozen source:** `1c1be4ecc2eb1c85081328297a02c0795b7c45f1` on `refs/heads/fix/checkpoint-evidence-validation`, targeting `main`. Task `task-9677a1ef10f046f983cbdabf39d30efd`. No merge or deployment.

## Root cause
A Wayfold audit worker got the generic `Invalid Working checkpoint metadata` even though its attempt was unbound. The source commit has always been optional: `checkpointAttemptWithSession` verifies a source object **only when sourceCommit is nonempty**, but summary, nextAction and evidence have independent **8192-byte UTF-8 limits**. A large audit report in `evidence` can thus fail before the ledger transaction, misleading the worker into blaming missing source binding. This change does not weaken binding or ownership rules.

## Source changes
- Validate `taskId`, `agentId`, nonnegative version/generation assertions, required `summary` / `nextAction`, optional `evidence`, and optional `sourceCommit` with **field-specific InvalidBinding errors**.
- Required strings must contain non-whitespace text and fit 8192 UTF-8 bytes. Evidence can be absent/empty or up to 8192 bytes. Oversized audit reports are rejected with an explicit suggestion to store the full report separately and pass a bounded reference; no silent truncation or unlimited ledger record.
- Empty or null source SHA means no source commit claimed, appropriate for read-only/unbound audits; a supplied SHA must match the ledger's shared lowercase 40/64-hex Git OID validation and refer to an actually available Git commit. Unknown objects remain `SourceUnavailable`, lexically malformed IDs are `InvalidBinding`.
- No changes to the atomic ledger transaction, expected versions, immutable checkpoint path, UUID replay rules, source verification or owner-reported evidence status.
- Tool description now declares each field and bound.

## Regression evidence
- `UnboundReadOnlyAuditCanCheckpointEvidenceAndCompleteWithoutInventingSource`: two Git clients, task assigned and Working without source binding; checkpoint evidence persists with `OwnerReported`, null/empty source, immutable sequence, replay by identical UUID `AlreadyApplied`, second checkpoint, stable Working clock and complete-to-NeedsWork.
- `CheckpointReportsExactInvalidFieldAndUTF8LimitsBeforeLedgerMutation`: required empty/oversized summary and nextAction, oversized multibyte evidence, malformed SHA, negative version/generation, missing 40-hex commit, stale generation all fail without changing the ledger head; **exactly 8192 bytes** of emoji evidence and summary pass.
- Both targeted Client Release tests **passed (2/2)**. Full Release Client + Server suite in progress at job `db5bf6bd3ba845f7bd6c4b0e011acba0`; update result once complete.
- `git diff --check` clean; source branch pushed clean; `git merge-tree --write-tree` into current main succeeds.

## Review focus
Check the shared 40/64-digit OID validity against Git source-object lookup, UTF-8 byte boundaries, unchanged owner/phase checks, transaction/error ordering, idempotent replay and that missing source doesn't accidentally become verified evidence. A separate large-report storage protocol is **not** implemented: this task only supplies explicit rejection and reference guidance, consistent with conditional requirement.
