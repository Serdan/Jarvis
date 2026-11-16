# P2 — Composition overview pages can exceed the client transport limit

**Frozen commit:** `a939ff7d5a5cdba1c219ebde0d1b1ab2488619e5`

**Locations:** `Client/Modules/WorkCompositionGitLedger.fs:117-172`, `Client/Modules/WorkCompositionGitLedger.fs:18-23`, `Client/SignalR/Client.fs:313-338`, `Common/Messages/AgentCommand.fs:1384`, `Client/Modules/WorkCompositionOperatorInputs.fs:41-45`.

**Problem:** `loadOverviewPage` bounds the item *count* to at most 100 (default 50), but serializes complete `CompositeWorkUnit` records, including each unbounded `Acceptances` history and arbitrarily long valid specification text. It does not budget serialized bytes or provide a way to page a single unit's acceptance history. The client rejects any ordinary response larger than `900 * 1024` bytes as `OutputTruncated`, with no `Items` or `NextCursor`. Consequently a legal graph can be unreadable via the new public API even when its Git ledger is healthy.

**Deterministic reproduction sketch:** Create one Project and 15 child Features (16 units total), each with a 60,000-character `scope` string, other required fields small. Every individual Create mutation is below the 64 KiB JSON input limit, and the graph model imposes no aggregate spec-byte cap. Then call `GetWorkComposition` with default pageSize=50. The response necessarily exceeds 900 KiB before JSON overhead and fails at `Client/SignalR/Client.fs:329-338` rather than returning the promised bounded page. More importantly, repeated historical acceptances on a single long-lived unit can eventually exceed the cap even for `pageSize=1`.

**Required:** Make the public view byte-bounded end to end, preferably a compact DTO with current acceptance/progress and separately paginated historical proofs (or an equivalent bounded history projection); preserve the pinned snapshot contract, and add a negative-space test through the actual client response-size guard. The normal default query and single-unit queries should remain serviceable without discarding authoritative history.

**Scope:** This is an operator-read API defect in the reviewed slice; it is independent of the intentionally unwired scheduler bridge.