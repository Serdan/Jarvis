Fix cache key from checkout to project+agent while tracking first hierarchy scan per checkout. Preserves content-hash redelivery across differing worktrees and suppresses identical workspace guidance. .NET 11 Release: 35 client instruction/activation tests and 3 server rendering tests passed; git diff --check clean.

---

## Reviewer 1 independent frozen review — Accepted

**Frozen source:** `404751fc2e97ea1b0bb73cfe5a6114596275f0be` over base `c84ce8c954546a3866835472f2656affb19e27a3`; single commit, only `Client/Modules/AgentInstructions.fs` and `Client.Tests/AgentInstructionsTests.fs` changed. The source worktree `worker2-instruction-scope` was clean/tracking the submitted branch; `git diff --check` exited 0. No reviewer source edits.

**Independent design inspection:** `DeliveryCache.entries` now keys by project ID + agent ID, sharing per-turn `Observed` instruction fingerprints across worktree selectors of the same project, rather than duplicating the same ancestor `AGENTS.md` for each checkout. `BootstrappedCheckouts: Set<ProjectTarget>` separately tracks whether each checkout had its hierarchy read or required a one-time root bootstrap; an empty hierarchy result is still conclusive for that checkout, and failed bootstrap remains retryable. A new turn resets both sets. Fingerprints remain keyed by logical instruction path and `(scope, sha256)` or warning text, so distinct checkout content is redelivered on switch and repeated identical workspace instructions remain deduplicated. Review confirmed that delivery cache is explicitly per-process, best-effort (not an authorization boundary).

**Regression tests:** Existing real Git worktree fixture now asserts checkout A first sees three docs, checkout B sees only changed project/nested docs (not the shared workspace root), switching back restores changed A docs, and each checkout receives its own ledger-root bootstrap without repeated unchanged workspace content. Traversal and symlink rejection tests remain.

**Independent .NET 11 Release verification:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~AgentInstructions --logger console;verbosity=minimal` passed **35/35** (no skips); unfiltered `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` passed **131/131** (no skips). `git diff --check` exit 0, source worktree clean. Full Client suite not independently run; author reports 35 client instruction/activation tests and three server rendering tests.

**Disposition:** Accept this bounded instruction-delivery correctness fix. No outstanding blocking findings in the frozen two-file delta. This verdict is review acceptance only; no merge, deployment, restart, or role/task changes.
