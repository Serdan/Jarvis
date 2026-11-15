# Reviewer 2 independent P1 transport combined-source review — Accepted

**Review:** `e3ca28e647ae4b94ad8e1425b71b9fe1`  
**Original task:** `p1-ledger-git-transport-recovery`  
**Exact frozen commit:** `797a9ebe3ca22075d574d0d5619c358ac74bc5c9`  
**Base/current integration parent:** `033d6384a6e706c8262a18c26c4f43c8158462fe`  
**Previously Accepted P1 transport parent:** `c7c0ea883be9bb1fd3b2b1e54de991bae122e1c5` (review `e95d6b97e9b74f3583ba5bc20f1ce0cf`, Accepted seq3366)  
**Verdict:** **Accepted** — no blocking correctness/integration finding identified in this exact combined source.

## Independent provenance / merge-safety checks

1. `git show -s --format=%H%n%P` confirms `797a9ebe` is a two-parent *non-force merge*: first `033d6384`, second `c7c0ea88`. Both independent `git merge-base --is-ancestor <parent> 797a9ebe` checks exited 0; the accepted transport history and WQ-02+inert WQ-07 baseline are both literal, unchanged ancestors.
2. `git diff --exit-code c7c0ea88 797a9ebe -- Client/Modules/LedgerStore.fs Client/Modules/OrchestrationApi.fs Client.Tests/ReviewWorkflowTests.fs` exited **0**: the accepted transport implementation and all P1 test-file blobs are byte-for-byte identical in the merge. This goes beyond ancestry-only evidence; no conflict-resolution rewrite of an accepted transport line is present.
3. `git diff --name-only 033d6384 797a9ebe` enumerates **only** those three transport files. WQ-02 `WorkQueueGitLedger` and inert WQ-07 `AccountCapacityLedger` paths and project wiring are not changed relative to the integrated 033d parent. `git show --remerge-diff --stat 797a9ebe` showed no conflict-resolution delta. `git diff --check 033d6384 797a9ebe` exited 0. The isolated reviewer worktree at the exact commit is clean.

## Correctness of preserved accepted transport path

- **Crash-durable first journal before remote access:** `PendingIntentJournal.persistUnsafe` writes the UUID-bound serialized command to a write-through temporary file and `Flush(true)`, renames without overwriting an existing UUID, fsyncs the leaf journal directory, and calls `syncPendingJournalAncestors` for `ledger-v2/pending` and `ledger-v2` before the first remote Git preflight. The same-UUID existing-file path re-fsyncs both leaf and parent directories on replay. An injected failure after rename or at a first-write parent sync fails closed before fetch/push rather than claiming a published operation. Saved immutable operation ID/command SHA-256 survive and changed payloads reject.
- **Task/Review public binding recovery:** `TaskApi.writeUsingContext` and `ReviewApi.write` inspect a locally approved binding before journaling, but perform authoritative remote verification only inside `executeLedgerWrite`, after the original UUID+command journal is durable. DNS failure therefore leaves an exact retriable record; initial authoritative task/agent state and sequence remain unchanged.
- **Transient transport and publication exposure:** `LedgerStore` uses three bounded retry attempts with a 12 s wall-clock budget for *read-only prepublication* remote probes, classifying transient DNS/network/timeout/server failures and not retrying auth/reference/integrity failures as transients. A publication push is never covered by that retry helper. The confirmation fetch reconciles the same UUID/request hash against verified remote state. Unknown push/confirmation outcomes return `OutcomeUnknown` with `reconcile-same-uuid`, preserving the journal rather than issuing blind new publishes or fake success. CAS replan observes current head; receipt replay is immutable. Public diagnostics expose stage, attempt, elapsed, bounded exit/status class and correct one `nextAction`, not credential-bearing remote stderr.

## Independently executed exact-source tests

- `dotnet --version` = **11.0.100**. The isolated clean worktree used by test jobs had exact HEAD `797a9ebe3ca22075d574d0d5619c358ac74bc5c9` and targets `net11.0`.
- Local `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter <OR of 12 P1 transport tests>` **12 passed / 0 failed** (Jarvis job `2fbbbaa19b4b4e679025bc7e5db87f1b`, exit 0). Cases cover prepublication DNS retry, original-UUID replay/no double publication, lost post-push confirmation, CAS contention, auth/local-preparation classifications, YieldAgent checkpoint/state, unknown push timeout, post-rename directory fsync, first-write parent fsync, public Task/Review binding failures and pure-read diagnostics.
- Additional combined-source `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~WorkQueueGitLedgerTests|FullyQualifiedName~AccountCapacityLedgerTests'` **35 passed / 0 failed / 0 skipped** (Jarvis job `bfef06e19dcf4a3d93cebb2c3af52627`, exit 0). This tests retained WQ-02 and WQ-07 code on the exact combined binary, rather than relying solely on the merge graph.
- `git status --porcelain=v1` empty; `git diff --check` exit 0. Source not modified by reviewer.

## Boundaries

Fault-injection proves deterministic failure-path behavior, not a physical power-cut recovery. First-write parent/rename fsync injection executes on Unix; the Windows write-through branch was not independently run. The **12 P1 + 35 WQ-02/WQ-07** selected tests are not a claim that the full Client.Tests or Server.Tests suites passed. No GitHub Actions, main push, production deployment, source-branch modification or Codex were invoked.

**Decision: Accepted for exact `797a9ebe3ca22075d574d0d5619c358ac74bc5c9`; Lead retains integration authority.**