# Reviewer 3 — WQ-06 task-bound frozen independent review

**Proposed verdict: Accepted — exact inert/isolated source only.** This is not authorization for canonical integration, live queue producers, Scheduler activation, migration or deployment; those remain gated on WQ-04's independent acceptance/integration and a new exact combined review.

## Identity and scope
- Task: `wq-06-task-operator-conversations`
- Review: `4b18e650ca064da0bd58271ec4079492` (task-bound; the withdrawn generic `_reviews/c81ac772` was not claimed).
- Frozen exact SHA: `371fdb636f694c60c69746f15786603dc697a947`.
- Independently verified direct parent: `8ed9671543f05073f7d453728b50772138dd87f2` (using `git rev-list --parents -n 1`); reviewed worktree was clean after testing.
- Four changed files only: `Client/Modules/WorkQueueConversationTransport.fs` (new 390 lines), `Client.Tests/WorkQueueConversationTransportTests.fs` (new 338 lines), and the two F# project registrations. No WQ-04 producer/index/SCC code or production entrypoint was changed.

## Independently executed local .NET 11 Release evidence
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueConversationTransportTests --verbosity minimal`: **4 passed / 0 failed / 0 skipped** (two-clone Git CAS tests).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~WorkQueue --verbosity minimal`: **29 passed / 0 failed / 0 skipped**, inclusive of the focused WQ-06 cases.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueue --verbosity minimal`: **88 passed / 0 failed / 0 skipped**.
- Compiler emitted existing non-fatal F# warnings (FS0760/FS3369/FS0066/FS3511); there were no test or build errors. No GitHub Actions, main push, deployment, scheduler changes or Codex.

## Invariant review
- **Authority and read-only semantics.** Task and Role messages delegate posting to the existing WQ-02 `PostMessage` project CAS. `readThreadAt` uses a pinned snapshot of authoritative `Project.Messages`; reads make no claim and expose reply content only when `MessageState.Handled(replyId)` is committed and the corresponding pinned immutable record validates.
- **Coverage and source safety.** `completeCoverage` requires QueueCovered, GraphCovered, MessageCovered, SourceCovered and no dangling hard prerequisite ID on read, reply preparation and completion. A forged/missing prepared reply fails closed; prepared but unhandled content remains invisible. WQ-01/WQ-02 enforce the work/graph transition and source gates; WQ-06 does not manufacture its own scheduler credit.
- **Claim and generations.** `claimAncillary` executes the original CAS, then resolves the *original* receipt's verified operation origin and historical project state, matching request hash, receipt payload and exact version; it does not infer a lease generation from moving HEAD. `primaryMessageClaim` requires the one matching `RoleMessage` primary work/launch/generation. `activeHandler` fences inactive/STOP-requested launches, missing primary claim, dependencies, task/role message identity and stale handler generations.
- **Reply and race semantics.** Immutable reply IDs are scoped through message/reply digest paths. Preparation is durable but not a Handle/Complete side effect; `completePrepared` checks the pinned receipt and lease and delegates exactly one canonical `CompleteMessage` or `CompleteAncillary` project mutation. The two-clone tests cover losing CAS claims, original-UUID acknowledgement replay, STOP fencing, old same-launch g1-vs-g3 replies, hard dependency gating and downstream wake after the valid new reply. The original primary Plan stays claimed during ancillary handling.

## Explicit limitations / integration gates (not claimed proven)
1. WQ-04's authoritative role-demand producer and per-subject event/FIFO/high-water source reconciliation are not integrated here. `RoleWorkFromProducer` is an interface input, **not** a proof of trusted producer provenance; boolean WQ-01 coverage predicates cannot by themselves prove WQ-04 end-to-end high-water. Message-ID ordering is provisional and must be replaced/validated against WQ-04's authoritative sequence.
2. The adapter deliberately registers no public operator API, scheduler route or production handler. This review does not prove authentication/Challenge permissions, live queue demand, full production handoff, or source-writer/host activation safety.
3. Before integration, independently Accept and integrate WQ-04, reconcile interfaces and complete a **new frozen combined independent review** (including producer/high-water/role demand and two-clone negative tests). Do not infer broader acceptance from this scoped verdict.

**Decision rationale:** No blocking defect found in the exact frozen incremental transport under its explicit inert scope; the CAS, immutable reply and stale-generation boundaries are coherent and locally exercised. Accept the scoped source only, retaining all upstream production/integration gates.