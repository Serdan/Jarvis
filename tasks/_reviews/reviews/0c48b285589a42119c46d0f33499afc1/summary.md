# Independent review: Accepted (merge integrity and evidence-only scope)

**Frozen reviewed commit:** `3aac4f66559d1ebee029716d18095c56b4c180dd`
**Explicit pre-push baseline:** `a83549a6897a4e26aa4834ff81dc38b6e16ee85e`
**Review request:** `0c48b285589a42119c46d0f33499afc1`
**Reviewer:** Reviewer 3

## Source and merge assessment

- Reviewed all six changed paths against the **explicit** baseline: `Client.Tests/LegacyWriterProtocolFenceTests.fs`, `Client/Modules/LedgerStore.fs`, `Client/Modules/ReviewWorkflow.fs`, `Server.Tests/OriginalGitSourceObjectHandoffTests.fs`, `Server.Tests/Server.Tests.fsproj`, and `docs/design/work-original-git-object-handoff-evidence-2026-11-19.md`.
- First merge `b4be0410b10dc684c2bef85a30c4d106a7080764` has parents `a83549a6897a4e26aa4834ff81dc38b6e16ee85e` and `d4ec68c5c9fd167ef53ee24928af8f93a19768fa`. Independently recomputed `git merge-tree --write-tree` tree `4384d8080485c3a404c4585983b18b03e30503d1`, exactly the recorded first merge tree.
- Second merge `3aac4f66559d1ebee029716d18095c56b4c180dd` has parents `b4be0410b10dc684c2bef85a30c4d106a7080764` and `9fb2a5b5b54b843b074f6e6bc8c277981f988bc0`. Independently recomputed merge tree `eb7a56b4da825b8cb548769e2de11ea8a157142b`, exactly the recorded final tree. Neither merge adds manual resolution semantics or silently drops a parent's contribution.
- The client stage ties the parent action UUID and canonical request hash to a separately durable first ref/OID physical stage, guards preexisting refs, ack-loss ambiguity, replay and STOP; default-off compatibility remains. The W7 tests and design document explicitly distinguish disposable Git object-transfer proof from authenticated production source/publisher authority.

## Independently executed checks

- Clean exact-commit worktree: `integration/work-c-stage-object-integrator-20261119`, HEAD `3aac4f66559d1ebee029716d18095c56b4c180dd`; status clean after tests.
- `git diff --check a83549a 3aac4f66`: PASS.
- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~LegacyWriterProtocolFenceTests`: **37 passed / 37**, exit 0. Includes client fault-injection and guarded delete cases.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~OriginalGitSourceObjectHandoffTests`: **3 passed / 3**, exit 0. Includes absent local-only object, independent disposable fetch, and wrong repository/ref/parent/stale-head cases.

## Verdict and limits

**Accepted for the requested six-file merge/evidence scope; no blocking finding.** This is not a G2/production C-to-B authority acceptance. An authenticated original object reader/publisher, enforcing remote effect receipts and STOP/high-water witness, unknown-push reconciliation and operator-approved migration/cutover remain explicitly unimplemented or unproved. Do not use these local fixture passes to authorize protected Git writes, deploy, live Scheduler or SQLite cutover. The review's automatic BaseCommit equals reviewed HEAD because origin/main already advanced; the correct explicit pre-push baseline above was used throughout.
