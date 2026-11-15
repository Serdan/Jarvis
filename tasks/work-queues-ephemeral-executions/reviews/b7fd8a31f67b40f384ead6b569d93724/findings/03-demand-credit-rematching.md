# B03 — P1 — Claim-any-eligible-work conflicts with fixed generation demand credits without rematching

**Frozen source:** `36155553147705748c90d73a5aaf497b511768ef`, lines 117–123, 173, 189–208, 383–384.

**Finding.** F01's pre-claim overdispatch is substantially addressed by cross-device demand credits. However, section 8 reserves a credit against an individual eligible work generation **without assigning it**; then `ClaimNextWork` deliberately chooses the highest-priority eligible work, *regardless* of the generation used for its launch's credit. The statement that the claim CAS "consumes/reconciles" its credit does not say how it reassigns every other still-live launch credit affected by that claim. A credit-to-generation invariant and atomic rematching rule are necessary when two launches claim out of reservation order or subscriptions overlap.

**Interleaving.** Available A (higher priority), B. Manager reserves L1's demand credit against A and L2's credit against B. L2 starts first and claims A (per the specified priority rule). Its own B credit is consumed, but L1's A credit is now tied to claimed A. If L1's credit is left untouched, B appears uncovered and a following Manager pass reserves L3 for B while L1 is still in flight. Three launches have now been admitted against two work generations. This reintroduces F01-style duplicate pre-claim demand. With partially overlapping subscriptions, naïvely moving L1's credit to B may also be invalid if L1's queues do not include B. Cancellation/priority changes create the same class of gap.

**Required design change.** State a canonical invariant for *all* live unclaimed launch credits relative to eligible work generations (including role/queue compatibility). Make the claim transaction atomically reconcile the entire affected matching, cancel/reclassify incompatible credits, or choose a different provably safe aggregate demand-credit scheme. It need not bind a specific task to a worker; Worker still selects by deterministic priority. Define treatment of credits whose work becomes claimed/cancelled/superseded and never allow an uncovered queue projection to trigger a replacement launch for demand already covered by a live compatible launch.

**Acceptance fixtures.**
1. L1 credited A, L2 credited B; L2 claims A first. Before L1 claims, a Manager tick launches no L3 for B when L1 can still claim it.
2. Same trace with L1 and L2 having overlapping but nonidentical queue subscriptions; either valid rematching or documented conservative suppression.
3. Concurrent claim, cancellation, reprioritization, and Manager reservation over two clones; no duplicate demand, lost receipt or invalid credit.
4. Late `NoWork` and terminal release restore eligible demand exactly once.

**Severity:** Blocking until the credit rebalancing protocol and race fixtures are part of the accepted design.