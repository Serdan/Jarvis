# F1 — Integration blocker: four executable FutureRed contracts (intentional; not an I0 source defect)

**Disposition:** MERGE HOLD / NO-GO for integrated Stateful Work rollout. The isolated I0 acceptance-preparation artifact itself is reviewable and may be Accepted within that narrow scope; acceptance does not discharge these four gates.

**Frozen source:** `930574c223fd148c557d86c968e4f19bc2e1d6fb`, `Server.Tests/WorkStatefulRolloutAcceptanceContractTests.fs` lines 205–219.

**Independent execution:** the isolated `FutureRed` VSTest selection discovered exactly four NUnit tests and produced exactly four expected `Assert.Fail` results (exit 1). An unfiltered full Server.Tests Release run would therefore not pass. None of these tests is skipped/ignored, and filtering them out proves only the narrower 3/53/56 component groups.

**Unsatisfied live-integrated predicates:**
1. Authenticated original-Git producer/ref/commit/receipt to typed B ingress (no caller-forged authority).
2. Verified trusted Git backup root and complete eight-kind deterministic JSON export/empty-SQLite restore, including full receipts/audit and rejection matrix.
3. Restore-time persistent RecoveryRequired fence for stale backup N plus newer original N+1 artifacts and uncertain Prepared/Unknown one-shots *before any host serving or admission*.
4. Authenticated and project/account-scoped complete H Work-first status, including truthful backup/Recovery uncertainty.

**Required future proof:** merge neither this fail-by-design branch nor a weakened/ignored RED variation into main. Wait for the relevant producer changes, freeze independent accepted exact SHAs, re-pin the integrated source, replace the assertions only with real behaviorally equivalent tests that demonstrably pass, run full .NET11 Server+Client Release and cross-client backup/recovery/rollback checks, and require explicit operator GO before any production authority cutover or unattended Scheduler activation. The current source review offers no G4–G6 or live-host approval.