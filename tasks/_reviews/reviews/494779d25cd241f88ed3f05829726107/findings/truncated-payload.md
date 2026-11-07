# [P2] Cache considers truncated AGENTS.md fully delivered

**Location:** `Client/Modules/AgentInstructions.fs:142-158,232-251,329-370`

**Reproduction by code path:** `readDocument` stores `Sha256` of the *entire source file* but adjusts `Content` according to the remaining per-response byte budget. `DeliveryCache.Filter` uses only that full source SHA as its fingerprint. If a long hierarchy leaves insufficient budget for a 50 KiB project instruction file, the first call can deliver only its first 24 KiB with an incomplete-source warning. A following call targeting a shallower path can read and return the full 50 KiB, but `Filter` suppresses it because the SHA has not changed. The remainder is never automatically delivered within that working turn, even when space becomes available.

**Why this matters:** Source content was not actually delivered in full, but the cache treats it as already delivered. This defeats delivery-completeness guarantees when truncation depends on the depth/size of the request, not a permanently oversized file.

**Recommended correction:** Track delivery completeness/byte count or a fingerprint of the *delivered* content in addition to the full-file hash; allow a later complete version (or a larger prefix) to supersede an incomplete one. Retain explicit truncation notices for content still omitted. Regression test: deep call with constrained budget, followed by shallower call returning a complete unchanged file.