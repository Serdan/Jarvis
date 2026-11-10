# Design review — remote Git orchestration ledger

**Date:** 2026-10-27
**Document reviewed:** `docs/specs/orchestration-v2-architecture.md`
**Reviewed revision:** `6ff2c261c93ee4137e55e49a7e5a7cc31df0f493`
**Disposition:** Architecture accepted; specification changes required before implementation. No source or specification changes made by this review.

## Executive assessment

The architectural decision is correct. A single orphan `refs/heads/jarvis-orchestration` on each project's canonical Git remote eliminates duplicated authoritative task state in source worktrees while preserving the small, orchestration-stateless Jarvis MCP server. Explicit fast-forward-only pushes and logical retry after a rejected push are an appropriate transaction/serialization mechanism for a trusted single-user, multi-agent environment.

The disposable two-clone Git test previously established that stale sibling pushes are rejected, that a retry based on the new parent can succeed, and that the source worktrees need not be touched. That verifies the core Git primitive; it does not establish end-to-end recovery or the proposed scheduling semantics.

**Major design gap:** the spec currently describes a storage protocol more precisely than the domain model. Without explicit agent slot/queue transitions and a reproducible retry identity, the implementation can still produce inconsistent scheduling or duplicate actions even though every push is serialized correctly.

## Must resolve before implementation

### RV2-01 — P0: Idempotency key must survive an ambiguous tool response

**Relevant sections:** 3 / Mutate steps 1, 3, 7-9; section 9 tests 5-6.

The text says to generate an `operationId` before attempting a command, then recover an uncertain push by searching Git history. It does **not** define who generates and durably retains that ID before the request first crosses the MCP/client boundary. If the client generates a fresh ID on every invocation, a caller repeating an apparently failed call receives a new key and can create another checkpoint, instruction generation, or task.

**Required contract:**
- Every mutating API accepts a stable, explicit logical-operation idempotency key provided by the calling agent/orchestrator. The same key must be reused when retrying the same intent; different payload with that key fails.
- A client may generate an ID for a local TUI action, but it must persist the pending command/key before remote execution and reuse it until resolved.
- Record `operationId`, canonical request hash, affected logical entities, and enough original outcome data **atomically in the same ledger commit** as the mutation.
- Use an indexed path in the same ledger tree (for example `operations/<shard>/<id>.json`) for exact lookup instead of requiring an O(history) `git log --grep` scan on every write. Commit trailers remain useful human audit data; they are not the sole lookup index.
- Define how the original response, including ledger commit identity, is reconstructed from the operation record plus commit history; a commit cannot embed its own SHA in its tree.
- Retrying with a fresh key is a *different command*; document that exactly-once effects are conditional on key reuse and cannot be guaranteed for an unknown lost request.

**Tests:** response lost after accepted push; caller resubmits same key from another clone; changed input with same key; two simultaneous submissions with same key; client/TUI crash before receiving push response.

### RV2-02 — P0: Scheduling, ownership, and turn capacity lack a normative schema

**Relevant sections:** 2 and 4; tests 3, 10, 11.

The spec retains the V1 `status`, `nextActor` and assignment revision vocabulary, while promising more than one task responsibility per agent, a single executable slot, a queue of returned review work, and preservation of parked assignments. The relationship between an agent's queue, an active turn and each task's `nextActor` is unspecified.

**Required contract:**
- Explicitly model per-task lifecycle, per-assignment generation, and per-agent execution slot/queue as distinct concepts in ledger files. Distinguish *owned*, *queued*, *ready for review*, *currently executing*, *blocked* and *parked*.
- Give a transition table for Assign, Acknowledge, Begin, Checkpoint, SubmitForReview, RequestChanges, Park, Block, Resume, Complete/Cancel, and ReviewAccept, with required versions and actors.
- Define whether a queued assignment counts as actionable to `GetAgentAssignment`; give deterministic `GetAgentInbox` ordering and explicit selection for `BeginNext`. Avoid silently preempting a busy agent or interpreting parked/review tasks as Free.
- Treat a new agent turn as an atomic change to both its task generation and the agent's slot/queue, within one ledger commit. Recheck these invariants after every rejected push.
- Specify behavior when a worker disappears mid-turn. A last-seen heartbeat cannot be inferred from ledger status; lease/timeout behavior must not claim human/agent liveness without a real heartbeat.

