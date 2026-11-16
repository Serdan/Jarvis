# Independent review — WQ03 original browser one-shot journal

**Frozen source:** `ffef2b1e11ff1c4ec3e396c3b6041246d3136648` (base `275dce20cd0d3b51bb8369d27cdb6f03a8942691`).

**Scope inspected:** `Client/Modules/OriginalBrowserJournal.fs`, `Client/Modules/WorkQueueSchedulerHostPreview.fs` diff, journal tests and operational documentation. Examined SQLite transaction/anchor ordering, identity hashes and lookups, conflict behavior, immutable outcomes, corruption/rollback fences, and the WQ08 preview's treatment of missing original records.

**Independent validation:** .NET 11 Release:
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~OriginalBrowserJournal --verbosity minimal`: exit 0, 7 passed, 0 failed, 0 skipped (includes child-process marker/receipt continuity checks).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~WorkQueueSchedulerHostPreview --verbosity minimal`: exit 0, 12 passed, 0 failed, 0 skipped.
- Existing unrelated compiler warnings do not arise from changed journal code.

**Assessment:** Accept. Before a caller can invoke an injected provider, `tryMarkOriginal` writes exact original operation ID, launch/project/work/role/provenance, derived UUID, and hash, appends/fsyncs an independent marker, commits SQLite synchronously, and returns Marked only to the first writer. Repeated matching intent returns Existing; conflicting ID/launch/intents fail closed. Unresolved marker and Unknown outcome cannot be interpreted as no-effect or authorize a replay. The immutable-outcome API prevents silently changing uncertain or accepted results. Missing/corrupt journal and deleted/rolled-back records fail closed, with explicit recovery guidance. WQ08 binding is opt-in via an opened store; existing CLI and realReadPorts remain unbound and CanDispatch=false. WQ08 preview flags missing original as a blocking issue, never proof of no effect. No live browser send or Scheduler activation is added.

**Operational limitation (nonblocking):** Enrollment remains an explicit trusted-host integration step; there is intentionally no automatic repair of an incomplete store. Restore/rollback of *both* anchor and SQLite history needs external reconciliation, as documented. This work does not claim a cross-machine original-send authority.

No blocking findings.