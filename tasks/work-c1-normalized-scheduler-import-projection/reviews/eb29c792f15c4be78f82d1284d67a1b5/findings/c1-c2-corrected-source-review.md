# Reviewer 3 — corrective C1/C2 → E0/E1a normalized projection review

**Frozen candidate:** `3e795e008295f800a3010ca4bf67cb9a52be2977`. Prior ChangesRequested frozen `d6f49a6c8b29d645e4641a0595a704190e30e3ed`, base `2a51920bcedf3fed185a2956b7dc812474107881`.

**Independently checked:** `git diff --name-status d6f49a6..3e795e` reports precisely `Server/WorkLegacyImportProjection.fs` and `Server.Tests/WorkLegacyImportProjectionTests.fs`, both in authorized scope, no existing importer/Scheduler/Recovery/backup/client edits; `git diff --check` exit 0; clean registered Worker17 isolated worktree. I read both prior Reviewer2 blocking findings and inspected corrected exact source, original B SQLite and C1/C2 requirements.

## Prior blockers addressed

1. **Full C1 original source equality:** `OriginalC1.encode` re-encodes the complete typed F# source inspection including records, unions, arrays, maps, sets, fields and waiver receipt **values**; `originalRow` compares whole stored `WorkSqliteRecordValue` plus version 1 before projection, `originalOrProjected` accepts only exact original v1 or exact deterministic normalized v2. Project `legacy-queue`/`legacy-composition`, account `legacy-account` and receipts, stage + source origin, role subscriptions, retained original reviews/integrations/terminal/validation/operation receipts are all checked. Immutable rows are carried into `WorkSqliteTransactionCommand.Guards` in the same CAS as normalized authority/stage/role updates. This closes the prior name/key/case-only trust gap; note that the incoming `IWorkLegacyProjectionVerifier` must still be independently source-authentic in future production wiring.
2. **Complete C2 authenticated fence comparison:** `verifiedFence` checks account/project scope+epoch+ledger/source heads, device/decision/import UUID, exclusive receipt, original head/fence head and +1 high-water, writer generation, no-new-mutations/durability, original intent/effect high-water, minimum client protocol, all-client proof and audit digest. The retained `legacy-import-c2-authority-audit` is checked for exact outer identity/effect/schema and complete 19-field receipt equality with one-to-one unique entity coverage. The code still never treats synthetic disposable C2 receipts as live remote proof; no production gateway is registered.

## Regression evidence

- Focused independent `.NET 11 Release`: `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkLegacyImportProjection` **12 passed, 0 failed, 0 skipped**, exit 0.
- Entire independently run `Server.Tests` Release `--no-build --no-restore`: **451 passed, 0 failed, 0 skipped**, exit 0.
- New negative cases mutate C1 account/queue/graph/stage gate same-case, role duplicate, origin digest, retained receipt and **same-key changed waiver value**; C2 cases alter each previously missing high-water/protocol/decision/receipt/fence identity and persisted C2 audit source-effect entry; all require `Incomplete` without any SQLite projection commit. Existing positive two-project global cap-one C1→E0/E1a path and replay/source guard cases are retained.
- Source-whitespace gate passed; no live remote, hosted credential inspection, production Git writer quiescence, Scheduler dispatch or cutover was attempted.

## Boundary and decision

**Accepted for the default-off source and hermetic proof at this frozen commit.** This is not acceptance of `CaptureVerifier(Some proof)` as production authentication, any live C3 all-client fence receipt, real Git migration, authorization of newly imported sources/roles, source effect dispatch, Scheduler activation, deployment or operator cutover. Implementers must preserve fail-closed behavior until separately reviewed authenticated source/role/quiescence captures and cross-domain gateways exist.

**Nonblocking design observation:** complete reflection-based original C1 encoder is justified here by source-shape equality; if importer exposes a shared canonical typed digest/encoder later, prefer reuse with independent proof over divergent serializers. Do not weaken equality today.