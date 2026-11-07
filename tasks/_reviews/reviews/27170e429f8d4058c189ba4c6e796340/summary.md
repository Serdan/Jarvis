# Re-review: AGENTS.md retrieval and cross-client state-cache changes

**Branch:** `feat/incremental-agent-instructions` at `27170e429f8d8058c189ba4c6e7963407a8a3e01`
**Base:** `main`
**Verdict:** Changes requested (two P2 findings).

The original reported defects have been addressed:
- Automatic AGENTS.md delivery is complete-or-omitted; explicit `get_agent_instructions` retrieves workspace, checkout and nested hierarchy with paginated UTF-8-safe continuation for arbitrarily large documents.
- A->B->A oversized/complete/oversized transitions re-deliver the omission warning.
- Cached ordinary Working-state lookups no longer force GitHub fetches on every source call; the binding loader reuses a single verified ledger snapshot per actual refresh.

**Verification:** Re-ran `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`: 390 passed, 0 failed. Re-ran Server Release: 95 passed, 0 failed. `git diff --check 3d33253..27170e4` clean. Worktree and tracked remote at same pinned HEAD.

**Outstanding findings:**
1. `findings/missed-implicit-activation.md` — successful non-inbox source operations (and failed commands with an activation side effect) can transition Ready->Working, but the server's command-type success filter never broadcasts that transition. Other clients retain stale state until a fallback refresh.
2. `findings/overbroad-invalidation.md` — project-wide invalidation on every successful TaskWrite, including writes unrelated to any agent state, can force remote Git fetches for all active agents under ordinary multi-agent activity, partially reintroducing the latency regression.

Recommend emitting explicit typed mutation/invalidation metadata from the client side where a transition actually occurs, delivered through the existing response pipeline, rather than approximating state changes by command kind and response success. Add an end-to-end or transport-level test that exercises the complete server->client invalidation path, alongside remote-fetch-count tests.

No source modification, merge or deployment performed during this review.