**Tests:** two task assignments to same agent, pending reviewer return while agent busy, parked plus new execution, racing Begin operations, restart/reconnect with stale generation, deterministic queue order.

### RV2-03 — P1: Canonical remote identity is not established

**Relevant sections:** 1, 3 Read step 1, 7 and 9 test 19.

'Existing configured remote (normally origin)' is not a durable project identity. Two devices can have `origin` pointing to different forks, mirrors or stale remotes, and each can initialize a perfectly valid `jarvis-orchestration` branch. The server has no state to notice the fork.

**Required contract:**
- Ledger root `ledger.json` includes a random stable `projectId`, ledger schema/version and initialization identity. Local project config pins the *canonical writable remote endpoint* (or provider repository ID) and expected ledger projectId.
- On each remote configuration change, missing ref, different projectId, or divergent history, stop writes and require an explicit rebind/adopt decision.
- Normalize/validate remote address safely, including SSH/HTTPS aliases, credential helpers and distinct fetch/push URLs. Do not assume remote alias `origin` is a global identity.
- Document how remote migration, renamed GitHub repositories and restored mirrors are adopted without accidentally forking authority.

**Tests:** two distinct remotes of the same-looking project; renamed host URL; incorrect pushURL; clone with no ledger branch; intentional move to a new remote.

### RV2-04 — P1: Rewrite detection exceeds what remote Git alone can prove

**Relevant sections:** 7; test 15.

The spec says that forced rewriting/deleting the ledger ref stops writes with an integrity diagnostic. A client with a previously trusted tip can detect a non-descendant head. But a brand-new clone that trusts only the current remote cannot infer that the remote was rewritten yesterday. An absent branch is indistinguishable from a never-initialized branch without an external anchor.

**Required contract:**
- Persist each client's highest trusted ledger tip and verify that any newly observed remote tip descends from it before accepting a write. Handle a failed ancestry check separately from a normal rejected concurrent push.
- No automatic ledger initialization if a local project was already bound to a nonempty ledger or a previously trusted ref.
- Specify detection limits clearly: a fresh clone cannot independently establish the complete historical integrity of a remote without an external trusted anchor, protected branch, signed checkpoint, or verified backup.
- Recommend host protection that **allows direct fast-forward pushes**, forbids force pushes and deletion, and does not impose mandatory PR review on the ledger branch.
- Record how an authorized intentional restore/reparenting requires explicit re-adoption, not silent correction.

GitHub documentation confirms that protection rules can separately restrict force pushes and deletions; administrators can sometimes bypass rules unless configured otherwise. See https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches .

### RV2-05 — P1: Cross-device retries and snapshot retention need concrete storage mechanics

**Relevant sections:** 3, 6 and 11.

Reading a fetched SHA is consistent only while its objects remain available. A plain `FETCH_HEAD` reference can be overwritten by later fetches and is not a durable named anchor; an older commit object might be pruned locally. Similarly, finding an old operation in history from a shallow/partial clone is not universally possible.

**Required contract:**
- Define the local namespaced tracking ref(s) used to keep a *verified* ledger head reachable; they are caches, not authorities. Pin live paginated snapshot commits for their duration or require a reachable remote ref ancestor.
- Specify shallow/partial clone behavior, object availability on missing parent trees, and bounded remote fetch of required history.
- At mutation start, confirm the authoritative remote tip even if the client has a recently cached tracking ref. At read start, document whether each call fetches or returns an explicitly stale cached snapshot.
- Validate a successful push by ancestry containment of its commit in the current remote tip, not exact tip equality (another writer may already have advanced it).
- Avoid unbounded history walks and repeated full task-blob parsing for every checkpoint. Put concrete latency, pagination and contention targets in the acceptance suite.

## Important refinements

### RV2-06 — P1: Protect the cutover from old task-writing clients

**Relevant sections:** 8; section 7 tool routing.

The migration says 'freeze V1 task mutations' but does not specify the enforcement point. A previously connected 3.19 client might still commit `docs/tasks/*.json` after the new ledger is live. Those writes become invisible to the V2 inbox and appear to succeed.

