# Worker 15 — trusted source ingress negative boundary (2026-11-18)

**Disposition: HOLD for authenticated/positive ingress, default-off source-only negative proof eligible for review.** No original Git -> SQLite production activation, live publisher registration, Scheduler, hosted action, deployment, credential change or permission to commit raw caller bodies was added.

## Immutable provenance

- Verified remote `origin/main`: `aa2e46ff2927b8e9fd0af25204377c299f2ec576` (read-only `git ls-remote`). Worker 15 created the clean isolated worktree `work-trusted-source-ingress-worker15-20261118` at exactly this base. Shared `main` is divergent and was not edited.
- Accepted source design/matrix: `docs/design/work-trusted-domain-original-producer-adapters-2026-11-17.md` at `09b739e32b928e8a2014e93032c65edacea55526`, independent Accepted review `58a4fa7593d749948cbc6fa14f9e5b9c` decision 6531. At the time of this check there was **no integration submission** for task `work-trusted-domain-original-producer-adapters`; Lead/Integrator owns that separate handoff.
- Independently Accepted **physical B v2 only**: review `5f4fd7a89b2143b298873ce98e6fbf50` decision 6504, frozen source `5d872d41ba7259bc84ebafebdf9e5d83e52353d3`. `git merge-base --is-ancestor` returned false against `origin/main`. The review purpose expressly excludes authenticated ingress and live cutover; its positive physical-schema verdict cannot be upgraded into source authorization.
- Current origin/main `Server/WorkSqliteAuthority.fs` schema version 1 accepts arbitrary JSON-object `BodyJson` and syntactically valid Git-shaped SHA without independently authenticated repository/ref/producer witness. The file's local CAS/operation receipts are valuable but not original source authentication.
- `Server/OriginalGitPublisherAdmission.fs:6-9,78-92` defines `IOriginalGitPublisherAuthority` without production implementation/registration. It cannot yet enforce all-client/credential/delayed-writer exclusion.
- `Server/WorkLegacyImportProjection.fs:10-13,49-56` documents `IWorkLegacyProjectionVerifier` as unregistered; a synthetic fixture cannot prove original source identity. Current E1 dispatch and G1 Recovery are default-off/incomplete for production source proof.

## Additive bounded proof

New `Server/WorkTrustedSourceIngress.fs` and `Server.Tests/WorkTrustedSourceIngressTests.fs` (plus only their F# compile inclusions) establish an internal, default-off, **negative-only** entrypoint `WorkTrustedSourceIngress.inspectUntrusted`. It parses a v1 *claim envelope* (not B physical schema v1) into explicitly `Rejected`, `Incomplete` or `UncertainEffect`. Its closed eight-kind mapping reuses `WorkSqliteRecordKind`; the parser validates exact claim fields, canonical account/project UUIDs, repository/ref/commit syntax, bounded IDs, row/graph/stage/claim/STOP/role generation shapes, duplicate JSON properties recursively, and hard Prepared/Unknown handling. Secret-shaped system fields are rejected as defense in depth; since the ingress **never constructs a WorkSqlite mutation or receipt**, it does not rely on those key names to assert credential safety.

A valid-looking claim (including a SHA, matching project IDs, original operation UUID and review/integration-shaped body) is still `Incomplete`: no caller-supplied string or `verified` boolean becomes trusted. Neither body nor raw source assertion is echoed into a durable value/error. Original immutable evidence must remain under its original source/receipt identity and needs a separately authenticated loader; we do not create a second database or producer graph.

This is deliberately **not** the complete per-kind producer decoder, physical B v2 migrator, authenticated publisher, F1 typed inverse, or G1b boot fence. No positive production witness or safe complete backup can yet be asserted. Tests may establish negative safety, not full GO.

## Source-contract next owners

1. **Worker 17 / C2 original source provider:** produce authentic account+project+ledger+epoch+device/repo/ref/commit/operation/result receipts with independent remote inventory and all-client writer exclusion, including exact review/integration/role/STOP and capacity scope. `IWorkLegacyProjectionVerifier` must cease being fixture-only before positive ingress.
2. **Worker 20 / B v2 owner:** integrate independently accepted physical-catalog correction, then add closed per-kind writer schemas, independently sourced provenance and migration semantics. Coordinate a stable trusted domain mutation constructor without changing Worker 15's leased files.
3. **E/G/F owners:** guard Scheduler/Recovery/backup outputs with the accepted B+C trusted contract and persistent prepared/UNKNOWN fencing. E1's source-shaped strings and G1's nested `originalStages.bodyJson` are not proof or credential-safe F export.
4. **Lead/Integrator:** reconcile the missing integration submission for the accepted producer-matrix document `09b739e...` before claiming that design source is integrated.

**GO/HOLD handoff:** GO only for independent review of the additive negative-space boundary and tests. **HOLD** positive/authenticated ingress, live writer cutover, all-kind typed roundtrip and any production claims pending exact independently verified upstream source and schema changes plus separate operator authorization.

## Test evidence

- `.NET 11 Release` red-first compilation: expected missing `WorkTrustedSourceIngress` module/type errors before implementation.
- `.NET 11 Release` focused after implementation: **17 passed, 0 failed, 0 skipped** (`WorkTrustedSourceIngressTests`).
- `.NET 11 Release` adjacent B/C/E/G and original publisher: **74 passed, 0 failed, 0 skipped**.
- `.NET 11 Release` full `Server.Tests` regression: **505 passed, 0 failed, 0 skipped**.
- Git diff whitespace/conflict-marker check and exact source head: to be recorded after final commit.
