# WQ-03 portable source handoff: practical workflow and recovery

Status: **isolated/inert adapter; no production scheduler activation or migration**. The operator's 2026-11-16 simplification directive supersedes the former rule that every WIP ref must have host-enforced immutable retention and every possible external process must have a globally complete journal before a useful source handoff is permitted. Keep these stronger measures optional unless a concrete failure makes them proportionate.

## Normal handoff (A to B)

1. Work in a separate Git-registered generation worktree. Commit all tracked and untracked work that belongs in the continuation; confirm the worktree is clean. Keep the source branch, target ref, pinned base SHA and generation.
2. Assign a stable **original push operation ID** *before* attempting the push. Record the exact Git command (for example `git push origin HEAD:refs/heads/jarvis-wip/I/L1-g1`), attempted commit SHA, captured exit status/response or **Unknown** if acknowledgement was lost. Use ordinary non-force Git push; do not overwrite a diverged remote tip.
3. Inspect `git ls-remote --refs <approved-fetch-url> <wip-ref>` from the trusted source binding. Record the observed remote SHA separately from the push's reported outcome. A lost acknowledgement remains `PushOutcome.Unknown` even if an independent read later observes the expected SHA.
4. Submit `YieldSource` with a `SourceCheckpoint` recording `RepositoryId`, `TargetRef`, `PinnedBaseCommit`, `ContinuationRef`, `ContinuationSha`, `PushOperationId`, `PushCommand`, `PushOutcome`, `ObservedRemoteSha`, `RecoveryNote`, summary, next action and test evidence. The adapter checks the actual approved remote ref, local HEAD, cleanliness and ancestry before the Git ledger publishes it.
5. The successor fetches the recorded WIP ref and verifies it still equals the checkpoint SHA, creates its own isolated generation worktree at that SHA, and claims the next stage through the canonical project ledger. The original checkpoint and task conversation remain accessible.

**Meaning of proof:** an exact remote observation establishes *current reachability*, not permanent immutability, that the original push reply was received, or termination of every background process. The source path remains inert until the operator authorizes production activation.

## Missing WIP, lost acknowledgement, non-fast-forward, or unknown process

Do not infer failure from a lost reply or success from a source commit alone. First inspect the original operation ID/command and any available process result, then query the remote and compare the observed SHA with the local continuation and prior checkpoint. If current remote equals the expected SHA, a lost acknowledgement may be reconciled via `PushOutcome.Unknown` plus the independently observed SHA; retain the Unknown outcome in the checkpoint rather than rewriting history.

If the remote ref is absent, diverged or cannot be read, keep both known SHAs and the failed command/output. A non-fast-forward push must be visible, with the **actual remote SHA** shown; fetch both sides and decide whether to incorporate the other work, use a new continuation ref or repair the record. Never default to force push, delete a remote ref to escape a conflict, or fabricate an observed success. For a genuinely unknown external process, inspect it before retrying and keep the current claim `SourceRecoveryBlocked` until an authorized agent records a reasoned resolution.

`MarkSourceRecoveryBlocked` accepts a durable reason; include at least the original push operation ID, command, attempted local SHA, WIP ref, observed remote SHA (or `missing/unknown`), captured failure/uncertainty, and exact next recovery action. This is intentionally inspectable text, not a blanket permanent lock. A new authorized repair can resolve the issue after examining the evidence.

## Authorized direct repair of inconsistent Git ledger records

If canonical records themselves are inconsistent, an authorized agent may directly correct them with an auditable Git change rather than waiting for code enforcement to be relaxed. Record the current remote ledger head as a **backup SHA**, preserve the original command/receipt and relevant source ref tips, make the smallest coherent edit (including canonical state hash/receipt relationships if applicable), review `git diff`, and commit a **repair note** explaining what was observed and why the record changed. Use normal Git ancestry/CAS semantics; on conflict fetch and reconcile instead of overwriting unseen changes. Preserve the original broken state via the backup SHA and make the new state independently readable by another clone. Do not bypass the recorded operator authorization required for production rollout.

## Acceptance checks

Two-clone disposable Git tests cover: committed A-to-B source continuation with task message and checkpoint preserved, independent recovery after a lost push acknowledgement, explicit non-fast-forward with both SHAs retained, missing WIP, dirty checkout, drifted WIP, and a durable unknown-operation recovery reason. Run .NET 11 Release tests and request a new independent frozen review before Lead integration. No hosted Actions, Codex via Jarvis, deployments, restarts or scheduler activation.
