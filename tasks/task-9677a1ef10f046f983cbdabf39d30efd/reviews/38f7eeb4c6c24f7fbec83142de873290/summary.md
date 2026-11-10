# Independent re-review — checkpoint evidence validation

**Verdict: Accept. No blocking findings.**

Frozen source `0b035a792404f33959ca1cb64884b73737325c72` on `refs/heads/fix/checkpoint-evidence-validation`; correction to previously rejected `1c1be4ecc2eb1c85081328297a02c0795b7c45f1`.

## Verified corrections

- Previously generic checkpoint semantic checks are now separate and field-specific, *inside one canonical ledger transaction*: task.lifecycle, attempt owner (before loading an agent record), attempt.phase, expectedAttemptVersion, assignmentGeneration, expectedAgentVersion, agent.state, agent.activeAttemptId. Error type remains `StaleEntityVersion` with actionable, safely scoped messages. No mutation on rejected requests.
- `Server/McpTools.fs` marks both `sourceCommit` and `evidence` `[<Optional; DefaultParameterValue("")>]`; existing mandatory fields remain mandatory. Server tests assert reflection metadata.
- Unbound read-only Working audits retain evidence-only progress checkpoints and explicit owner-reported evidence provenance; source SHA is optional and not fabricated. No change to source-object verification, 8192 UTF-8 byte bounds, immutability, generation/UUID replay or Working ownership.
- Tests assert exact stale field and unmodified canonical head for wrong owner, version, generation, queued attempt and Ready agent after yield. Error ordering prioritizes ownership before agent record, avoiding exposure from a wrong-owner agent lookup.

## Independent validation

- Full Client Release: **531 passed / 0 failed** — job `b131121a1f7d4362b31cbce55f33915f`.
- Full Server Release: **123 passed / 0 failed** — job `7b089e4a9e10449d952f1ac793ac4c84`.
- **654 full-suite tests passed**, 0 failed.
- Exact frozen worktree HEAD verified and clean; `git diff --check` passed.
- `git merge-tree --write-tree main 0b035a79` passed without conflicts.
- No integration or deployment performed.

**Decision: Accept.** Integration and release remain Lead/operator responsibilities.
