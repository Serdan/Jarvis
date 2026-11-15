# Reviewer 3 — independent frozen WQ-06 transport audit

**Review:** task-bound `4b18e650ca064da0bd58271ec4079492`; original task `wq-06-task-operator-conversations`.  
**Frozen exact source:** `371fdb636f694c60c69746f15786603dc697a947`; **single parent / approved integrated baseline:** `8ed9671543f05073f7d453728b50772138dd87f2`.  
**Disposition:** **Accepted for the isolated, inert WQ-06 conversation transport only.** No Scheduler/production authority, WQ-04 integration, or combined source acceptance is implied by this verdict.

## 1. Source provenance and scope — pass

Read the task's current seq3540 parallelization gate, claimed review detail, `summary.md`, applicable AGENTS.md, exact commit, and source changes. Independently ran `git rev-list --parents -n 1 371fdb636` and ancestry check. The frozen commit has exactly the required parent `8ed9671543f05073f7d453728b50772138dd87f2`. Its diff is exactly four paths: adds `Client/Modules/WorkQueueConversationTransport.fs` (390 lines) and `Client.Tests/WorkQueueConversationTransportTests.fs` (338 lines); adds one compile entry to each corresponding .fsproj. The registered worker8 worktree remained clean after the independent tests. A repository-wide `git grep -n WorkQueueConversationTransport` found only this new module, its test call sites and .fsproj declarations: no production API endpoint, Scheduler hook, launch credit producer, or live source mutation route.

## 2. Conversation persistence, canonical CAS, and negative-space safety — pass in inert scope

`readThreadAt` (transport lines 113–147) reads a pinned project Git snapshot, checks QueueCovered, GraphCovered, MessageCovered, SourceCovered and complete hard dependency generation (lines 74–82), and reads prepared reply objects at the same snapshot only for canonically `Handled replyId` messages. Unhandled immutable prepared blobs do not appear as operator answers. Reply records use project identity + subject key + message/reply IDs + handler launch/generation/kind; a missing/noncanonical/mismatched reply object fails closed (`readReplyAt`, lines 84–111). The provisional deterministic message-ID ordering explicitly awaits WQ-04's authoritative event/FIFO cursor; do not advertise it as chronological before the combined review.

`post` (lines 153–165) delegates to the existing WQ-02 `PostMessage` transaction, not a shadow thread writer. The existing contracts `postMessage` (WorkQueueContracts.fs lines 598–630) admit `Task` subjects with no WorkId and no role work, while `Role` subjects require one matching dedicated typed role stage. The role stage comes from a caller-supplied WQ-04 producer interface; no new source-of-truth or Scheduler registration is present here. WQ-02's project mutation dispatch (WorkQueueGitLedger.fs lines 901–932, 967–972, 1020–1021) remains the sole authority for handler and work-state transitions. The existing `projectCasTyped` (WorkQueueContracts.fs lines 281–297) prevents conflicting project versions and returns the identical typed receipt on exact original-operation replay.

## 3. Handler leasing, generation, STOP and stale replay — pass

`claimAncillary` (transport lines 167–205) does not guess generation from a later project HEAD: it verifies the original `WQ02.Mutate` operation's request hash/type, immutable origin commit and pinned historical project receipt/version, then returns the handler generation from that original claim. This specifically fences same-launch g1-to-g3 reclaim after an unknown acknowledgment. Primary role work is discovered only from a genuinely claimed `RoleMessage` stage whose claim generation equals the message handler generation (lines 207–232). `activeHandler` (lines 234–288) checks live registered non-STOP launch, active unrelated primary for ancillary, exact message/work/lease identities, current generation, clear graph gates and satisfied hard dependencies.

`prepareReply` (lines 301–337) is a separate immutable Git append under the exact handler lease, requiring current coverage; it cannot itself Handle a message or finish work. `completePrepared` (lines 339–381) validates the durable reply against the bound launch/generation/kind and delegates completion to existing atomic `CompleteMessage` / `CompleteAncillary` project CAS. A second operation is not allowed to appropriate a prior Handled receipt. `resolveGatedAncillary` (lines 383–390) forwards to the existing independent hard-edge resolution CAS without terminalizing an unrelated primary. Existing WorkQueueContracts.fs lines 908–1066 enforce generation/STOP/gates/coverage at the authoritative transition as a second line of defense.

## 4. Independent bounded .NET 11 Release evidence — pass

Executed on the clean exact worker8 frozen checkout (no hosted CI and no production mutation):

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueConversationTransportTests`: **4 passed / 4**, including real disposable bare Git remote plus two clones, competing CAS writers and lost-receipt replay.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue`: **88 passed / 88**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue`: **29 passed / 29**.

Four new tests exercise (i) passive Task reads and two-clone durable ancillary reply/idempotence, (ii) dedicated primary Role message terminalization only after prepared receipt, (iii) STOP and independent high-water negative checks, and (iv) hard-edge gated ancillary recovery, lost original g1 acknowledgment, g3 same-launch reclaim, stale reply rejection and dependent-stage wake on only the valid g3 completion. The client compile issued one unrelated existing F# disposable-constructor warning `FS0760` in `Client/Program.fs:188`; no tests failed.

## 5. Authority and deferred integration boundary — pass only with explicit gate

This new module does not update canonical spec approvals, Challenges, review decisions, account admission, or integration authorization, and it exposes no production route. The caller-supplied `actor` value and `RoleWorkFromProducer` are **not authorization or proof of trusted producer identity by themselves**; no per-role security guarantee is inferred. Integration must bind a trusted operator route, the versioned WQ-04 producer/high-water contract, and source/queue coverage; this review is not proof of those routes, nor of live Scheduler dispatch, host WIP protection or deployment.

**Mandatory before any canonical combined integration/activation:** WQ-04 queue producer source must independently pass review and integrate, then WQ-06 must be combined and frozen for a *new* exact independent review of role-message demand/index, authoritative event/high-water provenance, matching/credit semantics, subject ordering and source/recovery safety. No main push, GitHub Actions, Scheduler activation, migration, deployment or Codex occurred in this audit.

**No blocking finding within the explicitly isolated inert WQ-06 transport at exact 371fdb636.**