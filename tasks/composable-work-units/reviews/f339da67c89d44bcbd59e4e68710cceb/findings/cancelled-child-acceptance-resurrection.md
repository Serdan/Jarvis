# P1 — Cancelled-child revisions can resurrect stale parent acceptance

**Frozen head:** `4a35fd3849bf2f46b70e9aa4b04571c4b7032205`  
**Relevant code:** `Server/WorkComposition.fs:136-172, 296-327`, `Client/Modules/WorkCompositionAcceptanceAdapter.fs:46-68`  
**Status:** Changes requested

The new `reviewPurpose` correctly binds direct children’s spec versions, cancellation reasons and current proof identities **when evaluating a new parent acceptance**. However, `WorkComposition.currentAcceptanceIn` still represents any cancelled child as the sentinel `(child.Id, -1)`. The persisted parent `WorkUnitAcceptance.ChildAcceptances` has no cancellation generation or child spec version. Consequently, a historical accepted parent can become current again without any new review or integration after its cancelled child changes.

## Reproduction using the pure graph

1. Create project `parent` with child `child`; cancel `child`.
2. Independently review/integrate the parent's composition target and accept `parent`. Its stored proof has `ChildAcceptances["child"] = -1`; `currentAcceptance parent` is `Some`.
3. Apply `Reopen "child"`. This bumps the child's spec version and temporarily makes `currentAcceptance parent = None`.
4. Apply `Cancel("child", "Different scope decision")` (the same reason also reproduces it). `currentAcceptance parent` returns the **old** acceptance again, because the child is mapped to `-1` regardless of changed version/reason. No fresh review, integration or parent acceptance occurred.

This directly contradicts the documented invariant that accepted targets bind the current child revisions and that a child change invalidates stale parent acceptance. The same omission can leave a parent accepted when a still-cancelled child is revised.

**Required correction:** Bind the cancelled child's immutable identity/epoch (at minimum its spec version and cancellation state/reason, or a monotonic cancellation revision) into the persisted parent proof and enforce it during `currentAcceptance`. Keep this consistent with the closure fields already hashed by `reviewPurpose`. Add negative tests for parent acceptance after cancelled-child `Reopen -> Cancel` (same and different reason), and spec revision while cancelled. The old parent proof must stay historical but not become current without a new independently reviewed/integrated parent target.

**Testing observed:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkCompositionLedgerTests` passed **3/3**; those tests cover adding/accepting a child and parent spec revisions but not this cancellation replay. `Server.Tests/WorkCompositionTests.fs` verifies invalidation at `Reopen` but not the subsequent `Cancel`.
