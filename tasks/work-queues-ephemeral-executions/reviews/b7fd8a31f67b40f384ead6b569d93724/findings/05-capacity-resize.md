# B05 — P2 — Global-cap invariant has no concurrency-safe configuration-reduction policy

**Frozen source:** `36155553147705748c90d73a5aaf497b511768ef`, lines 65, 117, 193–200, 215, 301–316; acceptance 30–33.

**Finding.** The spec states the invariant `0 <= globallyReservedActiveAndUncertainLaunches <= maxConcurrentAgents` for the account-global ledger, but does not define an update protocol for `maxConcurrentAgents` when the operator lowers the configured maximum below current occupied/uncertain reservations. A plain config commit changing 8→3 while eight tokens exist breaks the stated invariant immediately, despite every launch having followed CAS. Concurrent reserve versus resize also needs one serialization point.

**Required clarification.** Either reject reductions below current reserved count until natural terminal releases occur, or explicitly model a drain-only target limit with grandfathered reservations and a revised invariant; never evict an uncertain token or treat a timeout as terminal merely to satisfy the new number. Version the cap in the same account-global CAS domain as reservations, and specify how a scheduler/Manager handles a cap change after its preflight and before the authoritative reserve.

**Acceptance fixtures.** With eight occupied tokens, request max=3 and race a ninth reserve with the update; show deterministic rejection/drain behavior and zero new launch admits. Verify release-triggered scheduling resumes only when within the applicable cap and old reservations are not silently discarded.

**Severity:** Nonblocking for a fixed-limit Phase A simulation, but must be settled before exposing runtime limit changes or activating cross-project scheduling.