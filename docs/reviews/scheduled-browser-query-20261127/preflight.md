# Scheduled browser query preflight (2026-11-27)

Task: robustness-scheduled-browser-query-projection-20261127, spec blob
6dd90ea8a9c982e06576745d8521ceec31dacbd8, assignment generation 1.
Verified remote main: 291b5499051fbddfff1c76308e1a0202a5d1862f
(`git ls-remote origin refs/heads/main`, exit 0; isolated worktree based on
that exact SHA). Only the new preflight test and review directories are owned.
This is **not production wiring** or a scheduler policy change.

## Observed registered path and semantics

- `Server/McpTools.fs:2475-2479`: registered MCP read-only
  ListScheduledBrowserExecutions(agentId, deviceId?) has no project/status/
  action parameter; constructs the empty union case. At 666–674 the command
  requires workspace:read. At 907–989 authenticated account and scopes are
  checked, then command routed through the selected account/device.
- `Common/Messages/AgentCommand.fs:90,1123,1255,1356,1440,1544,1620,1763`:
  empty case, name, capability, no project association, no detail.
  `Client/Modules/PermissionPolicy.fs:144-152`: no confirmation for these reads.
  `Client/SignalR/Client.fs:306-318`: absent project is Unscoped.
  `Client/SignalR/Client.fs:1119-1124`: dispatches to BrowserScheduler.List()
  or BridgeDisabled. Not scoped by agentId. The account/device boundary is
  not a project filter; never derive project identity or authorization from an
  agent label.
- `Client/Modules/BrowserSchedules.fs:195-225,234-259,354-430,617-628,977-978`:
  exclusive client-local ownership lease; restored schema v1/v2/v3,
  in-memory actions under gate; List sorts by stored DueAtUtc, calls describe
  for every row, then serializes the whole array. Get looks up one ID.
  Store bounds ordinary retained history to 100 on mutation; query cannot
  reconstruct removed history. A read does not tick or persist. Never reopen
  the live private store for this feature.
- `describe` (157–193) supplies raw status, due, check-in wake, project,
  legacy BrowserScript/Once/Every normalization, CLI metadata, script path
  (not script contents), raw Error/LastResult, URL origin and other details.
  The legacy direct ArgsJson/CommandArgs values are omitted, but raw
  Error/LastResult may include arbitrary diagnostics (including recovery
  request JSON, cf. 69–104, 312–320). Do not carry those fields into the
  compact contract or claim the legacy response is comprehensively redacted.
- `ScheduleExecutionCommand` (Common lines 92–113) supports RunCommand and
  BrowserScript, Once, Every and AfterAgentIdle. Both schedule families share
  the store. Worktree selector uses project::worktree (Server lines 269–282);
  Common.ProjectSelector.identity (Common/ProjectSelector.fs:17–26) parses
  rather than stripping malformed selectors.
- `BrowserSchedules.fs:261–352,390–428,603–615,777–851`: recurring ordinary
  outcomes rearm pending at the next cadence; held/failed/etc. may be
  last-occurrence diagnostics, not the current status. Cancellation of running
  marks Disabled and leaves running until completion; suspension is not a
  runnable cadence; interrupted one-shot is outcomeUnknown, not failed/replayed.
  WaitingActivity requires fresh authenticated idle activity; pending idle
  deadlines are candidates, not proof of readiness. Separate check-in wake
  applies only to actual isReadyResume and does not change cadence. Claim
  also depends on capacity, hold, verified activity and current revision.
  Never replicate the claim algorithm in a read.
- Sibling `ListScheduledExecutionsCommand` and Get (Client lines 1068–1081;
  Server lines 2535/2542) share List/Get; no change to them in this preflight.
  Exact-source git grep found no production direct caller of the browser-list
  command beyond registration/dispatch/capability/permission cases and
  Client.Tests/BrowserBridgeTests.fs:1200 (permission list); docs
  browser-extension/README.md:34 and docs/BrowserOneShotScheduler.md:10
  advertise existing no-argument history. Tool consumers external to this
  repository are not enumerable; preserve compatibility, do not assume none.

## Proposed narrow query contract

For current-project inspection, pass the registered project explicitly,
with current as the default status selector and BrowserScript as the default
action selector. Add explicit all-history or exact raw status, and explicit
RunCommand/Any action alternatives. Unknown selector values fail validation;
do not silently broaden to Any or all projects. The client must resolve the
requested project against the selected device's registered/authorized
workspace using existing typed routing before reading; no raw client-side
string or MCP actor label grants permission. Include canonical project identity
for all its registered checkout selectors, using the existing parser and
canonical project identity (exact case; resolve canonical spelling upstream).
Malformed stored selectors do not match. No cross-project aggregation on this
scoped route. Empty successful result means no *matching retained records*,
not necessarily no live agents or no scheduler activity.

Current = all except known terminal statuses canceled/completed/failed/
held/heldLaunches/noEligibleReady. Include suspended, outcomeUnknown and
unrecognized raw states as attention/evidence, never label them active;
running/disabled remains visible. An explicit All or ExactStatus selection
includes matching retained terminal rows. Preserve unknown action/trigger
under Any with no asserted eligibility. No repeated pagination/truncation
beyond existing retained data; all matches returned.

