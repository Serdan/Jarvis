Durable CLI/browser once/every/idle scheduling, authenticated cross-device Manager activity hints, no-replay claims, OAuth permissions, API/list/get/cancel and migration. Release tests: Server.Tests 129/129 passed, Client BrowserSchedules 13/13 passed; full Client.Tests timed out at 180s without reported assertion failure. No merge or deployment.

---

## Reviewer 1 independent assessment — ChangesRequested

**Frozen source** `2ad69b6cca7966ba79cdaa9c786a40300bec6865` (`feature/general-execution-scheduling`) against base `b9b9e7a4b201b9baf581a644c1a7ca451f2943ba`. Clean isolated worktree; 12 files changed, 957 insertions/94 deletions; diff-check clean. Reviewer did not edit, merge or deploy source.

**Implemented and inspected:** One client-local persistent CLI/browser scheduler; Once/Every/AfterAgentIdle deadline logic and no-backlog replay; UUID attempt matching; scheduled RunCommand through the existing Client.receiveCommand authorization/audit path; BrowserScript uses existing browser window and explicit permission/bridge; server OAuth scopes and observation filtering; successful cross-device SignalR fanout when both devices are connected. The existing tests exercise clock-driven idle reset and rearm, canceled schedules, recurring skip, and stale attempt replacement.

**Blocking findings persisted:**

1. **P1 — `findings/manager-idle-stale-cross-device-activity.md`:** Connected-device activity hints are best-effort only. While a scheduler client is offline or misses a hint, it continues executing due actions against its old local `tui.GetAgentLastSeen` without any reconciliation or fail-closed network gating. Active Manager on another device can consequently be relaunched after a false 15-minute idle interval. No reconnect catchup or authoritative activity stamp. This violates the single-flight intent and cross-device acceptance criteria.
2. **P1 — `findings/schedule-claim-not-process-atomic.md`:** A process-local monitor plus unconditional temp-file replacement cannot provide an atomic cross-process claim. Two JarvisClient instances sharing the default state path can both load pending, each persist its own running attempt, and both execute the occurrence. The scheduler starts before server registration and keeps ticking offline; server device exclusivity would not prevent this.
3. **P2 — `findings/claim-rename-lacks-directory-sync.md`:** Temporary file fsync before `File.Move` is not a durable fsync of the containing directory entry after rename; power loss can roll the claim back and allow replay. Source-level observation only, not a reproduced power-failure test.

**Independent .NET 11 Release verification:** `Server.Tests` **129/129 passed**; `Client.Tests` filtered `BrowserSchedulesTests` **13/13 passed**; `git diff --check` exit 0 and frozen worktree clean. Full unfiltered Client.Tests was **not independently run**; submitter reports a 180-second timeout without a reported assertion failure. Passing current tests do not exercise disconnected cross-device manager activity or two competing client processes, and do not establish the required crash durability.

**Disposition:** RequestChanges on this frozen source. Preserve the good one-scheduler design and API; establish authoritative/fail-closed idle freshness, cross-process occurrence ownership, and durability prior to a re-review. No merge/deploy/production Manager clock activation based on this verdict.
