# P1 — Dependency edits leave unclaimed launch credits bound to ineligible work

**Frozen SHA:** `1b886591ae7e87158fc0debe2bf5f01858d17b7d`  
**Source:** `Server/WorkQueueContracts.fs:129-142, 349-397, 454-473, 567-628, 789-815, 978-989`.  
**Architecture:** §8 demand-credit invalidation: changed eligibility must atomically rematch or mark `DemandInvalidatedPendingTerminal` and fence claim.

**Reproduction:** Publish Available A and Blocked B; admit unclaimed launch L covering A (L.Credit=Some A). Add A→B while B is unsatisfied. `setHardPrerequisite` marks A Blocked but does not update L.Credit, rematch other launches, or fence the in-flight claim. A newly Registered L now receives `NoEligibleWork`; a ReservedUnregistered L has neither a valid credit nor a normal `terminalizeNoClaim`/submitted-recovery path. `LaunchState` has no `DemandInvalidatedPendingTerminal` representation. Credit coverage consequently becomes stale while its account token stays occupied.

**Impact:** Demand may be reported as covered by a launch that can never claim it; capacity and scheduling can stall, and a competing Scheduler can make decisions against invalid coverage. CAS conflict on a concurrent actual claim is not enough: the successful edge edit itself must reconcile the credit.

**Required:** Under the same project CAS as an eligibility-changing mutation, compute and persist a compatible injective rematch for all unclaimed credits; otherwise explicitly fence invalidated launch(s) for verified terminal/account release without duplicate dispatch. Add races for ReservedUnregistered, SubmittedUnregistered, Registered preclaim, ClaimNextWork and a concurrent new admission; require no stale credit and no token leak.
