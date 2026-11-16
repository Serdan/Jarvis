# Reviewer 1 — WQ-08 default-off Scheduler host preview: Accept

**Frozen source:** `bde6d12676739055b571ce5bccc1a36ca5ebed54`, base `289e32754b6e2bd59b157d51c3fd79a16e881e98`. Six changed files (662 added lines), including `Client/Modules/WorkQueueSchedulerHostPreview.fs`, standalone client CLI branch, six tests and operator documentation. No production Scheduler file was edited.

## Independent verification

- Read the complete new preview, its test fixture and operator guide; checked the CLI dispatch in `Client/Program.fs` and the linked existing WQ08 prototype project entries.
- `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore`: **pass**, 0 errors and 3 existing F# warnings.
- `dotnet fsi scripts/test-client-hermetic.fsx -- --no-build --filter FullyQualifiedName~WorkQueueSchedulerHostPreviewTests --timeout-seconds 180`: **6/6 pass**, 0 failed/skipped, disposable profile cleaned.
- Tests show read-only cap=1 per-project preview, full-capacity visibility, immutable origin SHA/ref/operation reporting, unknown original browser identity, remote read errors as actionable unknowns, and no provider session-key leak. Worker 1 additionally reported **12/12** existing Server WQ08 fake-browser tests; not independently rerun here.

## Acceptance boundary and follow-up

The explicit `scheduler preview PROJECT_ROOT ACCOUNT_BINDING_JSON ROLE_ID` invokes read-only account and project Git readers, pinned role demand and source/admission inspection. It never installs a timer/automatic host, reserves a capacity token, emits a browser submission, generates an executable launch ID, or alters `canDispatch`. It always reports `Mode=ReadOnlyDryRun`, `CanDispatch=false` and `DispatchDisabled`. Unknown/missing journal evidence is reported, not interpreted as authorization to retry.

The production `ReadOriginal` browser-journal port intentionally remains **unbound** and returns `BrowserJournalUnknown`; supported positive tests are injected reader fixtures, not an end-to-end live host/Scheduler dispatch. Account and project views are independently fetched and not transactionally joined, and selection is one role/one project rather than full cross-project ranking. These limitations are explicitly documented and should remain release gates rather than being represented as completed integration. Worker 4’s independently accepted producer entrypoint and other WQ03/WQ06 bridges remain separate upstream requirements. No operator cutover is authorized.

**Verdict: Accept** for this narrow default-off, observable host preview and documentation. Integration only of exact frozen source; production activation separately operator-approved.
