# Independent frozen E0 SQLite Scheduler review — ChangesRequested

- **Review:** `d5b6c74798bc4d308fb4b4867c2b8620`
- **Frozen source:** `a8e8639b16596c2cbcbe93ee21e94f88496ff8af`; reviewed B/D base `0e9a8d4ea958090c94af11952329565358fdd79b`.
- **Scope:** Default-off E0 scheduler SQLite adapter only; no E1 importer/cutover, production host registration, browser launch or deployment.
- **Independent verification:** .NET 11 Release `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **355 passed, 0 failed, 0 skipped**; 11 E0-focused tests passed. The SQLite substrate provides atomic capacity CAS and command-hash replay protection. These suites do not exercise the failing paths below.

## Blocking findings

1. **P1 — `findings/claim-source-generation.md`:** Rebinding an already reserved launch to different primary Work leaves the launch record's original source SHA and stage generation intact. Source authorization/recovery provenance can diverge from the actual claimed Work.
2. **P1 — `findings/unsettled-specialist-credit.md`:** Existing-launch feasibility matching counts `Unknown`/`Prepared` effect stages as credits even though the claim path rejects them. A new reservation can strand a specialist while exhausting remaining global capacity.
3. **P1 — `findings/fresh-snapshot-replay.md`:** A valid retry of the *same* reservation/claim after refreshing persisted state is rejected before reaching the durable replay receipt; existing replay tests only reuse stale pre-commit snapshots.

## Required follow-up

Correct all three with focused negative regression tests and submit a new exact frozen source for independent review. Preserve default-off operation. E1 still requires the verified Work/C importer and explicit old-writer authority fence; this verdict neither approves a cutover nor invalidates reviewed B/D baseline work.
