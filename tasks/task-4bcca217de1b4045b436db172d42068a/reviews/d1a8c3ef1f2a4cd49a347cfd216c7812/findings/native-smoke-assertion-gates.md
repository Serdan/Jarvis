# Finding — native pipe-holder smoke can report a false PASS (P2)

**Frozen source:** `49e3c6cd9bda7094db6fb03b0ba045a7a0095ed2` (direct parent `aa2e46ff2927b8e9fd0af25204377c299f2ec576`), `native-windows-bounded-process-smoke.fsx:128-164`.

The `descendant_held_both_pipes_then_timeout` test is intended to prove that a descendant retaining BOTH inherited outer stdout/stderr pipes forces bounded deadline termination and is reaped. But lines 156-159 accept either `TimedOut=true, ExitCode=-1` **or** `TimedOut=false, ExitCode=0`. Lines 160-163 only print `parent_marker`, `stdout_child`, and `stderr_child` booleans; none are asserted. Consequently, if the fixture stops inheriting the pipes or forwarding either stream, the case can print PASS after an ordinary zero exit and still be counted as proof of the negative inherited-pipe path.

**Required correction:** For this named negative control, require the timeout and -1 exit, the parent marker, both descendant stream markers, child PID creation and bounded descendant reaping. If no-pipe normal success is a supported alternative, model it as a *different* named case with its own explicit assertions. Keep the normal-parent-exit separately-redirected descendant case distinct, since it intentionally exposes the Windows timeout release blocker.

# Finding — smoke-suite pass count is inaccurate (P3)

`native-windows-bounded-process-smoke.fsx` defines **13** `runCase` invocations (lines 53-206) but prints `W23_SMOKE_SUITE_PASS checks=12` unconditionally at line 248. It also prints that total when the optional substring filter selects only one case. Count actually executed cases dynamically and distinguish full vs filtered suites; do not present an incorrect fixed pass count.

# Review boundaries

This is a **QA evidence-only** submission: the exact diff from `aa2e46f` to `49e3c6c` adds only `docs/qa/native-windows-bounded-process-smoke-2026-11-17.md` and the F# smoke file. No production files changed. The report records a Windows release blocker, not Windows GO; the Windows NUnit suite is intentionally blocked by `Client.Tests/HermeticHarnessTests.fs:85-90`, and macOS is untested. Source inspection confirms `runInternalGroupHost` waits for immediate child exit and then both forwarder tasks (`Client/Modules/BoundedProcess.fs:192-196` at the pinned baseline). The reported Windows process observations were **not independently re-executed** in this reviewer turn: the selected connected client was Linux and the Windows client project path was unavailable. All findings above follow from exact frozen Git source inspection, not native runtime claims.

**Disposition:** ChangesRequested narrowly for QA harness and evidence precision. Maintain Windows and macOS release HOLD. This verdict does not request implementation/integration/cutover or suggest removing the hermetic test guard.