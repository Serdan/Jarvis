# P2 — SHA-256 project Git heads are incorrectly reported UNKNOWN

**Frozen commit:** `aeab8b208deafda979339249ead473ba8c7f7809` (base `ee50af8b68694e0e5fda1c28a251d07db3363aab`).

**Location:** `Client/Modules/WorkQueueDashboardReadBinding.fs`, `validHead` and `toSnapshot` (around lines 88–110).

**Issue:** The new Work-first loader rejects every verified Git head except 40-character hashes:
```fsharp
let private validHead (head: string) =
    not (String.IsNullOrWhiteSpace head)
    && head.Length = 40
    && head |> Seq.forall Uri.IsHexDigit
```
Jarvis's authoritative `LedgerStore` explicitly allows both SHA-1 and SHA-256 object IDs via `oidPattern = Regex("^[0-9a-f]{40}([0-9a-f]{24})?$")` at `Client/Modules/LedgerStore.fs:165`; the same regex is applied to verified commit heads and reads throughout that module (including lines 600, 703-704, 867, and 1489). A legitimate project with a 64-character SHA-256 Git head can therefore produce a successful `WorkQueueToolFacade.readWithBinding` but is always downgraded to `UNKNOWN: no valid verified project Git head` by `toSnapshot`.

**Impact:** Read-only Work-first dashboard unusable for approved SHA-256 repositories, despite current ledger support. Incorrect fail-closed result, not an authorization bypass.

**Required correction:** Validate the same 40- or 64-character lowercase Git OID grammar as `LedgerStore` (ideally share one helper rather than maintaining contradictory validation rules). Add a positive SHA-256 verified-head fixture and negative malformed-length/nonhex fixture through the new binding's `Load` path.

**Scope:** This does not change default-off host installation, WQ04 provenance, scheduler dispatch, or project authorization.