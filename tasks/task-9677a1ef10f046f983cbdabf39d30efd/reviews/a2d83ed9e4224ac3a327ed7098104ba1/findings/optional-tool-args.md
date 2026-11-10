# P3 — Public optional source/evidence documentation disagrees with tool schema

Frozen source: `1c1be4ecc2eb1c85081328297a02c0795b7c45f1`.

`Server/McpTools.fs:1403` now describes `sourceCommit` as optional: "**omit or pass empty** for read-only/unbound audits", and `evidence` as optional. But the actual `CheckpointAttempt` MCP method signature at line 1404 declares `sourceCommit: string, evidence: string` as **required** arguments; neither has `[<Optional; DefaultParameterValue("")>]`. The public registered tool schema likewise lists both as required. The new unbound audit test supplies both explicitly (`sourceCommit=""`), so it does not cover a caller following the documented omission guidance.

With the current schema, omission is rejected before reaching the new validator. Either make both method parameters truly optional with empty-string defaults and add a public-shape/schema test, or describe them as **required parameters which may contain empty strings**. The former better matches the task's intended ergonomics for evidence-only and unbound audit checkpoints.

Severity: **P3 API/documentation consistency**. No evidence of ledger corruption; explicit empty strings work.
