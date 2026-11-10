# Independent review — checkpoint evidence validation for unbound audits

**Verdict: RequestChanges.** One P2 acceptance gap and one P3 public API/docs mismatch.

Frozen source: `1c1be4ecc2eb1c85081328297a02c0795b7c45f1` on `refs/heads/fix/checkpoint-evidence-validation`, base `2a52211ba2cc2c3fa3204fe8ea2d1c6f9ca27965`, target `main`. No reviewer source edits, integration, or deployment.

## What works

- The preflight validator now distinguishes invalid/negative *input* versions, malformed task/agent identity, required summary and nextAction, oversized evidence, and malformed source SHA; the unchanged Git lookup reports a missing 40/64-character OID as `SourceUnavailable`.
- All text bounds are UTF-8 byte counts. Evidence, including emoji, is accepted at **exactly 8192 bytes** and rejected over the bound without truncation.
- An unbound read-only Working agent can store a progress checkpoint with sourceCommit empty, preserving owner-reported evidence without fabricating a source commit. Generation, immutable checkpoint sequence, attempt version increment, Working clock, repeated UUID replay and eventual completion behave as intended.
- No unbounded ledger field or new source-binding abstraction was introduced.

## Findings

**P2 — semantic checkpoint validation is still generic** (`findings/semantic-checkpoint-errors.md`).

`ReviewWorkflow.fs:1548–1554` still combines lifecycle, owner, phase, expectedAttemptVersion, assignmentGeneration, expectedAgentVersion, agent Working state and active attempt checks into one `StaleEntityVersion "Checkpoint requires exact current Working versions and generation."` error. The task expressly requires **field-specific errors for invalid versions and ownership/phase**. New tests assert a generic stale failure rather than differentiating these conditions. Return precise diagnostics within the existing atomic transaction and test stale version/generation and ownership/phase separately.

**P3 — optional source/evidence descriptions contradict public tool schema** (`findings/optional-tool-args.md`).

`Server/McpTools.fs:1403` says caller may omit `sourceCommit`, and that `evidence` is optional, but `CheckpointAttempt` declares both as required `string` arguments at line 1404. The published MCP shape lists them as required. Either give both empty-string defaults with Optional attributes and verify the schema or correct the description to require explicit empty strings. Current unbound audit tests always supply both.

## Independent validation

- Full Client Release suite: **530 passed, 0 failed, 0 skipped** — job `c9c509afe5df45ab9d64045be0f556e8`.
- Full Server Release suite: **122 passed, 0 failed, 0 skipped** — job `7136df99fc5b449dbb147f3b1081900d`.
- Both new focused Client checkpoint tests independently rerun: **2 passed, 0 failed** — job `f801538a1d254de79ada4e2287daee2f`. (Included in 530; not additive.)
- **652 full-suite tests passed** in total.
- `git diff --check`: clean; exact frozen checkout HEAD and clean worktree verified.
- `git merge-tree --write-tree main 1c1be4e`: succeeded without conflicts.

The implementation fixes the reported evidence-only root cause; the requested semantic error resolution is still incomplete. No deployment authorized or attempted.
