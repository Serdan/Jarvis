# Reviewer 1 — global Agents dashboard F01/F02 re-review

**Verdict: Accepted (scoped to the requested F01/F02 corrections).** Review `68478fd9c709437f9fe4d5a58a5ee23c`, frozen source `02be332fd1a513a9d6e22a2d5ef9b271576c5ca1`, base `b277cd0d1b850b97ad81d889e2278aaef74e74ae`. Earlier Reviewer 3 ChangesRequested review: `7e607a9e31f74ced91ae55f91898b919`, frozen `87cccce757c57037cd91ec657eb64aa3ffe487e1`.

## Independent verification

- **F01 — same-ledger alias transport trust:** `Client/Modules/AgentDashboard.fs` now rediscoveres and re-inspects **all** locally claimed aliases in `inspectRosterMembers` before the global read, rather than selecting only `claim.ProjectName`. `readForRoster` passes the complete inspected group to `refreshGroup`, retaining its pre-fetch approved fetch/push URL equality gate (lines 370–379), post-fetch binding identity/transport checks and per-alias trusted-head ancestry checks (lines 380–405). A disagreement produces a failed refresh with no Fresh roster data. New tests exercise conflicting approved remotes via both the direct grouping path and the live two-alias discovery/refresh path, with unavailable/no-agent local projection; the valid-alias/global dedup tests remain present.
- **F02 — Parked visibility:** `Client/Modules/AgentView.fs` no longer unconditionally removes Parked rows in `visibleRows` (lines 326–339). State/name search and explicit `machine:` filtering still operate. Updated local and global tests cover Parked rows by default, direct state/name search, roles, pending notifications and machine filtering.
- **Provenance:** `git rev-parse HEAD` matched frozen SHA `02be332f...`; `git status --porcelain=v1 --branch` showed the branch and no uncommitted changes before review decision. No author source edits, merge or deployment by reviewer.

## Executed checks (Linux .NET 11 Release, exact frozen checkout)

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~GlobalRoster|FullyQualifiedName~Parked' --verbosity quiet`: **3/3 passed**, no skips; built test assembly.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --filter 'FullyQualifiedName~AgentDashboardCacheTests|FullyQualifiedName~AgentViewTests|FullyQualifiedName~GlobalRoster' --verbosity quiet`: **59/59 passed**, no skips.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~AgentRosterCacheTests' --verbosity quiet`: **9/9 passed**, no skips. Build emitted warnings, no errors.

## Evidence boundary

This accepts the two previously blocking defects, not an unfiltered full-solution test assertion or a live multi-device/network demonstration. Existing focused tests establish the correction paths; the complete unfiltered client/server test suites and production cutover were not run in this re-review. Server must remain unmodified/undeployed without operator authorization. No additional blocking F01/F02 issue identified.
