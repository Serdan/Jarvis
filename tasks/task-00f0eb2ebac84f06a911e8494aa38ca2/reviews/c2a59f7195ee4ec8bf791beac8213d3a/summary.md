# Reviewer 2 independent re-review — ChangesRequested

- **Frozen commit:** `5afad770e0d7cd237d8c7d9f9d044674951a990f` (`feature/general-execution-scheduling`).
- **Base:** `b9b9e7a4b201b9baf581a644c1a7ca451f2943ba`.
- **Scope:** durable CLI/browser scheduling, cross-device `AfterAgentIdle` Manager safety, claim durability, replacement and retry behavior; no merge/deploy.

## Independent evidence

- Inspected source/transport/permissions and server persistence changes at the exact frozen commit, plus the canonical task specification and worker's evidence.
- Created an isolated reviewer worktree at that commit; the 16 existing `BrowserSchedulesTests` passed under .NET 11 (`11.0.100`) Release, job `6a9816dfb981436194fd385cef6d030e`.
- Added and ran a temporary deterministic negative case only in the reviewer worktree: replacing the schedule ID during A's pending remote verification caused an unauthorized B launch (`Expected: 0; But was: 1`, job `d9ed91215b1b454e989e2473b1ce5c02`). Restored the temporary file afterward; reviewer worktree clean.
- Worker reports full Client 699/699, focused 16/16, Server 131/131 and Release solution build passing. These positive tests do not cover the identified safety interleavings.

## Blocking findings

1. **P1 — Activity write failure fails open:** `findings/activity-write-fail-open.md`. An attributed tool executes after failed durable watermark persistence; in-memory poison is lost on server restart, permitting an old watermark to look authoritative.
2. **P1 — Replaced schedule consumes another agent's verification:** `findings/schedule-replacement-verification-race.md`. Fully reproduced with a deterministic failing test on the frozen commit.

## Additional finding

3. **P2 — Manager launch with no first tool call never recovers:** `findings/manager-first-activity-stall.md`. `waitingActivity` has no stale timeout after a successfully returned launch.

**Decision:** ChangesRequested. Fix both P1 safety gaps and add negative regressions before a new frozen review. Clarify or correct the stale Manager launch recovery. The work has not been merged or deployed.