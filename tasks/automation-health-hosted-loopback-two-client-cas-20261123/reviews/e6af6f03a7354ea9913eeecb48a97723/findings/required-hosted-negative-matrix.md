# P2 — Required hosted Health negative matrix is incomplete

**Frozen source:** `d8ebf0025f11873a9a0f58aedaa7aaf541cb7187` (base `dcac884fc061869572b12a03befc12f11c009f2f`).
**Original obligation:** task spec `e5c8d455b90d5eb58ea5f22971f669cdce7bb989`, RED-first minimum proof point 4: “missing/invalid SQLite and restart coverage; … 600s idle boundary … future/reversed activity, changed version/generation/source, and concurrent CAS loser.” This is a test-only contract. No production defect has been established.

## Observations

- `tests/AutomationHealthHosted20261123/HostedTests.fs` builds and starts one `WebApplication` and stops it only in the final cleanup. No host/store re-instantiation against retained SQLite, and no fresh-process coverage reset check, is exercised. The source document explicitly acknowledges “actual hosted process restart” is not covered.
- The only native check with `InactivitySeconds = 600` asserts fresh-activity HOLD; the positive uses an expressly test-only 1-second timeout plus 1250-ms delay. Thus the required exact 600-second idle threshold is not tested at its boundary in the hosted composition. The document points instead to pre-existing source-local/pure-snapshot `Client.Tests/NativeHealthTests.fs`. Do **not** interpret the 1-second positive as proof that 600 seconds elapsed or require a real 600-second wall-clock run.
- The test mutates a future SQLite timestamp and deletes the row after positive recovery, and proves a stale independent `recoverAgentAssignmentWithTaskVersionAs` loses. It does not exercise a *changed source/version/generation between `Enumerate` and `Current`*, nor reversed server time between the two actual `Activity` calls. Those are guards of `NativeHealth.checkOnce` and are demanded by the original negative matrix.
- The foreign-user read is attempted **before** Alice's qualifying MCP ingress creates an activity row. It therefore checks absence of Bob's row but does not independently prove isolation once the Alice row exists. This is a modest coverage gap, not a demonstrated authorization bug.

## Impact and smallest remedy

The frozen test legitimately proves a registered authenticated two-client /client SignalR path, qualifying real /mcp ingress, current-process SQLite GetAgentActivity snapshots, concurrent two-clone canonical Git recovery, and a failing foreign-project mutant. **The narrower positive stands.** The complete negative-matrix requirement cannot yet be marked satisfied.

Repair, if feasible, **only** inside the original new `tests/AutomationHealthHosted20261123/**` and documentation lease. Add bounded test-owned hosted restart/coverage-reset verification without restarting a production OS or Jarvis service; add source-bound controlled time/row or a separately justified offline test to prove the precise 600-second edge without sleeping 600 seconds; exercise changed candidate/source/generation before the native recheck and reversed activity/clock, using genuine Git/host paths where feasible. Repeat focused .NET11 Release and negative mutant, retain immutable TRX. If any source API prevents honest coverage, checkpoint the precise hard blocker for independent adjudication instead of asserting the untested case. No production source edits, main, push, live effects, schedules, or cutover.

## Independent checks

- Scope: `git diff --name-status dcac884f d8ebf002` shows exactly **four added** paths: fixture fsproj, GitFixture.fs, HostedTests.fs, documentation.
- Source hashes match document: fsproj `0693388b...`, HostedTests `1970c5bb...`, GitFixture `60572ac0...`.
- Independent .NET11 Release filtered test job `2c52d2e0bf0b4686864fdc3c50b5a7a7`: **1 passed, 0 failed, 0 skipped, exit 0**; `reviewer3-health-hosted-green.trx` SHA256 `7204d47bab7a17d94bd0e3e025225833aa7c5035fe5cb3c8644a11f61960f208`.
- Independent mutation job `dd7c87e3d36d459291b6969093288cbf`: **1 failed, exit 1**, live Hub `ActivityProofUnavailable`; `reviewer3-health-hosted-mutant-red.trx` SHA256 `cc5c62a0ba464061d8775dccd6a03cf36f983cf878b4492e9efec2924d801685`.
- Submitted worker final green TRX SHA256 `f0341fd2...` also matches the source document; the independent rerun is the controlling verification.
