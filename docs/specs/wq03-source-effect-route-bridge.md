# WQ03 source-effect route bridge — nonproduction boundary

Baseline: canonical `origin/main` 8ed9671543f05073f7d453728b50772138dd87f2.
This is a follow-on to the independently Accepted, integrated
`Client/Modules/SourceEffectJournal.fs` inert foundation. It does **not**
authorize Scheduler, YieldSource, source succession, or deployment.

## Actual source-dispatch routes

The source-side receiver is `Client/SignalR/Client.fs`:
`receiveCommandForTarget` authorizes the operation before entering
`dispatch`; `RunProjectTask` is executed in a special authorized branch
rather than the ordinary `dispatch` branch. The diagnostic deny-only check
is deliberately located at that shared authorization boundary.

| Route | Concrete implementation | Source operation UUID | Bridge candidate | Terminal proof |
| --- | --- | --- | --- | --- |
| `RunCommand` | `ClientShell.runCommandIn` | **absent** | ManagedCommand | **unavailable** (process tree, arbitrary git push/merge) |
| `StartJob` | `JobManager.startJobIn` | **absent** | ManagedJob | **unavailable** (detached process tree) |
| `RunProjectTask` | `ClientShell.runProjectTaskIn` after special authorization | **absent** | ManagedCommand | **unavailable** (arbitrary configured process) |
| `WriteFile` | `ProjectBrowser.writeFileIn` | **absent** | FileEdit | awaited host-owned synchronous result only |
| `PatchFile` | `ProjectBrowser.patchFileIn` | **absent** | FileEdit | awaited host-owned synchronous result only |
| `ImportFile` | `importFile` | **absent** | FileEdit | **unavailable** (network/import ambiguity) |
| `GitCommit` | `ClientShell.gitCommitIn` | **absent** | GitCommit | **unavailable** (external process) |
| source `git push`/`merge` | reachable through arbitrary command/project-task/job | **absent** | conservatively ManagedCommand/Job | **unavailable**, late original remains possible |
| source retry | no dedicated source-retry RPC; ledger `RetryPendingIntent` has a ledger UUID only | **absent** | **uncovered** | **unavailable** |

Further source-capable pathways (`CreateWorktree`, `RemoveWorktree`,
`CreateSkill`, `BrowserExportDom`, `ScheduleExecution`,
`CreateProject`, task-spec export/import and legacy/out-of-band OS/Git
effects) remain **explicitly uncovered**. The scanner cannot prove arbitrary
process behavior or prohibit external credential holders from changing source.
A successful journal row never implies complete route coverage.

`AgentCommandInfo.operationId` explicitly returns `None` for the seven
concrete source RPCs listed above. The IDs in `TaskWrite`, `ReviewWrite`,
`LedgerWrite` and `RetryPendingIntent` are distinct **ledger** operations,
not original source-effect identities. A generated client UUID, request hash,
SignalR correlation ID, job ID or server-side retry ID must not be substituted.

## New bounded interfaces

`SourceEffectRouteBridge.classify` distinguishes potential journal candidates,
uncovered routes, and read-only calls. A host-built `SourceRouteInvocation`
may pass a previously authenticated, caller-stable original source UUID,
project/ledger/epoch/repository/approved-remote fingerprint, exact serialized
request hash and registered generation checkout to
`executeDiagnostic`. That diagnostic function commits the original UUID
before invoking the *actual awaited* handler and never redispatches the UUID
after timeout, crash or restart. The original intent scope must also match
an independently supplied host-trusted binding (`TrustedScope`); the
worker-provided intent alone cannot authenticate the project/ledger.
A successful awaited WriteFile/PatchFile may
receive a narrow host-owned terminal row. Other returned results remain
`Uncertain` because the host cannot yet attest the full process tree,
original remote push resolution, or asynchronous import terminal state.
Errors and exceptions remain uncertain.

No production `SourceRouteInvocation` constructor is wired to an RPC:
existing client requests lack the original source UUID and authoritative
lineage envelope. This is intentional. Test harness callers cannot turn this
diagnostic source row into permission to yield: `inspectForHandoff`
still reports `RecoveryBlocked` and missing route/host coverage.

The sole runtime hook is the explicit *nonproduction deny-only* environment
gate `JARVIS_WQ03_NONPRODUCTION_DENY_UNBOUND_SOURCE=deny-unbound-v1`.
Only registered checkout selectors prefixed `wq03-` are examined; for
mutation candidates and uncovered routes the hook denies the handler
**after authorization but before OS/Git dispatch**. Unset/default gate
preserves all legacy RPC behavior. The hook does not create journal rows,
invent original source IDs, or activate YieldSource.

## Remaining hard gates

1. Add authenticated stable source-effect original UUID and exact bound WQ03
   project/ledger/launch/work/claim/checkout context to all source-mutating
   client RPCs, including delayed command/job/retry, without trusting agent
   labels or ad hoc JSON. Bind to canonical project state at dispatch and
   reject stale generation/late original effects.
2. Make every path including scheduled/detached subprocesses and other
   credential-authorized Git mutations either completely journaled or
   denied while source authority may be handed off. Supply authoritative
   process-tree lifetime and remote-push terminal receipts; no positive
   result from `ListJobs`, `ls-remote` or clean worktree.
3. Add a canonically durable `MarkSourceRecoveryBlocked` transition on
   failure using the project's current CAS generation *without releasing*
   claim, launch or token. The diagnostic SQLite fence alone is not a
   substitute for that project transition.
4. Independently verify host-enforced create-only, anti-force, anti-delete
   `refs/heads/jarvis-wip/**` protection, all effective credential bypass
   paths, and retention. This remains an external operator gate.
5. Require a **new** independently reviewed activation/cutover design and
   explicit operator authorization before any source succession.

No GitHub Actions, main push, Scheduler activation, deployment, migration,
production ref mutation or destructive host-protection probes were used.
