# Reviewer 2 — frozen Roslyn project-scoped prewarm gate correction

**Review:** `5564d6c58e164b48ab9bbe1ccad6c35b`; **frozen source:** `5564d6c58e16b48ab9bbe1ccad6c35b5e704c0de`; **base:** `944bca7fa38cb5e973b04ce7829966cca351202b`. Previous ChangesRequested: `a6d9f140e8cb4f7b8a9d0f24a2b8ce61`, decision sequence 7154.

**Verdict: Accepted only for the project-scoped P2 fast-negative prewarm correction.** This verdict does *not* close the canonical Roslyn LSP hooks task, authorize integration or deployment, or override the outstanding full-task P1 acceptance requirements.

## Independent source inspection

Examined the exact `5564d6c58` corrective diff, the canonical task spec, preceding review summary and two findings, and surrounding code in `Client/Modules/CodeQualityHooks.fs`, `Client/SignalR/Client.fs`, `Client/Modules/ProjectPaths.fs`, and `Client.Tests/CodeQualityHooksTests.fs`.

- `commonGitDirectory` uses bounded local `.git`/linked-worktree `commondir` metadata and fails closed when unavailable; it does not invoke Git.
- `hasApprovedPrewarmCandidate mainRoot` matches the selected project's Git common-directory identity with an operator-approved, non-main checkout possessing an enabled Roslyn hook. Approval/hook for project A no longer makes a sourced inbox for unrelated project B enumerate Git worktrees.
- The candidate gate is evaluated inside `InboxPrewarmDispatcher.TrySchedule`'s asynchronous delegate, never in the synchronous TaskRead response path. Later `prewarmFromInbox` still resolves a distinct exact Git-registered source-ref worktree and applies checkout-specific prewarm approval; unknown/ambiguous source and invalid metadata fail closed.
- New two-project tests cover A positive/B negative (even with an unapproved enabled hook in B), then B positive only when individually approved. The original disabled/no-hook and main-not-approved negatives also remain. These are pure metadata/negative-predicate unit tests, not actual mixed-project Git subprocess or production AgentInbox integration instrumentation.

## Independently executed

At the exact frozen head in the clean registered `worker6-roslyn-lsp-hooks-20261116` worktree:

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter "FullyQualifiedName~CodeQualityHooksTests|FullyQualifiedName~SignalRDispatchTests" --nologo`: **exit 0; 18 passed, 1 skipped, 0 failed** (.NET 11). The skipped live Roslyn recovery case requires a configured `JARVIS_ROSLYN_LSP_DLL`; no official live acceptance was independently claimed. Compiler warnings occurred, no errors.
- `git diff --check 768f23da13519beb0b54fa3c1f517bfd8dc23700 5564d6c58e16b48ab9bbe1ccad6c35b5e704c0de`: **exit 0**.
- Post-test `git status --porcelain=v1`: **clean**.

## Residual requirements (not waived)

Previous P1 full-task acceptance findings remain open: unfiltered client-suite failure/timeout triage; deterministic real TaskRead -> AgentInbox -> source-bound async prewarm and delayed retry/idempotence integration proof; edited Wayfold small/large formatting parity including final newline, latency, RSS and diagnostics; and independent worktree/project/analyzer contexts sharing the official Roslyn daemon. Source-only correctness plus focused tests cannot satisfy these.

One existing retry edge is also visible: when there is no approved candidate, `TrySchedule`'s delegate returns `true`, caching the assignment key; enabling hooks/approval later within the same Working assignment will not by itself re-trigger inbox prewarm without a new key/runtime. First-edit lazy initialization remains an alternative. This predates the present correction and should be addressed under open delayed-binding/activation acceptance rather than used to reject this narrow P2 fix.

No source edits, merge, deployment or Wayfold mutations performed by reviewer.
