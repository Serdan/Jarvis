# Work-first authenticated queue and claim facade (WQ02/WQ04/WQ06/WQ09)

Source baseline: `944bca7fa38cb5e973b04ce7829966cca351202b` (main when the isolated Worker 12 worktree was created).
Branch: `feat/work-native-claim-facade-worker12-20261116`.
Scope: **typed MCP/Client adapter, not a production Work/Scheduler cutover**.

## Active read contract

`ListWork(projectName, agentId, category = "All")` and
`GetWork(projectName, agentId, workId)` operate under the existing
`workspace:read` OAuth scope and route through the existing authenticated
SignalR client to the approved project Git trust binding. The client fetches
a verified WQ02 project snapshot and a pinned WQ04 role-catalog observation
at the **same Git head**. Each result supplies:

* project Git commit, project version, graph version and coverage status;
* exact Work ID/subject/queue/kind/stage/claim generation and domain/gate state;
* available/blocked/active/terminal/unknown classification using the accepted
  WQ09 projection (including hard-dependency graph/cycle checks);
* every prerequisite's canonical Work ID, stage/claim generation, completion,
  optional recorded waiver receipt, and graph-versioned reason;
* claimed launch ID and, when registered in WQ02, the ephemeral session UUID;
  liveness is explicitly **UNKNOWN** until an authenticated WQ03 session journal
  is joined. No permanent Agent ID is promoted into durable Work ownership.
A claimed Work item does **not** become Active merely because its launch's
primary Work ID matches. WQ09 recovery-held claims (source, integration, or
external-effect gates) are Blocked, even though WQ09 also includes them in
ActiveAndStalled. Clear claims without verified heartbeat timing remain Unknown;
stop-requested claims are Blocked, and invalidated/terminal-pending launches
remain Unknown. The Active category requires WQ09's verified-live row state,
which this default-off read does not establish without a trusted clock.
Available results preserve canonical WQ02 blocker-first/FIFO ranking from WQ09;
they are not re-sorted by timestamps when prerequisite stages block other Work.

Readiness means *queue observation*, **not** permission to claim, schedule or
dispatch. Missing graph/queue/message/source coverage, wrong identity, or
unresolved cycles fail closed as UNKNOWN. A missing/mismatched WQ04 role catalog
also remains visibly UNKNOWN as role authority. Account occupancy/capacity
cannot be inferred from two independently read Git snapshots. No implicit
credit grant or account/work join is made.

## Default-off transition adapter (no mutations)

`ClaimNextWork`, `CheckpointWork`, `CompleteWork` are typed
`git:write` tool methods and invoke the existing local trust/permission layer.
They preserve the original caller-supplied operation UUID (or the server-
generated UUID when omitted) and use the OAuth-bound `operator:<hash>` actor,
not `agentId`. The client validates UUID/launch/evidence syntax but always
returns an explicit `Applied=false, Status=RejectedMissingBinding` with its
verified project head and original operation UUID.
Even before the missing production writer is installed, a stale expected WQ02
project version is rejected against the pinned Git snapshot.

Exact **missing claim binding**: WQ04 same-CAS selected Work generation,
pinned role, source authority, account credit and WQ03 authenticated ephemeral
launch/session proof are not bound. The accepted WQ02 Frozen fixture has a
real `ClaimNext` CAS, but normal production `WorkQueueGitLedger.execute`
correctly rejects that path; a standalone `RoleUse.ClaimWork` preflight
cannot authorize the missing same-CAS joint writer.

Exact **missing checkpoint/terminal binding**: WQ03 verified session-terminal
and source-effect journal must be joined to the original WQ02 claimed Work
generation with recovery of lost acknowledgements. Claim/yield/terminal
execution evidence must remain distinct from domain finalization and
independent Accepted review / Integrated source evidence. No
`Completed`/Accepted/Integrated state is synthesized.

There is no new ledger schema, scheduler enablement, runtime launch,
background job, Git effect, task assignment, migration, or deployment here.

## Staged coexistence and operator recovery

Legacy Agent-inbox and task-assignment tools remain unchanged. New Work
read/transition commands deliberately do not activate legacy Ready assignments,
consume Agent response alerts, or treat caller-supplied `agentId` as authority.
They use the same trust binding, client routing and OAuth identity boundary.
Operator repair remains through the existing original-operation ledger journal
and explicit evidence-backed recovery tools; a missing receipt or unknown
dispatch must never be interpreted as a failed write.

For first live writes, integrate the future Scheduler and WQ03 first-connect
session bridge, WQ04 role/source/account same-CAS proof, bounded original
journal receipt and independently verified terminal reconciliation. Preserve
default-off until tests cover the complete bound transport.

## Focused acceptance

`dotnet build Jarvis.slnx -c Release` and
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueToolFacadeTests`.

The new fixture uses two disposable Git clones of one bare remote and an
isolated **Frozen** WQ02 ledger. It covers pinned coherent Work views, ready
versus blocked and prerequisite generations, missing source/role coverage
being explicit, refusal with original UUID and no Git writes, and WQ02
competing claim CAS with one winner plus original operation receipt replay.
Negative-space regressions cover recovery-held claims, stopped and
terminal-uncertain launches, and Blocked versus Active category filtering.
The public production claim adapter is never activated by these fixtures.

No files in the concurrently owned Scheduler, graph, preview, Windows harness
or registration API were modified.
