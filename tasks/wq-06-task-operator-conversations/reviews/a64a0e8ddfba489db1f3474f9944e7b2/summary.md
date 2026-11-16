# WQ06 corrected frozen source — eb47d434

This is a follow-up to independent review `3b992a2446f14da8b7c02254cb62b849` (ChangesRequested). Four legacy WQ06 transport tests were newly included in the default Client test project despite incomplete canonical WQ04 producer and pinned Role-origin fixtures. `Client.Tests.fsproj` now compiles those tests only with `-p:RunWq06TransportFixtureTests=true`; the reason and restoration prerequisite are documented in `docs/specs/wq06-operator-conversations.md`. No production code, producer, source-effect, or admission guard changed.

## Executed .NET 11 Release validation

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueueOperatorConversationsTests --no-restore`: **5 passed, 0 failed**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkQueue --no-restore`: **118 passed, 0 failed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --no-build`: **not green**; command timed out at 180 seconds. Console reported `Jarvis activity persistence: agent-seen read failed (InvalidDataException / UnauthorizedAccessException)` and `append failed (UnauthorizedAccessException)`. This run does not establish any new WQ06 failure or success, and should not be counted as passing.
- A separate filtered Client WorkQueue run is in progress; report independently if it completes.

Independent reviewer: verify the default compile excludes the four known-red tests, the opt-in remains available, guards remain untouched, and the focused two-clone operator path remains green. Production Scheduler and automatic launch remain disabled.