# Reviewer 4 — independent H1a recheck

**Verdict: Accepted for H1a read-only source only.** Frozen head `2d37c72a78b4fa024427ac578a3844d08f2085cb`; base `e86e0133886f4f51b703a76167384071245e103c`. Rechecks the prior ChangesRequested review `e4e29c7a34d14fb2b27c0f312c7e7a15` on `ce66e7a1344768086f06c590857603f93c0bd81e`.

Inspected both correction files and the unregistered SQLite read adapter and client transport at the frozen head. Both required corrections are present:

1. Claimed stages require a nonempty launch and positive, int-representable `ClaimGeneration`; the projected claim generation is now the actual `row.ClaimGeneration`, distinct from `row.StageGeneration`. Unverifiable claim rows are omitted with explicit diagnostics. The new tests cover differing generations, zero, negative, overflow, and absent launch. The model still does not attest liveness.
2. `ReadProjection.StageSources` preserves the recorded SHA-40/64 by project and stage as typed `ReportedUnverified` evidence. Missing or malformed SHA is `Unknown`, with a malformed-SHA diagnostic. A SHA does not imply verified review or integration. New tests exercise all of these outcomes.

**Independent .NET 11 Release results on clean exact-HEAD worktree:** `git diff --check e86e013..2d37c72` PASS; `dotnet build Server.Tests/Server.Tests.fsproj --configuration Release` PASS (0 errors, 10 unrelated warnings); `dotnet vstest Server.Tests/bin/Release/net11.0/Server.Tests.dll --TestCaseFilter:FullyQualifiedName~WorkStatefulApiReadTests` **4/4 PASS**; `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkStatefulTransportTests --no-restore` **6/6 PASS** (unrelated pre-existing warnings). No full suite claimed.

**Scope limitation:** This accepts only H1a's library-only read adapter and future transport contract. It does not approve future trust-grant construction, network/session authentication, production route registration, mutating H1b commands, E1/F1/G1 gates, scheduler launch, deployment, integration or cutover. Lead must separately integrate this exact frozen source. No new H1a blocker found.