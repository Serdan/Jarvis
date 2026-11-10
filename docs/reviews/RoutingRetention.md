# In-memory device and conversation routing retention

## Scope and policy

Jarvis is intentionally a **single-instance**, authenticated multi-device server. The routing state in `Server/Services/UserService.fs` is **ephemeral**: it is created on a live SignalR connection or MCP device selection, exists in process memory only, and is discarded on restart. There is no schema migration, backing store or coordination between instances.

| State | Retention | Per-user cap | Global cap |
|---|---:|---:|---:|
| Registered device session | While its transport remains connected | Not evicted | Not evicted |
| Connected transport, including before device registration | While connected | Not evicted | Not evicted |
| Disconnected device session, with failure diagnostics | 7 days after disconnect | 64 | 2,048 |
| Conversation-specific device selection | 24 hours since its last successful selection/read | 256 | 8,192 |
| Default-device pointer | While that device is retained; repaired on eviction | One | One per user with retained devices |

The retention configuration is a typed `UserRetentionPolicy` in process, with validated positive durations and caps. The explicit public `UserService()` constructor is required for ASP.NET Core dependency injection and uses defaults; a separate `UserService(clock, retention)` overload supports deterministic tests. F# optional-argument constructors are deliberately not used because their runtime signature does not resolve in DI. No user-facing configuration setting or new database is introduced.

Expiry runs **lazily on service access** (not a background task) at most once every 2 minutes. Actual reclamation may lag the TTL if no requests arrive, but insertion caps prevent unbounded accumulation of disconnected sessions and conversation selections during sustained activity. A forced `PruneExpired()` and aggregate-only `RetentionCounts` are available for diagnostics and tests; no user IDs or conversation keys are returned. Capacity eviction is deterministic: remove the oldest disconnected device or least recently used conversation first, breaking time ties by user/device or conversation identifier. Active registrations are never evicted merely to satisfy the disconnected-device quota.

## Routing and fallback

- An explicitly specified `deviceId` is **never** routed to a different device. If its session was evicted (or the device belongs to another user), selection fails with the existing no-device/NotFound path.
- A conversation selection remains pinned to its named device while its record and device session remain valid. A retained **disconnected** device still appears as Disconnected and is not silently replaced before expiry; device-side command dispatch then reports that the selected device is offline.
- If a conversation selection expires, reaches capacity eviction, or points to an expired device, resolution uses the user's current default. Reading a live conversation selection refreshes its idle timestamp.
- When a default-device record is evicted, the default pointer is repaired to the **oldest currently registered** remaining device (ordered by registration timestamp, then device ID). If no registered alternative exists, the default is cleared. No automatic fallback chooses another disconnected device.
- If the old device re-registers **before** its disconnected-session TTL, the default and unexpired conversation selections still refer to that device ID and can route to its new connection. If it re-registers **after eviction**, it does not steal an already repaired default or silently reattach an evicted conversation; users can select it explicitly.
- On a server restart, all selections and session records are absent. The first subsequently registered device becomes the new default.

Each registration receives a **new monotonically increasing process-local generation**. Unlike the old per-device counter, the number is not reset to 1 when a disconnected device is pruned. This prevents an expired device's new registration from matching an old generation without retaining tombstone records. Both SignalR response tracking and session validation still match the authenticated user, connection ID, generation and correlation ID; a server restart also recreates the pending-response tracker, so cross-restart responses cannot be accepted. The numeric generation is an opaque safety token, not a count of a device's registrations.

An authenticated transport ID cannot be silently rebound to a different user: registration must match a known, live transport owner, and duplicate transport-connect attempts are rejected. Re-registering the same device on a different connection invalidates the previous connection's association. When a single transport registers a different device, the old device is marked disconnected and remains eligible only for bounded retention. All reads and writes are guarded by the existing `UserService` lock, so a pruning pass cannot interleave between automatic selection and session resolution.

## Verification and operational evidence

`Server.Tests/RoutingRetentionTests.fs` uses an injected mutable clock and deliberately small caps to check:
- Disconnected session expiry, device fallback, explicit override refusal and reconnection after expiry.
- Connected device and transport preservation beyond their nominal TTL.
- Last-used conversation refresh versus idle expiry.
- Per-user and global conversation caps, including thousands of synthetic selections.
- Per-user and global disconnected-device caps while connected sessions survive.
- Reused transport ID and stale-generation response rejection after pruning.
- Per-user isolation and zero state after a simulated server restart.
- Invalid retention policy rejection and authenticated transport owner isolation.
- Reconnect before expiry, LRU preservation on reads, and repeated registration on the same transport.
- Production-style `ServiceCollection.AddSingleton<UserService>()` resolution and public parameterless constructor reflection.

Existing `Server.Tests/ServiceTests.fs` and `FeedbackStoreTests.fs` validate multi-device routing, repeated registration, same-transport device switching, stale disconnect behavior, and MCP metadata selection. Two legacy test assertions were updated to check generation **freshness** rather than the obsolete per-device first-registration numbering.

The implementation does **not** provide persistent user device preferences. `ListDevices`, `GetConnectionDiagnostics` and registered session status can display retained disconnected devices until TTL or cap evicts them; after eviction, old failure details no longer appear in this in-memory cache. Persisted connection-event diagnostics, where configured, remain independent of these retention controls. No background sweeper process, database, or distributed state was added.