Proposed producer: take the lock once, sample the service's injected clock
once, filter raw ScheduledAction list by authorized project/status/action,
then order by stored DueAtUtc and ordinal ID, then project only safe fields,
then serialize. Return observedAtUtc and clockSource=clientSchedulerClock;
preserve selected device source from authenticated route metadata. Do not
substitute server or interpreter time. Return raw dueAtUtc, nullable candidate
due and nonnegative secondsUntilCandidateDue (fractional seconds allowed),
raw status, current-state classification, disabled, normalized trigger/action,
repeat interval and update time. Pending ordinary due is candidate only;
pending idle is awaitingIdleVerification even if its stored time elapsed.
For actual ready resume, use earlier valid wake/cadence deadline; no wake
for unrelated scripts. Running, waiting, disabled, terminal, suspended,
unknown outcomes/status have no predicted next occurrence. Missing stored
deadline maps to null. No extrapolation of future intervals after terminal
or re-derivation of scheduler cadence. Raw status is evidence, not execution
authority or proof the browser/agent is alive. No scripts, script paths,
URLs, aliases, raw errors/results, CLI executable/working directory or
arguments in compact response; use explicit allowlisted fields.

Legacy compatibility: keep the existing no-argument browser-list invocation
and its array shape as the explicit device-wide, no-filter historical
expansion for callers authorized for that existing route (document it as
legacy history, not recommended for routine status). Do not allow a scoped
query to fallback to it, and reject contradictory scoped/legacy arguments.
If a future MCP registration adds a mode argument, default legacy for
existing callers with no parameters, but advertise explicit project for
current queries and explicit legacy-history expansion; a breaking default
change requires coordinated consumer migration. Legacy history is not a
new scope grant and does not become available to project-only identities.
Do not change sibling generic list or Get shape, cancellation, STOP, wake,
store, scheduling, or execution policy. If legacy diagnostics need redaction,
coordinate a separate compatibility-reviewed change; no new scoped route
may reuse the legacy describe/error fields.

## Executable preflight and future route proof

Fixture links the real BrowserSchedules.fs and Common project, with only
the unused configPath helper stubbed to fail closed. It restores 53 synthetic
rows into a disposable owner-private file, with an injected fixed clock and
execution callback that must not run; never Start(), never use live storage
or browser. Baseline program calls the *actual* Service.List() and compares
its returned ID set with the required scoped current set of seven; expected
intentional nonzero red failure, not a compile failure. Contract program
uses the proposed pure filter/projection over the same raw fixture before
serialization, tests exact membership, ordering, history/actions, negative
scope, time, cancellation/wake/unknown state, no sensitive strings, and
existing full List producer for compatibility. Separate pure running/wake
cases avoid triggering real restart reconciliation in the fixture.

Future registered test (after shared route lease handoff): instantiate actual
registered MCP host/tool invoker with authenticated selected fake device,
registered Jarvis/Favn projects and an isolated seeded real Service;
assert typed query enters AgentCommandInfo.projectName/classifyProject with
canonical Jarvis, workspace:read and registered-project validation; spy on
producer serialization boundary (only matching raw rows described) and
wire JSON (seven exact IDs, fixed clock/source, no private sentinels).
Use invalid, unregistered, forbidden and lookalike project selectors,
missing/invalid mode and action/status combinations; assert no store read
for unauthorized input and no fallback to device-wide. Exercise explicit
scoped all-history, exact canceled, Any/CLI, no-argument and explicit legacy
history shape and sibling List/Get, unavailable device/BridgeDisabled,
unchanged store bytes, no scheduling side effect. Compare same input in
reverse order; test missing/unknown/terminal/recurring/idle/check-in cases.
This is a future real registration/dispatch test plan, not evidence that
the current route accepts any new arguments. Do not edit the three shared
route files or actual producer under this task.

## Terminal evidence

Red-first (before model was included in the executable): from the isolated
checkout, `dotnet run --project tests/ScheduledBrowserQueryPreflight20261127/ScheduledBrowserQueryPreflight.fsproj -c Release`.
Job ebf6e12978724762970530cad80aec54, ToolCompleted, ExitCode 134 (intentional
unhandled assertion failure), TimedOut false, Truncated false:
```
Actual Service.List rows: 53; expected scoped current browser rows: 7
Unexpected IDs: set
  ["command"; "completed"; "failed"; "favn-01"; "favn-02"; "favn-03"; "favn-04";
   "favn-05"; "favn-06"; ...]
Unhandled exception. System.Exception: RED: existing producer is unfiltered
```
This is an actual linked-source producer invocation and not a hypothetical
filter test. Baseline had already checked exact full fixture IDs, unchanged
file bytes and zero execution calls before its intentional scope assertion.

Final executable commands:
```sh
dotnet run --project tests/ScheduledBrowserQueryPreflight20261127/ScheduledBrowserQueryPreflight.fsproj -c Release
dotnet run --project tests/ScheduledBrowserQueryPreflight20261127/ScheduledBrowserQueryPreflight.fsproj -c Release --no-build -- --red
```
No-argument runs the proposed contract and compatibility witness, --red
reproduces the current producer's intentional scope failure. The first
contract compile detected a record field indentation error, corrected in
QueryModel.fs. Contract job 69cb714ead824de38109d501692afed5: ToolCompleted, ExitCode 0,
TimedOut false, Truncated false, empty stderr:
```
PASS: scoped contract, legacy actual producer, deterministic order, clocks, privacy, no dispatch/write (53 mixed rows)
```
Final --red rerun on the same built executable: ExitCode 134 (intentional
assertion failure), TimedOut false, Truncated false; 53 actual, seven
expected, same RED: existing producer is unfiltered assertion. No production
scheduler wiring or live execution was tested. The green result is solely
the proposed scoped model and actual legacy producer compatibility witness.
