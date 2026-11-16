# Independent review — WQ09 read-only task-first snapshot adapter

**Frozen source:** `698a359755499651a5f3214867e1b09972b56df4` (base `275dce20cd0d3b51bb8369d27cdb6f03a8942691`).

**Scope:** Inspected `Client/Modules/TaskFirstUiSnapshotAdapter.fs`, four two-clone Git fixture tests, project inclusion, `Program.fs` opt-in `tasks preview` route, and the existing projection/conversation read contracts. Submitter described 34 WQ09 tests passing; independently reran them.

**Independent validation:**
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~TaskFirstUiSnapshotAdapterTests --verbosity minimal`: exit 0; 4 passed, 0 failed, 0 skipped (7 s).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~TaskFirstUi --verbosity minimal`: exit 0; 34 passed, 0 failed, 0 skipped (4 s).
- Compiler emitted pre-existing warnings outside changed adapter/tests.

**Assessment:** Accept. The manual read-only CLI loads project, task, role, and operator conversation records at one verified project head. Separate account occupancy is explicitly marked an independent observation; no cross-ledger free-slot capacity grant is inferred. WQ02 unregistered/uncertain launch status is rendered without generating a session UUID. Missing WQ03 original-browser journal proof remains UNKNOWN. Missing, inconsistent, or unreadable sources produce warnings/unknown evidence instead of silently empty/zero. Two-clone fixture cases include blocked-first queue order, canonical task title, pending Role(Lead), hard dependency cycles, missing source, and unregistered unknown sends. No scheduler, mutation, dispatch, or posting entrypoint was introduced.

**Limitations (nonblocking, disclosed in UI):** No original-browser journal adapter and no project/account high-water admission join. The view does not claim these authorities.

No blocking findings.