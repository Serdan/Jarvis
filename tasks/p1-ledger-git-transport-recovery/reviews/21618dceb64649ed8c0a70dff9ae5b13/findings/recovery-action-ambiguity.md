# P2 — Recovery diagnostics advertise conflicting next actions after publication

**Frozen source:** `5a24d3da479a2a934e3898e7add99858e394e565`.

**Reproducible control flow from exact code:** `LedgerStore.remoteGitWithRetries` (`Client/Modules/LedgerStore.fs:439-473`) returns a transport diagnostic ending `nextAction=retry-same-uuid` whenever the 12-second budget expires or a subprocess times out, independent of which stage is being called. In `LedgerStore.transact`, a `publication-push` has already been attempted; `confirmation-fetch` calls that helper and catches its `RemoteUnavailable`. When the detail starts with `operationId=`, `:2208-2223` preserves that detail and blindly appends `; nextAction=reconcile-same-uuid`. Thus one `OutcomeUnknown` error contains **two distinct `nextAction` keys** in sequence. A read-only `LedgerStore.fetch` outside `transact` may similarly return `operationId=none; nextAction=retry-same-uuid` even though there is no write UUID. The new tests assert only the phase and/or occurrence of `reconcile-same-uuid`, not uniqueness or applicability of the action.

**Why it matters:** The task requires a clear safe next action with the original UUID, distinguishing an unattempted publication from an uncertain push. A parser using first/last-value wins can choose different actions. Both actions may be safe if the client rigorously reuses the exact UUID and verifies the ledger, but the instruction is ambiguous exactly where the operator needs an unambiguous recovery route.

**Minimal fix:** Let the remote transport layer report phase, bounded attempt count, timeout, and sanitized failure class without assigning write recovery policy. Select exactly one `nextAction` at the outer boundary based on publication exposure: retry the read (no write UUID), retry the original UUID after prepublication failure, or reconcile the original UUID after possible push. Assert exactly one key and a non-`none` UUID for every write failure, including prolonged confirmation loss.

**Severity:** P2 diagnostic contract. Not independently a reason to reject the exact-once ledger mechanics; should be corrected with the P1 API-boundary fix.
