# Reviewer 1 — independent frozen TUI terminal-clear-on-exit review

**Verdict: Accept.** Frozen source `93d9543f1691a5dbcdb69750dc540387b4e4957a` on `refs/heads/fix/terminal-clear-on-exit`, base/current main `f1a83ff2d7f5acf8ea8e38bafa6037ca8a323240`, review `f751391f971243ce9d2c257fb074bcc9`. Scope: remove painted Jarvis TUI from the **visible screen** when exiting, without destroying scrollback or leaving cursor/mouse/color modes altered.

## Independently verified source

- Exactly four files changed: `Client/Modules/TerminalMouse.fs`, `Client/Modules/ConsoleTui.fs`, `Client/Program.fs`, `Client.Tests/TerminalMouseTests.fs`. No server, protocol, Git ledger or agent-state mutation.
- `TerminalMouse.Tracking.stopSequence` first disables SGR mouse `CSI ?1006l` and button reporting `CSI ?1000l`, makes cursor visible `CSI ?25h`, resets SGR formatting/background `CSI 0m` and terminal default background via OSC 111, then emits **`CSI 2J`** to clear visible viewport and **`CSI H`** to home the next shell prompt. Crucially **does not emit `CSI 3J`**, which would erase scrollback. Original tracking start sets its default background and hides cursor; stop order restores modes before clearing.
- Moves `use tracking = Tracking.enable()` from the input loop to the enclosing `Program.runClient` task, starting before `RunInputLoop` and staying in scope until **after** SignalR `DisposeAsync`, activity-store disposal and input-loop completion. The SignalR `Closed` callback can redraw during shutdown; tracking no longer ends early. F# task `use` dispose remains in scope for normal quit, cancellation and exceptions.
- Existing no-op for redirected stdin/stdout or `TERM=dumb` preserved; its disposable catch prevents terminal-cleanup errors masking the original outcome. The feature does not claim universal Windows terminal VT support; the startup sequence already relied on those terminal capabilities. No new background control codes beyond standard ANSI/OSC already in use.
- `git diff --check f1a83ff2..93d9543f` exit 0; frozen worktree clean, exact committed SHA verified; `git merge-tree --write-tree main 93d9543f` exit 0, tree `a934c37bbc0d8caba36e4a4a58d70d72b01aa492`. No main merge/deployment by reviewer.

## Independent test evidence

- Frozen net11 targeted `TerminalMouseTests`: **11/11 passed**, exit 0, job `4604be40b4dc4a10bdd0c47098ade686`. Added `terminalExitRestoresModesAndClearsViewport` explicitly verifies restoration sequence and exact clear-home suffix and rejects `CSI 3J`.
- First independent **full net11 Client Release** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **581 passed, 1 failed (582 total)**, job `f1781a7fb8a54d62bb61a25db7bc1771`. Failure in *unchanged unrelated* `BoundedProcessTests.pipe_holding_descendant_is_deadline_bounded_and_reaped`, expected child StdOut text `parent exited` but observed empty while several other full suites were running concurrently. This is a known timing-sensitive process-harness assertion, not in the terminal mouse/Program code path.
- Reran that exact failing test **in isolation** with `--filter Name~pipe_holding_descendant_is_deadline_bounded_and_reaped`: **1/1 passed**, exit 0, job `d94cb8710e764a478ca82dd7951f6ce0`.
- Crucially, a second independent **full net11 Client Release run with other heavy suites finished**: **582/582 passed, 0 failures, exit 0**, job `351c3e991095415897835e99c6b422d8`, command `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build`. Nonfatal existing activity-persistence sidecar permission warnings appeared in both runs; they did not alter final test outcomes.
- Tests inspect escape sequence and lifetime in source; no claim to have captured or visually verified a real Linux and Windows interactive terminal session, especially under forcible SIGKILL where process finalizers cannot run. User-requested behavior is supported by source + deterministic unit suite. Operator can perform a post-deploy interactive confirmation.

## Decision

**Accept v3.** Full second Release Client run green, focused restoration/clear tests green; no blocking defect found. This verdict approves the exact frozen code for Lead's separate integration decision, not approval to merge, deploy, overwrite scrollback, or restart a live client on reviewer's behalf.
