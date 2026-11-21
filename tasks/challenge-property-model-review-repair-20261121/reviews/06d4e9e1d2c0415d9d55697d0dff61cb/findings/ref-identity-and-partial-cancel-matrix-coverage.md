# P2 — Explicit wrong-ref/ledger-identity and multi-dependency cancellation cases remain absent

**Exact frozen source:** `332020cd50f3a2a3986933763edb8f81251e0b1c`.

The added 17-mode real bare-Git reverse-index matrix at `Client.Tests/ChallengeStateMachinePropertyTests.fs:1327-1488` genuinely tests missing and extra edges in both directions, wrong owner, stale generation and attempt, canonical duplicate, pre-/post-requeue corruption, and valid historical N1→N2→N3. Independently confirmed 6 green controls and 11 deliberately red original-production fail-open probes.

The identity case `mode=13` (1397-1400) changes only `ProjectId`; `mode=19` is an incorrect **task locator**, not a wrong Git ref or ledger ID/epoch. The cancellation case `mode=12` (1386-1396) blocks on a single dependency and cancels that same Challenge; it does **not** exercise partial cancellation in a multi-dependency wait with the other still pending/satisfied. The pinned matrix expressly requests wrong project/ref/identity and cancelled/partial dependency negatives.

**Recommended correction:** Add bounded distinct wrong-ledger identity/epoch or allowed ref/remote tampering probes (where the binding API permits) and a two-required-Challenge partial-cancellation wait, with canonical paths/HEAD unchanged on fail-closed rejection. Keep the historical positive and original 9000–9003 tests unchanged.