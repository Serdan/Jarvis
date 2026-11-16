# Medium — Terminal integration acceptance cases are untested

**Location:** `Client.Tests/WorkDashboardViewTests.fs` (all 7 new tests) and new `ConsoleTui.fs` WorkMode integration.

The seven new tests exercise pure `WorkDashboardView` projection/filter/selection/fit only. No new fixture drives the actual `ConsoleTui` mode transition, `SetWorkAccess`/background `Load`, polling, WorkFilterMode key events, or live refresh sequence. Existing adjacent AgentView/TerminalLayout/TerminalMouse/TerminalFrame tests (54 passing) do not exercise these new branches. Yet the task expressly requires terminal fixture coverage for mode switching, concurrent snapshot updates, and legacy Agents isolation.

In particular, there is no automated proof that refreshing a bound source clears previously displayed ready rows while loading, that overlapping/set-new-provider loads cannot repaint stale results, or that switching W/A and filter Esc preserves the existing Agents shortcuts.

**Required correction:** add a small injectable terminal fixture/harness (without binding a live ledger) which exercises WorkMode and AgentsMode switching, keyboard selection/filter/navigation on narrow widths, background Load success/error/late completion sequencing, and unchanged Agents mode. Keep the pure projection tests. Run Release .NET11 hermetic focused + adjacent suites.