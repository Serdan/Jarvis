# P1 — Three browser tools fail in the mandatory TUI activity formatter before dispatch

Frozen source: `66d4e06566c3df5e8b771875f421815e19bc6770`.

`Common/Messages/AgentCommand.fs:997–1063` implements `AgentCommandInfo.detail` as a nonexhaustive match and omits **BrowserListWindowsCommand, BrowserCloseTabCommand, and BrowserRunInNewTabCommand**. The Release build emits **FS0025** for this exact function (BrowserCloseTabCommand shown as an unmatched case).

`AgentCommandInfo.invocation` (line 1088) always evaluates `detail command`. `ConsoleTui.StartActivity` (Client/Modules/ConsoleTui.fs:1236–1245) invokes `invocation`, and `Client.SignalR.Client.receiveCommand` (lines 463–470) calls `StartActivity(command)` **before permission authorization or command dispatch** for every command except GetClientActivity/Message. Therefore these three new public tools throw `MatchFailureException` before the extension request is sent, even when all permissions and registrations are valid. The high-level `BrowserRunInNewTab` flow and `BrowserCloseTab` cleanup are effectively nonfunctional.

**Fix:** Make the activity formatter exhaustive for every Browser* command (explicit labels for all three missing cases), and add a parameterized regression that invokes `AgentCommandInfo.invocation` and/or the receiveCommand path for all registered browser commands; treat FS0025 as a failing warning for the browser command module. Re-run Release tests and a real bridge lifecycle after correction.

**Severity: P1** — core requested browser lifecycle paths fail before execution, invisible to existing browser tests.
