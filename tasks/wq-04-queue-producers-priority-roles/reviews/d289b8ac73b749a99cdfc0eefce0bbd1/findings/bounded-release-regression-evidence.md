# Additional independent .NET 11 Release regression evidence

**Reviewer:** Reviewer 2  
**Frozen WQ-04 source:** `6f0686720e56e198e085111cd8f22f5c6a6d7b83`  
**Checkout:** `reviewer2-wq04-producers-d289`, created from exact immutable commit. Git status clean.

- `dotnet --version`: `11.0.100`.
- `dotnet restore Server.Tests/Server.Tests.fsproj --ignore-failed-sources`: successful local restoration.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerWorkQueueProducerTests --logger console;verbosity=normal`: **9/9 passed**.
- `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources`: successful local restoration.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueProducerGitTests --logger console;verbosity=normal`: **3/3 passed**; disposable two-clone Git fixtures.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~WorkQueue --logger console;verbosity=minimal`: **97/97 passed**.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter FullyQualifiedName~WorkQueue --logger console;verbosity=minimal`: **28/28 passed**.

The broader test counts include the focused tests; do not sum them as distinct. No GitHub Actions, main push, Scheduler activation, migration, deployment or Codex. All passing regression evidence is necessary but insufficient to certify source provenance/high-water and versioned role admission. The source-level blocking findings remain unchanged.

**Disposition recommendation:** ChangesRequested for exact frozen WQ-04 source.