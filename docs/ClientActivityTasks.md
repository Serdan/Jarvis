# Client Activity Feedback Tasks

## Goal

Make the local Jarvis client activity view compact enough to scan at a glance while still exposing what ChatGPT is doing, where it is doing it, how long it took, and whether it succeeded.

The primary activity format is one mutable row per command:

```text
20.47.52 @Jarvis ReadFileCommand(docs/AI.md) …
20.47.52 @Jarvis ReadFileCommand(docs/AI.md) (842 ms)
```

The receive timestamp remains fixed when the row is updated.

## Tasks

- [x] **1. Add command result summaries.**
  - Append compact result metadata inside the duration suffix where useful.
  - Examples: `exit 0`, commit hash, result/match count, patch hunk count.
  - Result-summary extraction must never turn a successful command into a failure.

- [x] **2. Represent command lifecycle in-place.**
  - New commands show a running marker.
  - Commands blocked on local confirmation show `[awaiting permission]`.
  - After permission is resolved, return the row to running state.
  - Completion updates the same row instead of appending a second row.

- [x] **3. Show connection state in the header.**
  - Show connecting, connected, reconnecting, disconnected, and closing states.
  - Include the configured Jarvis server host without adding connection events to normal command activity.

- [x] **4. Color project labels consistently.**
  - Apply a deterministic terminal color to `@Project`.
  - The same project must receive the same color for the life of the client.
  - Non-project/global commands remain uncolored.

- [x] **5. Keep failures concise while preserving full details.**
  - Activity rows show a short failure category/message.
  - Preserve the complete error text in client-side activity details.
  - Provide a lightweight way to inspect the most recent full failure without expanding every activity row.

- [x] **6. Add scrollable activity history.**
  - Retain up to 500 activity entries.
  - Continue showing a compact viewport by default.
  - Add Page Up / Page Down navigation and a way to return to the newest activity.
  - New activity should not unexpectedly destroy the user's position while viewing older history.

- [x] **7. Improve command-specific activity details.**
  - Files: path.
  - Process commands: executable plus a compact argument preview.
  - Project tasks: task name.
  - Search: query.
  - Git diff: optional path.
  - Git commit: compact commit message.
  - Jobs: executable or abbreviated job ID.
  - Multi-file reads: file count.
  - Sanitize newlines and bound detail length so a command cannot monopolize the row.

- [x] **8. Verify, publish, and leave the repository clean.**
  - Add focused formatting/result-summary tests.
  - Run the full solution build and client/server test suites.
  - Publish the Linux client artifact.
  - Commit coherent source changes and confirm a clean working tree.

- [x] **9. Add selectable activity details.**
  - Arrow keys select activity rows in the normal view.
  - Page Up / Page Down move the activity selection by one viewport and `End` returns to the latest activity.
  - `Enter` toggles an expanded detail pane for the selected activity.
  - Expanded details preserve full command-specific detail and the full RunCommand reason rather than reusing truncated row text.
  - Failures expose their retained full error in the selected activity pane.
  - `P` enters the permission view; permission arrows and A/S/E/D apply only there, and `Esc` returns to activity.
  - Permission requests remain visible from the activity view so waiting commands are discoverable without taking over navigation.
  - New activity follows the latest row only while the user is already at the latest activity; browsing older history remains stable.

- [x] **10. Add activity filtering.**
  - `/` edits a free-text activity filter and applies it live while typing.
  - Pressing `@` from the activity view enters filter editing with an `@` project term ready to type.
  - Terms prefixed with `@` match project names case-insensitively by substring; e.g. `@Loke` matches `Projekt Loke`.
  - Other terms match command name, activity text, full reason/detail, and retained failure text case-insensitively.
  - Multiple text/project terms are combined with AND semantics.
  - `S` cycles the predefined status filter: All, Running, Awaiting, Completed, Failed, Info.
  - `C` clears text/project and status filters.
  - Navigation, details, paging, and `End` operate on the filtered result set while preserving the underlying history.
  - Incoming non-matching activity does not disturb the filtered viewport.

- [x] **11. Copy recent command activity for chat recovery.**
  - `R` opens a compact numeric prompt for copying recent command activity.
  - Default to 20 commands; typing a digit replaces the default and additional digits extend it.
  - Ignore informational client log rows when counting commands.
  - Export oldest-to-newest with timestamp, project, command, full reason/detail, status, duration, and compact result/failure summary.
  - Do not include raw command stdout/stderr in the recovery transcript.
  - Use platform clipboard helpers: `clip.exe` on Windows, `pbcopy` on macOS, KDE Klipper over `qdbus6` first on Linux, then `wl-copy`/X11 fallbacks.
  - Report clipboard success or failure as client activity without counting that report as a command.

- [x] **12. Expose recent activity to fresh agent sessions.**
  - Add read-only `GetClientActivity` to the local protocol and public MCP surface.
  - Read directly from the connected client's in-memory 500-entry activity history; do not persist detailed activity on the server.
  - Default to 20 entries and bound requests to 1–100.
  - Support optional case-insensitive exact project filtering.
  - Return absolute start time, age, command/reason/detail, status, duration/result, and retained failure detail.
  - Do not log the inspection command itself as client activity.
  - Use `GetConnectionDiagnostics` separately when the client is disconnected or reconnecting.

## Design constraints

- Keep one command per activity row.
- Do not duplicate permission events in the activity list when the command row itself can communicate the state.
- Preserve culture-aware timestamp formatting.
- Preserve thread safety for overlapping commands and permission prompts.
- Failure/reporting UI must not alter command execution semantics.

## Verification

- `dotnet build Jarvis.slnx --no-restore`: 0 warnings, 0 errors.
- Client tests: 138/138 passed.
- Server tests: 28/28 passed.
- Linux x64 client published with the production server URL and installed into the ignored `artifacts/client/linux-x64` launch location.
