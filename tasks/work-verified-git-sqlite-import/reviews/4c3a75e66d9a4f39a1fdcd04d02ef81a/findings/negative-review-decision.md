# P1 — Legitimate negative review decision rejects an otherwise valid source

**Frozen commit:** `76944ed50389b73b93ee307063049e8e80f6a368`

**Location:** `Server/WorkLegacyImportPreflight.fs:308-316`.

The legacy `decideReview` state transition (`Server/WorkQueueContracts.fs:1322-1334`) records `DomainState.FinalizedPendingTerminal decisionReceipt` for a finalized review *regardless of the verdict*. The actual positive/negative verdict lives in the independently verified receipt. However, the import preflight requires `decision.Accepted` to be true for **every** frozen-review stage in that domain. A legitimate `ChangesRequested`/rejected review with an authentic receipt, matching frozen SHA and no integration should be imported as rejected, not classified `MissingReviewDecision`.

**Required:** Validate receipt existence, subject, frozen commit and finalized-domain receipt for either verdict; keep the `Accepted=false` outcome. Only an actual accepted decision may authorize integration or satisfy acceptance evidence. Add a two-project negative-review fixture with `Accepted=false`, matching finalized `decisionReceipt`, no integrated descendant, and assert successful preflight plus SQLite persisted rejection. Add a mismatch-negative control that still fails. Do not reinterpret rejected feedback as approval.
