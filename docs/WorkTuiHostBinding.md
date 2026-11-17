# Read-only Work TUI host binding (WQ09)

The canonical Work dashboard is **not** a Scheduler or dispatch capability. The
existing `ConsoleTui.SetWorkAccess` provider is installed only when the local
JarvisClient process starts with **`JARVIS_WORK_TUI_READONLY=1`**. It is
absent by default. This setting does not create a Git binding, grant new
permissions, or enable Work claims, complete, source execution, retirement,
Scheduler launches, Agent creation, or cutover.

## Read path and provenance

On each Work view refresh (including `r`), the host enumerates real main
Git projects in the configured workspace. For each, it requires an existing,
explicitly enabled local `ledger-v2` binding. It reads the pinned WQ02
project state **at the one Git head** returned by
`LedgerBindingConfig.loadWithSnapshot`, which verifies the existing local
trust anchor, ancestry, and remote identity and advances the persisted
highest-trusted head through its normal short CAS lock. WQ04 role authority
is read at that *same snapshot*, then the existing WorkQueueToolFacade
`inspectAt` projection supplies Work IDs, stage/claim generations, graph,
gate/domain, dependency reasons, pinned role provenance, and claim/session
uncertainty. Review/integration and source-effect authority not independently
joined by that projection remain UNKNOWN rather than inferred. The existing
`WorkQueueDashboardReadBinding.toSnapshot` mapper preserves every DTO field;
unproven subject titles remain `None`.

The host compares physical binding identity before and after the pinned load
and requires the final trusted head to **equal** the displayed Work head.
Any missing/changed binding, duplicate canonical project ID, stale anchor,
remote failure, incomplete coverage, unproven role catalog, or read error is
rendered as a per-project **UNKNOWN / NotReady** row. The host does not keep a
last-known-good Work cache. The terminal clears previous rows while a fresh
read is pending and rejects late replies from superseded refreshes. Error
messages intentionally suppress local checkout paths, remote URLs, and
credential-bearing exception text; the status and project label are retained.

## Boundaries not yet proven

The local Git workspace inventory does not prove an authoritative,
account-wide complete project membership registry. The host therefore always
adds an **Account membership UNKNOWN** row, even when every listed project
is readable. Account capacity, cross-ledger admission/CAS, original browser
send/effect/terminal outcomes, source authorization, and genuinely live
WQ03 session liveness are not joined by the TUI. `Available` means
**NotReady (queue available)**, not permission to claim or launch.

No runnable or writable Work operation is exported from the adapter.
An operator must separately authorize every real Git ledger binding using the
existing binding process. Missing production WQ02/WQ04 data remains unknown;
the adapter does not migrate existing Agent tasks or invent Work rows.

## Local tests

`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter
"FullyQualifiedName~WorkQueueDashboardHostTests|FullyQualifiedName~WorkQueueDashboardReadBindingTests|FullyQualifiedName~WorkDashboardViewTests"`

Host fixtures cover opt-in default-off behavior, two projects, pinned stage and
dependency detail, read errors, local binding loss, duplicate identity, stale
trust heads, and TUI refresh without displaying old availability. Existing
Git-based fixtures test real SHA-1/SHA-256 refs and malformed remote heads.
Only local .NET 11 Release runs are used; no hosted Actions.
