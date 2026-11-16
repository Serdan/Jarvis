# WQ06 corrected frozen source — eb47d434

Follow-up to independent review `3b992a2446f14da8b7c02254cb62b849` (ChangesRequested). Four legacy WQ06 transport tests were newly included in the default Client suite despite incomplete canonical WQ04 producer and pinned Role-origin fixture evidence. `Client.Tests.fsproj` now compiles them only with `-p:RunWq06TransportFixtureTests=true`; the reason and restoration prerequisite are documented in `docs/specs/wq06-operator-conversations.md`. No production code, producer, source-effect, or admission guard changed.

## Executed .NET 11 Release validation

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueOperatorConversationsTests --no-restore`: **5 passed, 0 failed** (two disposable Git clones, CAS contention, original replies, recovery and audit repair).
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue --no-restore --no-build`: **72 passed, 0 failed** (1m12s).
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue --no-restore`: **118 passed, 0 failed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --no-build`: **not green**; timed out after 180 seconds. Output contained `Jarvis activity persistence: agent-seen read failed (InvalidDataException / UnauthorizedAccessException)` and `append failed (UnauthorizedAccessException)`. Not evidence that WQ06 production semantics failed; not a passing run either.

Independent reviewer: confirm the default project excludes the four known-red dependency-gated transport fixtures, the explicit opt-in remains available, guards remain unchanged, and the focused two-clone manual operator path is correct. Production Scheduler and automatic launching remain disabled.