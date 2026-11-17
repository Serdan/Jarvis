# Independent frozen E0 SQLite Scheduler review — ChangesRequested

- **Review:** `d5b6c74798bc4d308fb4b4867c2b8620`
- **Frozen source:** `a8e8639b16596c2cbcbe93ee21e94f88496ff8af` on reviewed B/D baseline `0e9a8d4ea958090c94af11952329565358fdd79b`.
- **Scope:** E0 default-off scheduler domain and SQLite transaction adapter; no live host, importer/C cutover, browser launch, deploy or integration authorization.
- **Independent verification:** .NET 11 Release `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **355 passed, 0 failed, 0 skipped**; 11 focused E0 tests passed separately. Base SQLite substrate fingerprints replay commands, guards source/generation and performs atomic account-capacity writes. These positives do not cover the two mismatches below.

## Blocking findings

1. **P1, `findings/claim-source-generation.md`** — `claim` may deliberately choose different Work from the one credited at `reserve` but retains the launch row's reserved `SourceSha` and `StageGeneration`. Durable claimed Work identity and pinned provenance can disagree; no claim regression with differing SHAs/generations.
2. **P1, `findings/unsettled-specialist-credit.md`** — Injective existing-launch credit matching counts `Prepared`/`Unknown` external-effect stages, but actual new reservation and claim filter these out. False feasibility can consume the remaining global slot and strand an existing specialist reservation.

## Required follow-up

Fix both at the frozen E0 level with negative regression tests and a new exact frozen review. E1 still depends on the independently verified Work/C importer/old-writer fence. Passing 355/355 tests and the default-off boundary are positive evidence, not sufficient acceptance of these safety/admission invariants.
