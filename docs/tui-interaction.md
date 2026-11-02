# Jarvis terminal interface and mouse input

The TUI continues to work entirely with the keyboard. When stdin/stdout are interactive and the terminal is not `dumb`, Jarvis requests xterm SGR mouse reports (`1000` for click/wheel and `1006` for coordinates), restoring both modes when its input loop ends. On terminals without SGR mouse reporting, keyboard behavior is unchanged.

## Navigation

The Activity, Agents, Jobs and Settings headings are clickable. List rows can be selected by clicking. A second click on the same Activity or Job row opens its details; for agents, a double-click expands details on narrow terminals. The mouse wheel moves through the current list in three-row steps. On the job output screen it scrolls the output, preserving the existing follow mode. Settings fields can be selected with a click and edited with a double-click. Pending permission rows can be selected by clicking, but **a click never grants or denies a permission**. Approval remains an explicit keyboard command.

The Agents screen prioritizes Ready, Needs work, Working and Parked, while keeping project/name ordering within each group. Rows include the current task title and elapsed time in that status, with a responsive detail pane on wide terminals. The elapsed time measures the persisted **logical state**, not an active ChatGPT/browser session. Snapshot refresh times are separate. No new orchestration actions are triggered by UI navigation.

Mouse clicks are decoded only from complete, well-formed SGR reports, and mapped against the hit regions registered during the last rendered frame. Invalid, non-left-button, release, and mouse-move reports are ignored. Escape remains a working keyboard key (a short decode grace period allows distinguishing it from an ANSI report). Modal edits/confirmations cannot be dismissed inadvertently by clicking another tab.

Mouse support depends on terminal capabilities (particularly on Windows). The automatic `1000/1006` setup has been unit-tested with simulated SGR reports but not end-to-end validated against every terminal emulator. Most terminals allow selecting text while tracking is enabled by holding Shift while dragging. There is no need for a native desktop window, mouse driver or external TUI framework.

## Buffered rendering and resize behavior

The TUI now renders into a bounded terminal-cell frame rather than calling `Console.Clear()` and writing directly during each update. The renderer compares consecutive frames and writes only changed spans; a full clear occurs only when a new size requires a fresh frame. Each frame clips writes to the terminal bounds (one physical column is reserved to avoid bottom-right auto-scroll), preserving the click coordinates recorded during drawing. A failed presentation clears the active hit map and triggers a fresh frame on the next input-loop iteration.

On short terminals the header becomes compact, list page sizes shrink with the available height, and job details suppress secondary metadata so output remains accessible. Permission requests have their own paged screen. Clicking a visible request on Activity or Agents navigates to the permission screen and selects it; clicking **never** approves or denies anything. The normal keyboard shortcuts remain available.

Pure frame clipping/color/Unicode tests and a pseudo-terminal frame-presentation check cover the underlying renderer. Interactive terminal-emulator testing is still advised after installing a new client; nothing in this change deploys or updates a running client.

## Automatic page sizes

Activity, Agents, and Jobs now use the available terminal height rather than a fixed 12-row limit. Page Up and Page Down move by the **current rendered page size**, including on the Agents screen. Job-output paging likewise uses the visible number of output rows instead of a fixed step. Resizing the terminal recalculates each page size; open details and diagnostic panels reserve their rendered space. Ordinary Activity/Agents pages keep only a compact, clickable permission preview when requests are pending, with the full request details in the dedicated permission screen. No page-size configuration is needed.
