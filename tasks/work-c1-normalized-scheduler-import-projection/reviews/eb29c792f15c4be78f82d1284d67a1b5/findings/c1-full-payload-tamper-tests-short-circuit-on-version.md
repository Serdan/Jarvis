# P1 — C1 full-payload tamper tests do not exercise the equality predicate

**Frozen candidate:** `3e795e008295f800a3010ca4bf67cb9a52be2977`; corrective diff against `d6f49a6c8b29d645e4641a0595a704190e30e3ed`.

## Observed source/test behavior

The production correction adds a meaningful full-source check: `originalRow` in `Server/WorkLegacyImportProjection.fs` (approx. lines 223–231) requires **both** `row.Version = 1L` and `row.Value = expected`; `OriginalC1.encode` duplicates the original importer `SnapshotJson` encoding field-for-field. Source inspection confirms that queue/graph/account/stage/role/origin/receipt values are now compared against the typed inspection.

But `tamperImportedJson` in `Server.Tests/WorkLegacyImportProjectionTests.fs` (approx. lines 266–285) corrupts its fixture by invoking `WorkSqliteAuthority.Commit` with `ExpectedVersion = Some original.Version`. The actual `WorkSqliteAuthority.applyRecord` implementation in `Server/WorkSqliteAuthority.fs` (approx. lines 402–433) increments the record version on **every** existing-row update (`version=version+1`). Thus the C1 payload-tamper cases in `c1_projection_binds_full_original_payloads_not_only_keys_and_union_cases` and `c1_projection_rejects_same_key_changed_waiver_receipt_from_original_stage` produce a **version-2** original row. `originalRow` will reject all of them on `Version <> 1` even if the newly added `row.Value = expected` predicate is deleted.

This is a false-positive regression proof for the exact P1 issue in prior review `a819d4fc75d547e9a4bab74b1d6f6ae2`: same-key, same-version **payload** mismatch is not independently tested. This is a test-evidence deficiency, **not** a demonstrated successful unauthorized promotion in the current corrected source. The independently run .NET 11 Release tests all pass (12/12 focused, 451/451 full), but those results cannot establish that the new content comparison is active.

## Required correction

Add at least one direct fixture-level value-only corruption that **preserves the original imported row version (=1)**, one that changes a meaningful C1 nested payload while retaining the same IDs/keys/cases/SourceSha/graph/queue/generation metadata. A same-key waiver **receipt value** is an ideal regression. Isolated disposable-fixture `Microsoft.Data.Sqlite` direct SQL (already used in `WorkSqliteAuthorityTests.fs`) is acceptable for *test-only* adversarial tampering: update `body_json` without changing `version`, then call the normal projection and assert `Incomplete`, unchanged projection sequence and no `Project/authority`. Also cover at least one other originally missed field (e.g. account payload or role subscription content) under preserved version. Alternatively construct an independent inspection/source mismatch that passes preflight while leaving the imported row at version 1 and proves the same predicate.

Preserve the version-increment tamper cases for normal store-mediated conflicting writes if useful; do not replace all of them. No production source edits, Scheduler relaxation or live migration are needed for this correction.

## Separate boundary

`WorkSqliteRecordGuard` compares version, graph/stage/claim/STOP/role generation and source SHA, not `BodyJson`. The current positive source proof therefore relies on the legitimate authority-store version-increment contract to detect *concurrent* updates after inspection. A version-preserving out-of-band SQLite writer would be outside that contract and is **not asserted here as an observed supported runtime bypass**. Before any production cutover, the trust model should explicitly preserve this versioned-writer assumption or separately review a stronger body-digest CAS witness. This observation is not an additional required change to the owned projection module in the present default-off review.

**Disposition:** ChangesRequested for targeted test evidence, with the corrected source predicates and C2 audit field equality recognized as substantively improved.