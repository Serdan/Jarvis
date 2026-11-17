# Reviewer 4 — ChangesRequested

**Frozen review:** `d9c2e32d4e114e1f8fc2b8a9612e1021`, exact head `ab6ecdb0d952f348ecaea504c59a4ed0c875aba9`, base `35f675cc1564710d10fdb2f50a03edff39368a28`.

The 6-file diff introduces a default-off SQLite admission/attempt/settlement journal, instrumenting project ledger initialization/CAS, account ledger initialization/CAS, and immutable review/integration ref publication and guarded deletion. Static `git grep` traced current Work queue/composition mutations through `LedgerStore.transact`; other concrete push commands in Client/Modules are confined to `LedgerStore` and `AccountCapacityLedger`. The typed fence witness intentionally sets `RouteCoverageProven=false` and does **not** establish cutover eligibility. No source edits or merge were made.

**Independently executed:** `git diff --check <base> <head>` returned 0; .NET 11 Release `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~LegacyWriterProtocolFenceTests` at the exact frozen author's checkout passed **16/16**, zero failed, zero skipped. Other adjacent suites and actual restart/activation scenarios were not independently run here.

**Blocking findings:**
1. `findings/stop-binding-continuity.md` (P1): STOP is persisted in journal A, but missing environment configuration or a substitution journal after restart causes `withPublication` to take its default-off direct push path. This makes STOP non-monotonic across binding loss/changes, violating the intended durable admission boundary. Retain true default-off preactivation, but require an authenticated persistent activation/STOP binding and fail-closed handling after activation.
2. `findings/initialization-negative-gates.md` (P2): journal-enabled project/account initialization and binding-loss/UNKNOWN negative gates are not exercised; fixture initialization predates setting the journal environment variable. Add source-proven hermetic tests including post-STOP and lost-confirmation scenarios.

**Decision: ChangesRequested.** Do not infer SQLite migration GO, original-writer quiescence, source-effect completeness, live activation or deployment approval from passing focused tests. Resubmit a new frozen exact head after corrections.