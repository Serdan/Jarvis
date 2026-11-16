# WQ08: inert cross-project host preview

The `scheduler preview-global` command is an **operator-invoked, default-off,
read-only** comparison of approved account and project Git authorities. It is
not a Scheduler service or a way to start an agent. It never reserves account
capacity, performs a project CAS, mints an original browser operation ID,
submits a browser launch, starts a timer, or changes deployment state.

## Invocation (.NET 11)

```sh
dotnet run --project Client/Client.fsproj -c Release -- scheduler preview-global \
  /absolute/path/account-binding.json \
  /absolute/path/approved-project-manifest.json
```

The account binding uses the same schema as the existing one-project
`scheduler preview` command, with `RepositoryPath`, `RemoteName`,
`ApprovedFetchUrl`, `ApprovedPushUrl`, `AccountId`, `LedgerId`, and
`Epoch`. The host's account fetch is a read. Project roots must already have
separately approved, enabled Jarvis V2 trust bindings. No V1 fallback or
implicit binding enrollment is attempted.

A manifest lists **every approved project** in the account's comparison set:

```json
{
  "CompleteProjectSet": true,
  "Projects": [
    {
      "ProjectRoot": "/absolute/approved/project-one",
      "OriginalJournalRoot": "/absolute/existing/wq03-journal-one"
    },
    {
      "ProjectRoot": "/absolute/approved/project-two",
      "OriginalJournalRoot": "/absolute/existing/wq03-journal-two"
    }
  ]
}
```

`CompleteProjectSet` is an explicit *operator assertion*, not evidence that
Jarvis discovered every account project. Set it to `false` when the roster
is partial or uncertain. Any missing project, conflicting pinned head, role
catalog gap, missing source lineage, or outstanding token owned by an omitted
project prevents a global winner. Individual successful projects remain
inspectable alongside diagnostics for the failed ones.

`OriginalJournalRoot` is optional; a supplied root must point to an already
provisioned, anchored WQ03 client-local journal. A missing or invalid journal
remains `Unknown` or `NotRecorded`, **never** `DefinitiveNoEffect`. A read
does not initialize, replace, or repair journal state.

## Reading the output

- `Projects` contains each approved project's pinned Git head, WQ04 catalog
  revision and observed launch/browser status. The original operation string
  stays visible, without provider session keys.
- `SimulatedWinner` is one global blocking-first, then FIFO demand projection
  from real pinned WQ04 role selections, with source lineage and work
  generation. It is **not** a dispatch grant. The model selects at most one
  currently chosen demand per current enabled non-Manager role.
- `Planner` is the independently accepted WQ08 validation report, fed those
  pinned observations. Prospective WQ04 demand does not contain a durable
  pre-send launch/browser intent; those identities are intentionally absent.
  `NoDurablePreSendIntent` and a non-`WouldLaunch` planner decision are
  therefore expected even when `SimulatedWinner` exists.
- `AccountHead`, `MaxConcurrentAgents`, `OccupiedAgents`, and `AvailableSlots`
  expose the account-wide authority; a full cap is diagnosed explicitly,
  but capacity is never changed.
- `CanDispatch` is always `false`. Inconsistent source/project/role or
  unknown original-send evidence must be reconciled at the original
  authorities before any separate, reviewed future dispatch implementation.

Existing `scheduler preview` remains the original one-project diagnostic.
Neither command enables Scheduler timers or production browser submissions.
