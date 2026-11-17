# Worker 20: source provenance in B (isolated implementation evidence)

**Task:** `work-trusted-sqlite-domain-provenance`, attempt `51c706d37c504eb6a356e2ef6369ebd5`, generation 1.
**Base:** exact clean `origin/main 51b67bf1d5ba7e16512b915f8004456440b3489c`.
**Branch:** `feat/work-sqlite-domain-provenance-w20-20261117`.
**Review boundary:** **PARTIAL, NOT production-ready.** The accepted design `docs/specs/work-sqlite-domain-backup-contract-2026-11-17.md` authorizes no live migration, host binding, Scheduler or F1/G1b promotion.

## Source decisions

1. **An explicit v1 -> v2 SQLite migration, never a silent reinterpretation.** Original repository ID and original full Git ref cannot be inferred from B `source_sha` or the Git *backup* repository. V2 adds `source_repository_id`, `source_ref`, `source_verification_receipt_id`, `source_evidence_sha256` to each `work_records` row. Existing `source_sha` remains the exact commit and legacy rows remain unchanged. A legacy row with `source_sha` but no original source columns is **untrusted**, including after upgrade. The new `ReadSourceProvenance` refuses such rows and partial evidence; it cannot manufacture a witness.
2. **No implicit upgrade and no new authority.** `Initialize()` still makes v1 by default. Explicit `InitializeProvenanceV2()` checks JWRK application identity, user/meta schema consistency, exact physical named column/type/NOT NULL/primary-key layouts, complete canonical SQLite table/index catalogs (including enforced CHECK/UNIQUE/references), and schema metadata before returning healthy v1/v2 or upgrading. A same-count foreign schema fails closed. The v1-to-v2 upgrade is additive and transactional. Existing B receipt/sequence/audit transaction ordering remains unchanged. The old `Commit` is valid only for v1. An internal v2 physical adapter couples recorded original source evidence with the operation fingerprint and original row CAS guards. Changing repo/ref/evidence with a replayed operation ID cannot replay as the original; a v2 source-bearing to source-less UPDATE atomically clears all five related fields.
3. **A physical provenance tuple is not an authentication proof.** `WorkSqliteSourceProvenance` records a claimed source and a referenced original-verification receipt; the B store does **not** itself independently fetch/verify canonical Git, immutable source, role approval, authenticated producer identity or scheduled credential scope. Tests which construct this tuple are explicitly **physical schema** tests, not authority tests. There is no live host registration for this adapter. F1/G1b must not treat the tuple alone or `ReadSourceProvenance` success as complete trusted provenance. An independently verified original-producer boundary must be installed and tested first.

## Blocked domain and producer obligations (not implemented in this lane)

**B-domain ingress is still NO-GO.** The isolated v2 physical adapter accepts `WorkSqliteRecordValue.BodyJson`; it does not parse a complete closed DTO for all eight kinds, including C1 original legacy payloads, E0 dynamic scheduler bodies and G1 recovery `originalStages.bodyJson`. It does not prove that arbitrary otherwise-valid strings originated from a non-credential source. The actual original Git verifier, role/decision/claim evidence producer, hash-only scheduled verifier binding, and the G1 trusted sealed-stage representation are owned by other lanes. Do **not** publish complete F1 export or authorize any v2 writer from this physical adapter.

Precise dependency for Lead/Manager backlog admission: **proposed** `work-trusted-domain-original-producer-adapters`, independently source-reviewed, covering C1's `IWorkLegacyProjectionVerifier`, verified original `RepositoryId/SourceRef/SourceCommit`, E0 roles/claims/launches, G1 recovery immutable stage facts, and source-bound scheduled verifier authorization. This task must define the eight closed canonical per-kind body versions, authenticated type constructors, negative duplicate/unknown/extra field rejection, same-transaction CAS/role/source/STOP/effect cross-record checks, and no dispatch bearer/manual-grant path into B. Source ownership should be assigned by Manager, not patched here. Even successful source/body mapping still needs the separate F1 canonical export/inverse and G1b persistent boot/recovery fence and full two-project stale-restore independent acceptance.

