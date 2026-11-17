# Reviewer 4 — independent frozen H1a read-only API/transport review

**Verdict:** Changes requested.  
**Frozen head:** `ce66e7a1344768086f06c590857603f93c0bd81e`  
**Base:** `e86e0133886f4f51b703a76167384071245e103c`  
**Review scope:** Library-only, unregistered authenticated Work SQLite read adapter and client transport/projection; NOT an H1b live route, mutable API, deployment, F/G acceptance or G5/G6 cutover.

Inspected all 11 changed files (933 additions) against the current canonical `work-stateful-api-ui` H1a specification and the stateful Work architecture. Confirmed the server uses a read-only connection and single SQLite read transaction, checks schema/application ID, project/account binding, role request and allowed project IDs, and does not register a host route, run the Scheduler or mutate Work. The client correctly keeps eligibility, approval, Lead priority, review/integration outcome, Backup and Recovery UNKNOWN and disables actions. Trusted grant construction and actual network/session authentication are intentionally deferred to gated host integration; this review **does not** approve those future pieces.

**Independent .NET 11 Release evidence on the exact frozen source:** `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkStatefulApiReadTests --no-restore` passed **4/4**; `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkStatefulTransportTests --no-restore` passed **3/3**. No full Server/Client suite was run by this reviewer. A pre-existing Client/Program.fs FS0760 style warning appeared in the client build; no test failure.

**Required corrections:**

1. [`findings/claim-generation.md`](findings/claim-generation.md): a claimed stage substitutes `StageGeneration` for the distinct `ClaimGeneration`, producing false claim-generation provenance (including treating zero claim generation as valid).
2. [`findings/source-provenance.md`](findings/source-provenance.md): `SourceCommit` is transported from SQLite to the server DTO but silently discarded in the H0 client view, contrary to H1a's explicit source-provenance preservation requirement. Preserve as *reported, non-attesting* metadata without turning SHA strings into review/integration acceptance proof.

Request a new exact frozen source review after corrections. The current reviewed head must not be integrated as accepted H1a.