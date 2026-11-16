# P1 — Admission actor is not pinned; a second client cannot replay the original

**Frozen source:** `9924d0ad24b3e78e0590274775a926244312e66f`

**Location:** `Client/Modules/WorkQueueDurableLaunchIntent.fs:23-55,457-513`; `Client/Modules/AccountCapacityLedger.fs:480-499`; `Client/Modules/WorkQueuePracticalAdmission.fs:162-171`.

The immutable `DurablePlanningLaunchIntent` stores `AdmissionOperationId` and the other UUIDs but **no admission actor**. `admitPreparedExplicit` constructs `AccountAdmissionRequest.Actor` from the **current caller's** free-form `actor` argument every time. Both authoritative replay contracts bind actor to that same original UUID:
- WQ07 `AccountCapacityLedger.transact` includes `actor` in `fullRequest` and returns `AccountOperationIdReused` if the previously committed original has a different actor.
- WQ04 `WorkQueuePracticalAdmission.originalCreditAt` explicitly checks `operation.Actor = request.Actor` for replay of an admitted launch.

**Reproduction by sequence:** Client A calls `admitPreparedExplicit true ... "Client A"` and commits the account/project original. Client B recovers the identical stored intent and calls `admitPreparedExplicit true ... "Client B"`. The stored launch is present, so the wrapper takes the replay branch, but `originalCreditAt` rejects it as a different operation actor. If the account reservation exists without the project admission, the different actor also prevents replay via WQ07's idempotent reservation even after the partial-reservation gate is fixed.

**Impact:** The documented two-client same-original replay/restart guarantee only works when both clients coincidentally supply the **same actor**. Existing test `CanonicalPlanningOriginIsPreparedOnceAcrossTwoClientsAndWq07CapOne` uses `"WQ12"` for both admission calls, hiding the incompatibility.

**Required fix:** Persist an immutable, verified **original admission actor** (or a canonical deterministic actor identity) alongside the original admission UUID and always use it for exact-id WQ07/WQ04 replay. The current invoking client may be separately attributed for audit, but must not silently change the original canonical request hash. Add a two-client regression that uses distinct caller actor labels for initial admission and replay, including an unknown/partial WQ07-only reservation.