**Known additional F1 parity blocker:** B `boundedId` admits non-control trimmed IDs up to 256 chars; current F1 `safeId` admits a different and narrower vocabulary/length (max 128). A B-admissible key must not be silently dropped. Worker13's F1 export/inverse must match exact B IDs or obtain an explicitly reviewed B producer-side narrowing migration, not filter rows. Source SHA alone cannot authenticate repo/ref, and B lacks a per-row last-write transaction sequence, so neither should be synthesized.

**Future negative tests required before trusted ingress is accepted:** all eight real C1/E0/G1 body variants and releases; nested/duplicate/extra/future JSON keys; bearer injected via allowed `state`/`status`, identifier/ref, escaped Unicode, nested recovery originals or fake manual-grant fields; verifier binding to reservation/account/source/role/STOP/permissions; missing/changed source repo/ref/evidence receipt, incomplete account project set; exact review/integration and Prepared/Unknown one-shot effects. Failure or missing producer authority must block the whole typed export, not redact silently. These full-domain tests are **not** claimed by this partial source slice.

## Evidence

Isolated `.NET 11 Release` compilation succeeded after project-local restore. `Server.Tests/WorkSqliteDomainTests.fs` covers explicit migration, eight physical record tags on two projects with shared capacity and replay, bad/missing repo/ref/commit/evidence inputs, raw-v2 denial, duplicate JSON property rejection (including nested objects/arrays), and partial schema upgrade failure. In direct response to independent review `b27b2c5fd7f448cf978cf52aa7f1a640` (ChangesRequested at sequence 6330), four additional tests cover source-bearing to source-less v2 update clearing, exact restart/receipt replay, same-count wrong-name v1 upgrade rejection, fabricated same-count v2 layout rejection after restart, and missing v2 index rejection. **First-correction Release results (reviewed at 60df897d):** focused `FullyQualifiedName~WorkSqliteDomainTests` 9 passed/0 failed/0 skipped; full `Server.Tests` Release 497 passed/0 failed/0 skipped. These are physical B tests, not authenticated eight-kind ingress proof. All checks use disposable local SQLite fixtures; nothing was migrated on a live host. No F1 export, recovery fencing, Scheduler, external effect, host route, deployment or cutover was executed.


### Second independent ChangesRequested correction: commented-out constraint bypass

Reviewer 4's frozen review 5b6bb2b1c6d64e45b51a77a5f1017a5f of exact
60df897dba87b42440420b69cbdcd0c7f1706360 (decision sequence 6377)
established that sqlite_master.sql kept CHECK(json_valid(body_json)) as
literal text even when the SQLite CHECK itself was replaced by a block comment.
The old fragment-presence validator therefore accepted physically non-enforcing
v1 and v2 layouts. This review has been acknowledged; it was **not** an approval.

**Red-first:** two new NUnit cases build disposable v1/v2 authorities,
commit one record and receipt, replace exactly the CHECK with a SQL comment in
sqlite_master, and reload the altered schema. An invalid JSON UPDATE
**succeeds** in a new connection (rolled back), proving the constraint is
actually absent; before the source fix Initialize() returned Ok 1 and
Ok 2, respectively. Both cases failed as expected.

**Corrected implementation:** the original v1 CREATE/metadata and explicit v2
ALTER/metadata SQL are shared single-source constants. A lazy, SQLite-executed
in-memory reference builds complete canonical v1 and v2 sqlite_master
catalogs. physicalSchemaMatches checks exact normalized catalog objects and
full DDL (tables, indices, autoindices, constraints and references), together
with existing named column/type/NOT NULL/PK and work_meta checks. Full-catalog
equality rejects comments, extra schema objects and noncanonical constraints;
a displayed substring can no longer impersonate enforcement. No unrelated
producer, F1, G1b or host file was changed.

**Independent-review candidate (not yet approved):** .NET 11 Release focused
WorkSqliteDomainTests **11 passed / 0 failed / 0 skipped**; full
Server.Tests **499 passed / 0 failed / 0 skipped**; git diff --check
exit 0. Both new cases verify ForeignDatabase from Initialize() and
InitializeProvenanceV2() without modifying SQLite user/meta schema version,
last sequence or receipts. This is still a **physical v2 partial B slice only**:
no authenticated eight-kind typed ingress, production writer authorization,
F1 backup/inverse, G1b restore/recovery, deployment or cutover approval.
