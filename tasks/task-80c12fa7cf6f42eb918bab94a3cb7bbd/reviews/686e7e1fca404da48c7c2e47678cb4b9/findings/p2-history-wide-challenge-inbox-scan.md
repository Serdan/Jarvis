# P2 — All-history challenge scan reintroduces per-entity Git subprocesses and eventually disables every agent inbox

**Frozen integration:** `cfed037caef3ccd68c77a3e635133c5172086c41` (merge parents main `b24d4358` and accepted durable challenges `c5a839d6`). This is a lifecycle/performance integration finding; source's core challenge behavior otherwise passes existing tests.

## Deterministic source trace
1. Main's `Client/Modules/LedgerStore.fs` `readAgentInboxRecordsAt` has a deliberate single-tree, batched `cat-file` path for agents/tasks/reviews/notifications; see `readViewRecordsAt` ~1094-1193. The main implementation removed per-entity subprocesses to keep inbox reads viable under large ledgers.
2. The staging `Client/Modules/OrchestrationApi.fs` `TaskApi.read` `AgentInbox` (~2260-2295) immediately enriches **every** ordinary self inbox via `ChallengeWorkflow.enrichInboxAt`.
3. `Client/Modules/ChallengeWorkflow.fs` `enrichInboxAt` (~716-806) calls `listAt binding snapshot` unconditionally (even for an agent with no active challenges or subscription). `listAt` (~701-718) enumerates **all** canonical challenge records, active *and historical*, then invokes `readChallenge` for each; no issuer/owner filtering occurs before the reads.
4. Each `readChallenge` (~170-189) calls `LedgerStore.readAt`. `LedgerStore.readAt` (~1050-1059) calls **`blobAt`**, which executes `git ls-tree <commit> -- <path>` (~565), and **`showBlob`**, which executes `git show <commit>:<path>` (~441). Neither uses the existing dashboard batch cache. So each ordinary inbox read launches **at least 1 + 2 × N Git processes for N canonical challenges**, plus other ledger/inbox work.
5. `ChallengeWorkflow.listAt` explicitly fails when `paths.Length > 4096`: `InvalidBinding "Too many challenges to scan; use a narrower project."` This is a per-project ledger, so there is no narrower project to choose; durable `Satisfied` and `Cancelled` history is intentionally retained. Once a project accumulates **4,097** challenges, **every ordinary agent_inbox invocation fails**, including agents with zero actionable challenge work.

The existing `ChallengeTests` use small ledgers and do not exercise query-process count or this historical cap. The integration task explicitly requests workload scaling review. The above is a deterministic implementation trace, **not an independently timed benchmark**, so no unmeasured latency numbers are claimed.

## Impact
The merged feature reinstates the per-entity subprocess amplification recently addressed in Jarvis's main inbox path. It also introduces an unavoidable project-wide failure threshold from ordinary durable history accumulation. This can strand agent continuations despite no current challenge needing action.

## Required
Rework challenge enrichment to use snapshot-pinned batched Git object reads (the existing dashboard/agent-inbox `ls-tree` + `cat-file --batch` machinery), and bound returned entries **after** filtering by agent relevance rather than rejecting all ordinary inbox reads based on historical record count. If challenge history becomes too large, use a durable per-agent/issuer index or paginated/summary projection that does not scan all historical records on every self-inbox. Preserve immutable audit history, authorization, same-snapshot semantics, cancelled-condition filtering, and the inbox's bounded output contract. Add a regression where thousands of completed/cancelled challenges and one active challenge still give a functioning targeted inbox; assert a bounded Git subprocess budget, not merely response length.
