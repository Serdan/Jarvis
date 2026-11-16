# F03 — P2: Explicit scratch recovery does not distinguish a stale root from an active test run

**Frozen source:** `25a452e0ada1b99e286d40051d8438e905df4abd`  
**Location:** `scripts/recover-hermetic-client-scratch.fsx:19-35, 53-76`; root created at `scripts/test-client-hermetic.fsx:128-146`

The recovery command requires an exact 32-hex scratch ID and checks owner, DACL and reparse ancestry, which correctly bounds *where* it may delete. It does not establish that the selected root is actually orphaned. A still-running harness publishes its scratch ID, and a concurrent recovery invocation using that exact ID can delete an active run's transient files while its process remains alive, especially if no file is currently locked. The 30-second sharing-violation retry may subsequently delete files released during a live run. Exact naming/ownership alone are not evidence of liveness or orphan status.

**Required correction:** Fail closed against an active harness/testhost (e.g. a reliable per-root lease held for the entire run, with recovery refusing a live lease), or make documented operator-confirmed process-tree termination and exact stale-ID provenance an explicit enforced precondition. Include a disposable Windows concurrent-active-root negative test. Do not attempt broader directory sweeps or touch the real profile.