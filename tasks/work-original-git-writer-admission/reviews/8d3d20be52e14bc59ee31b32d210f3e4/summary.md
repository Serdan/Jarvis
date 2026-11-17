# Reviewer 2 — ChangesRequested

**Frozen independent review:** `8d3d20be52e14bc59ee31b32d210f3e4`, head `10df861cf096cf5b4733fb1178ebe5000ccb697b`, base `35f675cc1564710d10fdb2f50a03edff39368a28`. No source changes or integration by reviewer.

## Verified correction

The author introduced a durable protected `original-git-writer-host-state` **outside** the removable binding root, with journal identity, STOP state, epoch and a STOP-time high-water mark. A post-STOP same-UUID SQLite rollback no longer silently opens admission; removal of the binding directory alone leaves the sibling host state and denies publication. The new test cases assert the original project/account remote refs remain unchanged. The claimed project/account initialization and source/ref publisher paths call `withPublication`; composition mutations use `LedgerStore.transact`.

**Independently executed** on the clean exact frozen worktree: `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~LegacyWriterProtocolFenceTests --no-restore --verbosity minimal` passed **21/21**, no failures or skipped tests, .NET 11 Release (compiler warnings outside this correction). `git diff --check 35f675cc..10df861c` exited 0. This does not establish full suite, independent process-restart/host-loss, or migration acceptance.

## Blocking P1 finding

See `findings/pre-stop-journal-rollback.md`. The independent host authority is initialized `Open / 1 / 0` and is not advanced on admission or settlement. Its high-water is first written by `stop` using the then-present SQLite row count. Rolling the SQLite journal back to a same-UUID **pre-admission** snapshot *before* STOP erases a successfully published but UNKNOWN remote effect. STOP then accepts the stale journal, writes its older high-water, and reports a falsely drained witness. The current negative tests roll back **after** STOP and do not cover this failure. This is a source-established scenario, not a dynamically reproduced exploit.

**Verdict: ChangesRequested.** Make independently durable admission/effect history monotonic before irreversible publication, reject stale restored journals at STOP/inspect, and add a real bare-remote lost-ack plus pre-STOP restore regression. `RouteCoverageProven=false` remains correct. No GO for import/cutover, concurrent old/new writers, production deploy/restart or unattended Scheduler; no integration authorization implied by passing focused tests.