# WQ-03 source handoff: host safety dependencies (not yet satisfied)

Status: **fail closed; do not activate Scheduler/source-writing handoff**.

WQ-03's project Git CAS can fence a stale claim and check a remote WIP SHA.
It cannot, by itself, prove that the remote will retain that commit, that a
different account-authorized process cannot overwrite/delete its ref, or that
all physical source mutations and original pushes have terminated. A positive
`ls-remote` or clean `git status` is not protection or job reconciliation.

## Required before a successful YieldSource is permitted

1. **Host-enforced immutable source retention.** The trusted Git host/operator
   must install an immutable, generation-scoped `refs/heads/jarvis-wip/**`
   policy. Ref creation must be create-only; update/force-push and deletion
   must be rejected, including for this agent's effective credential and any
   configured bypass roles. Retention must outlive all unfinished stages,
   checkpoints and successor claims that reference the SHA; disable pruning
   that could remove the only reachable continuation object. Configuration
   needs an independently checked host-side ruleset/policy version and effective
   credential/bypass evidence, not a caller assertion or a local Git ref read.
   For GitHub, confirm the ruleset actually applies to the entire nested WIP
   namespace and blocks both updates and deletions for the active credential.
   Do not attempt destructive probes on a production ref merely to infer policy.
2. **Host-owned complete effect journal.** Every permitted mutating job/process,
   source git push/merge and retry must acquire a durable original operation ID
   before dispatch, bound to exact project, work ID, launch UUID, claim
   generation, repository identity, worktree identity and target/ref. The
   authoritative reconciler must prove the original process terminal and any
   remote push outcome settled, not merely absent at one observation. A client
   restart, missing journal or incomplete coverage is **RecoveryBlocked**.
   Unknown pushes retain original IDs and source/worktree/token fences; negative
   current remote ancestry is not proof that the push will never land.
3. **Atomic source admission and succession.** Each source claim needs a clean,
   Git-registered detached, generation-scoped worktree at the exact pinned
   base or independently fetched continuation SHA *before* ClaimNext's project
   CAS can issue actionable work. Do not reuse a predecessor worktree or clone.
   Initial publication must validate the approved repository identity and
   base commit against the remote target. Late old session project writes are
   generation-fenced; account-authorized physical writes require worktree and
   effect-journal isolation as above.

## Current implementation boundary

`WorkQueueGitLedger` validates initial approved-remote base ancestry and
generation-pinned source checkout before project publication/claim.
`YieldSource` still inspects local HEAD/status/commit ancestry and WIP
remote tip, **then rejects** normal requeue because host protection and the
complete journal are not yet available to this isolated adapter. An agent may
record `MarkSourceRecoveryBlocked` without releasing its claim; that
self-reported record is not a substitute for the missing independent proofs.
Neither the WQ-03 adapter nor any tests imply that a local bare Git remote or
GitHub branch is protected. The source bridge remains inert.

## Validation/activation gate

- Deterministic two-clone tests: forged repository, missing/non-ancestor base,
  ordinary stale successor clone, correctly pinned detached successor, dirty
  checkout, drifted WIP ref, and clean source with absent authoritative host
  evidence.
- Local .NET 11 Release tests; independent exact frozen review after code changes.
- Operator/Lead must supply verified host policy + durable journal bridge,
  additional real host-policy negative tests, a reviewed replacement for the
  temporary fail-closed gate, and explicit cutover authorization. No GitHub
  Actions, deployment or production migration are part of this change.
