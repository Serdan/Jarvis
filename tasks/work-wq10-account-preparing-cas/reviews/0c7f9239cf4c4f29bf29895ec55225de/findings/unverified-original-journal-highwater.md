# P1 — Original source/journal high-water is treated as optional for an authoritative account CAS

**Frozen commit:** `4307675a4b7b81310c57f2a375fab998790ac462`

**Locations:** `Client/Modules/WorkQueueCanonicalMigrationPrepare.fs:283-290, 335-363, 505-514, 543-547`.

The read-only assessor unconditionally reports `OriginalJournalsAndSourceHighWaterMissing`: it has no proof that legacy original journals and source-effect high-water are fully reconciled. The new account Preparing writer *explicitly removes this gap* in `accountOnlyGap`, along with `AuthenticatedOfflineReviewMissing`. Its only numerical high-water validation compares the **new project ledger** header `NextSequence - 1` with a caller-chosen expected value. That is not the old source/journal high-water, and the unsigned presence of a 64-character `ReviewedOfflineReportSha256` is not evidence that the report was read or independently validated. Thus the new writer can move the account Frozen -> Preparing with unknown original source/journal coverage, contrary to the required negative test for unknown source/journal high-water.

This is distinct from project-side Prepared conversion: the absence of complete original source provenance must be explicitly represented as a blocked *account* eligibility check, rather than silently reclassified as a later project conversion concern.

**Required correction:** Keep `OriginalJournalsAndSourceHighWaterMissing` blocking until an independently verified, source-bound, immutable high-water/coverage attestation exists for both participants, with journal IDs and exact heads included in the signed statement. A digest alone is not enough. Add a frozen, empty-capacity, syntactically signed fixture with missing original journals/unknown source high-water and assert no account CAS/ref movement.