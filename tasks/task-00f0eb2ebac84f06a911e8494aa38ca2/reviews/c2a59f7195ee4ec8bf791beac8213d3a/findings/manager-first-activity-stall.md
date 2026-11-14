# P2 — A successful launch with no Manager tool activity waits forever

Frozen source: `5afad770e0d7cd237d8c7d9f9d044674951a990f`.

**Location:** `Client/Modules/BrowserSchedules.fs:226-241`, `:523-542`, `:561-571`, `:652-655`.

After `execute` returns `Ok` for an `AfterAgentIdle` browser launch, `update` transitions the occurrence to `waitingActivity` if the Manager has not yet made a tool call. The 30-minute stale cutoff is evaluated only for `running` occurrences. The `waitingActivity` branch performs fresh queries but never re-arms without a newer attributed Manager activity. Thus a successfully submitted browser launch that stalls before its first Jarvis tool call leaves the heartbeat permanently inert. Restart preserves the waiting state, and there is no recovery deadline.

The task specifically calls for a bounded stale-replacement path for silent/stalled Manager invocations. Either implement an explicit 30-minute no-first-activity recovery with stable attempt/cooldown identity and no duplicate storm, or obtain an operator decision to treat successful submission as terminal until a tool call; current behavior does not provide autonomous recovery.

**Suggested negative test:** Execute a browser launch successfully and never emit a Manager tool call; advance a deterministic clock beyond 30 minutes, restart, and assert the documented recovery policy.

**Review status:** P2 behavior gap, independent of the two blocking P1 findings.