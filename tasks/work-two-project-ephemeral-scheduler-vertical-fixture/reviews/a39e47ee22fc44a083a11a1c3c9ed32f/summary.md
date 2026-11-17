# Independent frozen review — ChangesRequested

**Task:** `work-two-project-ephemeral-scheduler-vertical-fixture`  
**Review:** `a39e47ee22fc44a083a11a1c3c9ed32f` (claimed by Reviewer 1, seq 5527)  
**Exact source:** `be31eb9e749948a2dea2e04b80f1c03b6828421a`  
**Submitted base:** `bed92f3d2b39ef7c256a8d1955c48883af99cb60`

## Independent verification

The six-file diff consists of the 570-line new vertical test, one added project compile include, one adjacent planner regression test, two targeted production changes to `WorkQueueCrossProjectPlanner.fs` and `WorkQueueNativeLifecycleCas.fs`, and one scoped documentation file. `git diff --check` passes. A separate registered review worktree remains clean with exact `HEAD=be31eb9e749948a2dea2e04b80f1c03b6828421a` after tests.

All completed tests were run independently using local **.NET 11 Release**, `Client.Tests.dll (net11.0)`, and passed:
- New `WorkQueueTwoProjectEphemeralSchedulerVerticalTests`: **3/3**.
- WQ08 planner / Scheduler host, head binding, reactive wake: **57/57**.
- WQ11 native credential, lifecycle CAS, durable original intent: **13/13**.
- WQ07 original account capacity/admission: **27/27**.
- WQ04 role authority and Integration source authority: **13/13**.
- **Total independently completed: 113 passed, 0 failed, 0 skipped.**
- The initial broader 11-suite adjacent filter timed out after 180 seconds without a test summary; it is *not* counted as a pass. The narrower suites above completed.

## Verified boundaries

The test does use separate original disposable Git remotes for two projects and one account, two clones of each, deterministic WQ12 selection, original WQ07 cap-one token and WQ02 four-path credential CAS, WQ03 one-shot fake provider, native first-connect/claim/checkpoint/terminal, verified WQ07 release, STOP, and unknown-send/no-resend. The canonical account stays `LegacyActive`; the positive Scheduler `Activated` value is explicitly an in-memory test projection, not persisted or a production bridge. `prepareProduction` remains `HostBridgeUnavailable`. The review/Integration stages are inert, correctly refused as accepted WQ05 authority; missing causal WQ05/provider/migration interfaces are documented as production NO-GO. No live Scheduler, production host, cutover, hosted Actions, or Codex was used.

## Blocking acceptance-evidence gaps

1. **Competing WQ07 account-CAS race is absent.** `Client.Tests/WorkQueueTwoProjectEphemeralSchedulerVerticalTests.fs:391-410` only tests the other project *after* the first original already holds capacity; the stale clone test at 440-450 is a WQ02 same-launch Claim, not competing WQ07 admission. See `findings/competing-wq07-cas-not-proven.md`.
2. **Historical Agent/review preservation is vacuous.** The fixture starts with no historic Agent or review records and only checks `agents/*` remains empty (lines 56-118, 529-534). Seed representative original records and compare immutable blob OIDs before/after, or return precise typed NO-GO. See `findings/historical-record-preservation-vacuous.md`.
3. **Stale-clone race negative accepts any failure.** Lines 444-447 pass on `Error _` instead of proving the original version-CAS conflict; a preflight failure could satisfy the test. Assert exact conflict and absence of extra writes. See `findings/stale-clone-error-unclassified.md`.

**Verdict: ChangesRequested.** The green tests demonstrate useful default-off disposable composition, but do not yet establish all task-bound two-clone and historical-preservation acceptance claims. Do not infer production Scheduler/cutover GO.