**Required contract:** implement explicit protocol major-version rejection for V1 task mutation tools at the stateless MCP router once the project is cut over (or a simpler global major-version cutover since the user is sole operator). Do not rely on a social instruction to stop work. Keep old task JSON readable through source-file tooling only. Verify all connected clients before removing the gate.

### RV2-07 — P1: Project privacy and remote permission setup are underspecified

**Relevant sections:** 1, 7.

The ledger stores agent instructions, checkpoints and possibly private project details in a *pushed* branch. If the project remote is public, those records will be public too; Git history retains removed content. The spec currently defaults to creating and pushing without a required privacy decision.

**Required contract:** require explicit opt-in/configuration at first ledger initialization, surface repository visibility where the provider supports it, and warn when visibility cannot be established. Define maximum input sizes and secret/redaction policy; never store tokens or raw credentials. Provide a branch-protection setup/verification test using the actual remote provider, including the interaction with signed-commit requirements.

### RV2-08 — P2: Source-spec editing workflow is not specified

**Relevant sections:** 2, 5 and 8.

Moving `spec.html` into an orphan branch simplifies authority but removes task specs from normal source checkout `docs/tasks`. This is acceptable only if authoring and diffing task descriptions remains straightforward.

**Required contract:** define CreateTask, ReviseTaskSpec, ImportTaskSpec from a source HTML file, diff/review/history of task specs, and optional read-only export to worktrees. The versioned canonical ledger copy must be unmistakable, and reissuing an assignment must explicitly decide whether to adopt a new spec revision.

### RV2-09 — P2: Source integration and ledger publication are not atomic

**Relevant sections:** 5 and 9.

The spec acknowledges this correctly, but it needs a concrete reconciliation state. A merge/push to main may succeed while the ledger write recording acceptance/integration fails, or vice versa.

**Required contract:** store PendingVerification/PendingLedgerPublication/Verified integration evidence where needed. Re-run read-only source verification before repeating non-idempotent Git steps; never repeat a merge blindly. Record which ref/commit and which remote were checked, and distinguish accepted code, integrated code and pushed code.

### RV2-10 — P2: Operational write contention needs a measurable limit

**Relevant sections:** 3, 9, 11.

A single project ref is intentionally a serialized hot spot. This is the right correctness tradeoff, but many agents checkpointing simultaneously will each fetch, build, push and potentially rebase their logical operation.

**Required contract:** bounded retry with jitter/backoff, explicit caller-visible contention errors, and acceptance targets for typical 5–10 agent bursts. Prefer batching related changes in one logical operation; do not prematurely shard task authority or introduce a database.

## Priority and implementation disposition

| ID | Priority | Disposition |
|---|---|---|
| RV2-01 | P0 | Must specify stable end-to-end operation identity and indexed deduplication |
| RV2-02 | P0 | Must define a normative scheduling state machine and queue semantics |
| RV2-03 | P1 | Require explicit canonical Git remote/project identity |
| RV2-04 | P1 | Define enforceable rewrite protection and acknowledge detection limits |
| RV2-05 | P1 | Specify local ref/snapshot pinning and verification of remote writes |
| RV2-06 | P1 | Enforce breaking cutover against old clients |
| RV2-07 | P1 | Make remote visibility/permissions an intentional configuration decision |
| RV2-08 | P2 | Define spec editing/import/export UX |
| RV2-09 | P2 | Detail integration reconciliation across two Git refs |
| RV2-10 | P2 | Benchmark contention and retry limits |

### What not to redesign

- **Do not add a server database.**
- **Do not add a server-side Git mirror, scheduler, or persistent queue.**
- **Do not return to `owner.worktree` scoping as the authority.**
- **Do not split the project ledger across task refs:** that would lose the straightforward single-ref atomic validation for per-agent scheduling constraints.
- Keep a dedicated orphan Git branch, immutable commit history, fast-forward pushes, logical conflict retries, and existing local Git credentials. Those are appropriate choices.

### Recommended revised implementation gate

Approve implementation of *only the isolated LedgerStore prototype* after RV2-01/03/04/05 are specified, and of the domain/task APIs only after RV2-02 is specified. Require reproducible two-clone concurrency, lost-response replay, branch rewrite/recovery, and source-worktree cleanliness tests before migration. Do not attempt the current V1-to-V2 cutover from this revision of the spec.

**Final verdict:** Architecture approved; current specification requires amendments before a full implementation task.