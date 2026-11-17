# Independent review — Accepted (design-only)

**Request:** 58a4fa7593d749948cbc6fa14f9e5b9c
**Frozen source:** 09b739e32b928e8a2014e93032c65edacea55526
**Base:** 35f675cc1564710d10fdb2f50a03edff39368a28
**Task:** work-trusted-domain-original-producer-adapters

## Verified scope
- Independent Git `show --stat` and `diff --name-status`: only authorized new `docs/design/work-trusted-domain-original-producer-adapters-2026-11-17.md` (118 lines). `git diff --check` returned exit 0 with no diagnostics.
- Compared the eight-record-kind producer/reader matrix, trust boundaries, typed adapter sketch, owner-by-owner follow-ons and adversarial RED-first fixtures to the canonical task. They are present and preserve source-less planning events, negative-only gates, one shared SQLite transaction and no new parallel authority.
- Read pinned base sources: `Server/OriginalGitPublisherAdmission.fs:6-9,78-92` explicitly supplies no production remote authority; `Server/WorkLegacyImportFence.fs:61-84` explicitly supplies no production C2 gateway; `Client/Modules/WorkQueueCanonicalSourceEvidence.fs:38-41` has only IncompleteCoverage. A bounded search across Server `*.fs` found only the authority interface declaration.
- `Server/WorkSqliteAuthority.fs:157-187` accepts nonnegative versions, JSON-object bodies and syntactic optional SHA values, not authenticated origin; `Server/WorkLegacyImportProjection.fs:418-432` rejects nonempty legacy launches in normalized C1; `Server/WorkLegacyImportSqlite.fs:201-260` corroborates Stage/Launch/Role/Review/Integration/Work original row writers. `Server/WorkSchedulerSqliteDispatch.fs:374-450` confirms Prepared->Unknown effect journal controls.
- `Server/WorkRecoverySqlite.fs:168-177` embeds raw original stage BodyJson in Recovery effect records; `Server/WorkSqliteBackup.fs:103-151` only accepts <=128 safe IDs and narrowly whitelisted flat bodies while B admits <=256 IDs. The proposed typed/inverse F1 and secret-exclusion gates are therefore necessary.
- Canonical ledger overview independently confirms B v2 review `7cb92fd97b61432d8fae482b70c0f15e` is ChangesRequested, decision 6473. The design correctly does not promote that candidate to authenticated ingress approval.

## Verdict boundary
**Accepted for this narrowly scoped one-document, source-grounded dependency analysis.** No blocking correction identified. Its hard NO-GO for original all-client writer exclusion, unauthenticated B ingress and missing full F/G projection/restore is correctly explicit. The owner plan is a proposal, not an assignment or authority to edit another lane. No build/native tests were run in this docs-only review; only the Git diff checks above executed. This verdict does **not** approve B/C/E/F/G implementation, source auth, live Git/SQLite cutover, deployment, Scheduler activation or operator credential/rule changes.