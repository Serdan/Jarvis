# P2 — one-pass idempotency misses the already completed publisher adjudication

**Frozen reviewed source:** `5b973599331f01cb96d9c5e7fbe99a3148adccb7`; **base:** `d702352ef705f9991ac1cb14dc15a13cce78aa44`. **Scope:** `docs/reviews/FindingsAdjudication.md` lines 20-21, 55 and 57-64. This is a documentation-only, original-acceptance finding, not a request for a backend adjudication engine.

## Demonstrable conflict

The new procedure checks only the deterministic task `adjudicate-<reviewIdN>` before creating a case, and its idempotency instruction likewise looks only for an existing *deterministic* case. For historical review `08fdc04c145540b29d6920bd38f946f1` (Reviewer 2 ChangesRequested decision sequence 9829 on frozen `2f478f79deb1fc4d1be3416550844692657d7ed7`), that check resolves `adjudicate-08fdc04c145540b29d6920bd38f946f1`: canonical `get_task` returned `Missing ledger entity`.

But the same decided review was **already terminally adjudicated** by independent Adjudicator 1 under the non-deterministic canonical task `publisher-findings-adjudication-pilot-20261121` (attempt `6f3117277d954b9d9a31b1fb37f8b480`, phase **Finished**). Its actual canonical checkpoint **sequence 9948** identifies review 08fd..., frozen source, both exact finding blobs and the adjudicator's separately reasoned final dispositions: **F1 Narrow; F2 Narrow**. F1 retains real supported non-force publication and verification guards without requiring a separate signing issuer; F2 preserves fail-closed for unproven managed pre-v1, legacy branch-only support and conditional reconsideration upon authentic historical evidence. Original ChangesRequested verdict remains unchanged.

The historical runbook instead offers an expressly *illustrative* F1 Narrow and F2 **Defer**, and says these are not canonical rulings. While clearly labeled hypothetical (not a false claim of a decided verdict), it omits the *actual* completed pilot and its two Narrow dispositions. More significantly, a Lead applying the documented exact deterministic lookup is permitted to establish a **second** case and terminal decision for the same already-adjudicated review. The policy explicitly requires one bounded disposition per finding and duplicate-decision idempotency.

## Original requirement, impact, proportional remedy

Original pinned task `findings-adjudication-gate-20261121` requires historical read-only application, **duplicate decision idempotency**, terminal one-pass reasoning and no recursive/duplicate adjudication. A second decision could contradict accepted correction scope, cause unnecessary rework or overwrite the practical meaning of the original terminal findings even though the immutable review is unchanged. This is a concrete existing historical case, not a speculative new threat model.

**Bounded correction:** Before creating even a deterministic new case, require Lead to check already completed *canonical adjudication cases for the exact review ID/decision binding*, including historical/nonstandard task IDs, and reuse the existing terminal record. For this specific historical example, reference `publisher-findings-adjudication-pilot-20261121` checkpoint 9948 and distinguish its **actual F1 Narrow / F2 Narrow** from any separately labeled hypothetical alternatives. Preserve the operator-approved trusted-writer scope, no signer requirement, original review status and independent Worker4 implementation/review. No schema, API, database, engine, new source test or second adjudication is warranted.

This finding does **not** assert the documentation falsely claimed backend enforcement: its manual-only limitation is accurate and proportionate. It also does not demand a cryptographic publisher or re-adjudication of the prior verdict.
