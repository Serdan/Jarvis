# P2 — Count-only composition pagination can overflow the 900 KiB transport cap

**Frozen commit:** `a939ff7d5a5cdba1c219ebde0d1b1ab2488619e5`.

**Locations:** `Client/Modules/WorkCompositionGitLedger.fs:117-172` (`pageSize` 0..100; complete Unit and computed Overview), `Server/WorkComposition.fs` (`stageKinds`, `overview`), `Client/SignalR/Client.fs:313-338` (strict response guard), `Common/Messages/AgentCommand.fs:1384` (900 KiB).

**Problem:** The number of composition units per page is bounded, but serialized bytes are not. In particular, the derived `ReviewStages`/`IntegrationStages`/`ExecutionStages` arrays can repeat the same WQ stage identifiers across *many* units that link to the same canonical task ID. There is no one-unit-per-linked-task invariant, and the WQ model has no small ID-length bound. This is multiplicative response amplification: one stage stored once in the WQ ledger can appear in every queried composition unit's `Overview`. Once the JSON response exceeds 900 KiB, `toAgentResponse` returns `OutputTruncated`, not a page with `NextCursor`. A legal `pageSize=100` can fail despite healthy, within-limit 64 KiB graph and WQ blobs.

**Reproduction to add:** Construct ~100 small composition units sharing a linked validation task, plus ~100 legal WQ FrozenReview/Integration stages with that `SubjectId` and long distinct stage IDs (e.g. 100 ASCII characters). Both canonical record blobs can remain below the ledger's 64 KiB per-record cap. `GetWorkComposition(pageSize=100)` repeats ~10,000 IDs, which alone approaches/exceeds 1 MiB. Exercise the *actual* `Client/SignalR/Client.fs` 900 KiB guard, not only the pure page-count function.

**Fix requested:** Return a truly byte-bounded page/projection; either cap stage lists separately with a pinned cursor or provide their counts and drill-down, and guarantee pages cannot be silently lost to the outer transport limit. Keep the same verified-head semantics and return a useful continuation or explicit recoverable oversized-unit error.

**Correction to my initial wording:** The graph and WQ persisted records are each capped at 64 KiB by `LedgerStore.transact`. A reproduction based on many 60 KiB unit specs or a >900 KiB acceptance history in one graph was invalid and is withdrawn. The finding is specifically about *derived cross-product response amplification*, not unbounded canonical graph blobs.

**Scope:** Operator-read API; independent of unwired graph-to-WQ scheduler activation.