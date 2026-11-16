# High — List truncation hides the required readiness warning

**Location:** `Client/Modules/WorkDashboardView.fs:225-228` and `ConsoleTui.fs:1458-1518` (frozen `681958aa9233d33ebec13d3b8ef8e15bbb1b3093`).

`displayLine` formats `project | WorkId | subject | queue/kind | status`, then truncates the *whole* result to the terminal width with `fit`. Real Work IDs and subjects can be long. On a typical 80-column terminal, a 40-character ID, 20-character subject, and nontrivial queue/kind consume the whole line **before the status field**. Thus `UNKNOWN / NotReady`, `Blocked`, and `Claimed; session UNKNOWN` disappear. An unbound/partial project produces an `unavailable` row, but with a long project name the same issue hides its warning; the per-project warning is not otherwise guaranteed at the top.

This violates the acceptance requirement for visibly fail-closed state on incomplete Git coverage and a useful Work-centric readiness list, even while row length and escape sanitization tests pass.

**Required correction:** reserve visible space for readiness/status near the start or use fixed-width columns with status before truncatable identity/subject fields; ensure an unbound/partial/unknown project keeps its warning in the visible part of the row. Add exact 40/80-column regression tests with realistic long Work IDs/project names/subjects asserting that `NotReady` or `UNKNOWN` remains visible.