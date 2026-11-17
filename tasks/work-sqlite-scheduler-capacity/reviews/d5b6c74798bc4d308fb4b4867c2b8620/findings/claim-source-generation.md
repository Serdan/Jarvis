# P1 — Claim can retain the wrong work item's source SHA and generation

**Frozen revision:** `a8e8639b16596c2cbcbe93ee21e94f88496ff8af`
**Source:** `Server/WorkSchedulerSqlite.fs:436-449, 489-545`

`reserve` records `StageGeneration` and `SourceSha` from the initially credited work item on the durable launch row (lines 436–449). At claim time, the Scheduler deliberately reranks all compatible eligible work (lines 489–506), and may choose a *different* item after a priority/availability update or to preserve another launch's specialist credit. But the claim mutation (lines 515–522) updates only `BodyJson` and `ClaimGeneration`. The row now identifies `primaryWorkId = B` while retaining A's `StageGeneration` and `SourceSha`. `launchValid` (lines 195–211) does not detect the mismatch.

**Reproduction / missing negative test:** Arrange a registered unclaimed launch with credit A, durable launch source SHA `shaA` and stage generation 1; make compatible B (with `shaB` and generation 2) the highest-ranked eligible claim, and ensure other launches remain matchable. `claim` chooses B, but `ReadRecord(Launch)` still reports `shaA` and generation 1. Current claim tests use one work item and `SourceSha=None`, so they cannot detect this.

**Impact:** The claimed primary Work identity diverges from the durable source/generation provenance that future source permission, continuation, one-shot effect and recovery boundaries must trust. This violates E0's explicitly required source/generation consistency despite today's default-off host.

**Required correction:** Atomically bind the launch's relevant source SHA, stage generation and other work-specific provenance to the *actually claimed* stage, or explicitly preserve both original-reservation and current-claim provenance in separate, validated fields; strengthen snapshot validation and add a two-item different-SHA/different-generation claim regression. Do not rely on the future importer to infer a missing transition.
