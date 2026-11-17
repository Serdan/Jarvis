# P1 — Fence accepts conflicting identities for one physical remote ref

**Frozen source:** `bfab99d09e221a2661453dce7bb5fad13bfaf743`  
**File:** `Server/OriginalGitPublisherAdmission.fs`, `validateFenceRequest` duplicate check.

**Reproduction:** Start from the valid test fixture `Targets = [accountTarget; projectTarget]`; append `{ projectTarget with LedgerEpoch = 2 }`. Both project targets pass `validateTarget`. The `List.distinct` key includes `LedgerEpoch`, `LedgerId` and `ProjectId`, so the *same* `CanonicalRepositoryId + GitRef` is counted twice under conflicting identities, and `validateFenceRequest` returns `Ok ()`.

**Why this matters:** A remote Git ref has only one current canonical identity/epoch at a time. A barrier must never accept two contradictory claims for that exact publication path, or a subsequent verifier could incorrectly report comprehensive per-epoch scope while checking the same head twice. The common exclusive authority must refuse conflicting physical-ref mappings before acquiring any barrier.

**Required:** Deduplicate and detect conflicts using the *authority-resolved* canonical repository identity plus exact physical ref, not a key that includes the disputed epoch/ledger metadata. Check each claimed ledger ID, epoch and approved remote fingerprint against its trusted registry. Add a negative test with two epochs for one exact ref. A syntactically complete caller-supplied target list alone is not evidence that **all** active projects/credential channels have been covered.
