# P1 — Worktree-qualified source aliases bypass recipient sender block

**Frozen source:** `49fb15a3348c07be0babdc3785df19efbf167728`, task `task-46760acd0cc94361b4fba96cbea6349c`, review `648f9423bc6e4cc4a343c27c9632a3f6`.

## Confirmed source path

- `Client/Modules/ProjectPaths.fs:24–47` parses both `Jarvis` and `Jarvis::some-registered-worktree` as **the same canonical project name and ProjectId**, with checkout selection separate. `ProjectTarget.identity "Jarvis::review-worktree"` returns `"Jarvis"`.
- `Server/McpTools.fs:1572–1578` `NotifyAgent` decides cross-project routing by **raw string** `toProjectName <> projectName` and forwards the raw `projectName` in `TaskWriteCommand.SourceRef`. `Server/McpTools.fs:764–794` source-validation path accepts that selector to route/verify the sender; it does not rewrite the sender project into canonical identity before forwarding to the destination.
- `Client/Modules/ReviewWorkflow.fs` `setNotificationSenderBlock` stores `BlockedSenders.FromProjectName` based on the literal `senderProjectName` (possibly normalized to `""` for a local sender), while `notifyAgent` compares `item.FromProjectName = (if crossProject then fromProjectName else "")`. It compares **exact raw strings**, not project identity. Thus a block configured for `Jarvis/Owner` does not match `Jarvis::worktree/Owner`, despite identical authenticated source ledger and actor. Similarly a local block using `""` can be bypassed when a sender uses a worktree selector and explicit destination main selector, causing false cross-project classification.

## Independent negative reproduction

Added **only** to reviewer-owned disposable detached exact-SHA checkout `/tmp/jarvis-reviewer1-block-alias-49fb`, leaving Worker 5's branch unmodified: `Client.Tests/ReviewWorkflowTests.fs` test `ProjectWorktreeSelectorMustNotBypassRecipientSenderBlock`. It first asserts the source selector `Jarvis::review-worktree` has canonical `ProjectTarget.identity = "Jarvis"`; recipient `Reviewer` in destination `Wayfold` stores a block on source project `Jarvis` / agent `Owner` with autoreply. Then the destination-ledger `notifyAgent` is called with the **same logical project but a worktree-qualified source selector**.

**Observed:** `NotificationDelivery.Status = "Delivered"`, expected `"Blocked"`, NUnit failed, exit **1**, job `afcc58e974554a198f2a0089d39bccb2`. The test exercises the actual canonical project selector parser, recipient-policy writer and destination notification transaction; it **models** the previously verified source-routing precondition rather than spinning up a real cross-project Server+SignalR connection. The public routing path is established by source inspection. The fixture's illustrative worktree suffix would need to correspond to an actually registered worktree in production; registered ones exist, and the behavior is independent of the suffix spelling.

**Impact:** recipient's block is unenforced for a fully legitimate source-project alias; durable inbox notification is written and marked Delivered. This is not only a display-label discrepancy: it violates the central P1 invariant of recipient-owned blocking and is especially likely where agents operate from registered worktrees.

## Required correction

Canonicalize the *verified* source project identity at the routing boundary, distinct from checkout selector or human-readable display name. Store and compare stable project-qualified sender identity in recipient preferences and notification decisions; preserve a human-readable source label separately if needed. Do not silently strip arbitrary malformed strings before source verification. If local and worktree aliases address the same canonical source/destination ledger, classify them consistently. Add end-to-end same-project/worktree and cross-project tests (main selector, registered worktree selector, exact actor, unrelated actor, retries and autoreply) showing identical Blocked results and **zero** inbox writes for every alias, plus normal delivery for another canonical project. Keep immutable suppression receipt/replay semantics and fail closed on unresolvable identities.

**Severity: P1**, blocking the notification-blocking feature review.
