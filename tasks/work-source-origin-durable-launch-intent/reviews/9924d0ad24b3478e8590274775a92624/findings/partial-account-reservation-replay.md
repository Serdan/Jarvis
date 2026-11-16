# P1 — Partial WQ07 reservation cannot be replayed through the durable-intent adapter

**Frozen source:** `9924d0ad24b3e78e0590274775a926244312e66f`

**Location:** `Client/Modules/WorkQueueDurableLaunchIntent.fs:472-499`, particularly `state.Launches.ContainsKey original.LaunchId` and the fallback `freshSelection context`; `AccountAdmissionAdapter.fs:375-432`.

**Failure sequence:**
1. A canonical intent is stored and `admitPreparedExplicit true` begins. The initial global selection succeeds with cap=1, then `WorkQueuePracticalAdmission.admitExplicit` invokes `AccountAdmissionAdapter.admitUsing`.
2. WQ07 `reserve` commits the original held account token. The following WQ04 project admission CAS fails, times out or has unknown acknowledgment, leaving **one held account token but no project launch** (a documented valid recovery state).
3. Calling `admitPreparedExplicit` again with exactly the original intent finds `state.Launches.ContainsKey original.LaunchId = false`, so it calls `freshSelection` **before** `AccountAdmissionAdapter.admitUsing`. The existing token fills cap=1; `freshSelection` rejects `CapacityFull` / no available slots, and the wrapper returns `TokenRetained` forever rather than reconciling/resuming the original reservation.
4. The lower WQ07/WQ04 adapter has an original-UUID replay path and an abort-only-unbound path, but neither is reachable through this wrapper for the held-token state.

**Impact:** A normal partial/unknown cross-ledger admission can strand the only account token; an approved original cannot finish its project CAS or reach a typed abort/reconciliation path. This violates the task's explicit crash/unknown-ack recovery contract, even though it fails safely against duplicate sends.

**Required fix:** Inspect the **exact immutable original** account receipt/token, verify that it belongs to this project/work/role/launch/admission UUID and is still Held, and route a safe same-original replay to the existing admission/recovery adapter without requiring fresh empty global capacity. Preserve the stale/global-winner gate for genuinely new reservations and never issue a replacement UUID or release an uncertain token. Add a two-clone cap=1 test with WQ07 reservation committed and WQ04 project admission absent/unknown, then verify exact-original retry is recoverable without a second token.