# P1 — Idle Manager heartbeat can fire on stale cross-device activity after disconnect or dropped hint

**Frozen head:** `2ad69b6cca7966ba79cdaa9c786a40300bec6865`

**Locations:** `Client/Program.fs:274-282, 400-413, 440-469`; `Client/Modules/BrowserSchedules.fs:445-495`; `Client/Modules/ConsoleTui.fs:1857-1893`; `Server/Services/ClientService.fs:183-196, 302-313`.

The new cross-device observation is a best-effort SignalR notification delivered only to devices connected at *that moment*. The server does not keep a durable per-(project, agent) last attributed activity time or supply a snapshot on reconnect. The scheduler derives `AfterAgentIdle` due decisions solely from `tui.GetAgentLastSeen`, and `scheduler.Start()` runs unconditionally before/through disconnected SignalR periods; reconnect only invalidates the unrelated agent-state cache. The server also catches notification delivery failure, continuing with no retry or reconciliation.

Concrete failure sequence: scheduler device B last observes Manager at t0, B disconnects or misses an activity notification; Manager keeps issuing attributed tool calls on connected device A at t0+5m, t0+10m, etc.; at t0+15m B still believes Manager has been idle for 15m and launches another Manager. The CLI and browser actions are local and can run while B is disconnected. Thus a *currently active* Manager is not reliably protected against overlapping heartbeat launches by remote activity. Current unit tests inject an always-current in-memory `agentLastSeen` callback and test successful fanout, but do not simulate missed hints, disconnected scheduler execution, or reconnect with activity during the outage.

This violates the canonical cross-device activity/Manager single-flight goal and the task's requirement for an **adequate activity signal or reconciliation mechanism**, rather than reliance on an unaudited stale UI cache.

**Required:** Do not act on an agent-idle trigger when the relevant cross-device activity view is stale/unverified. Persist/reconcile authenticated activity server-side, or otherwise gate each due Manager launch on a verified current view and safe offline behavior. Rehydrate after reconnect, cover delayed/lost notifications and activity on another device while a scheduler is offline. Do not use target `agent_inbox` (which activates) or another agent's observer read as activity. Preserve opt-in per-project scheduling and no-recursion semantics.

**Evidence:** Independent frozen code-path inspection; no branch changes or live remote launch.
