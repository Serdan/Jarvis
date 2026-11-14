# P1 — Failed attributed activity persistence must block dispatch

Frozen source: `5afad770e0d7cd237d8c7d9f9d044674951a990f`.

**Location:** `Server/Services/ClientService.fs:310-323`, `Server/Services/FeedbackStore.fs:122,282-307`.

`SendCommandToUser` wraps `RecordAgentActivityForUser` in a `try` that logs a persistence exception and returns `None`; execution then proceeds through `client.ReceiveCommand`. If the SQLite write failed, the Manager's tool call may execute without an authoritative activity watermark. The `unverifiedActivity` safety marker is only an in-memory `ConcurrentDictionary` in `FeedbackStore`. A server restart clears that marker, allowing `GetAttributedAgentActivity` to return an older, apparently verified timestamp. A due `AfterAgentIdle` schedule can consequently launch another Manager while the first Manager has been active on another device.

**Required correction:** Do not dispatch an attributed tool call after failure to durably persist its activity, or guarantee an equally durable and restart-safe unknown-activity state that prevents idle verification until explicitly repaired. A negative test should fail the SQLite write, attempt dispatch, restart server storage, and assert that no stale Manager idle verification is permitted.

**Review status:** Blocking P1. Existing positive persistence tests do not exercise this failure/restart sequence.