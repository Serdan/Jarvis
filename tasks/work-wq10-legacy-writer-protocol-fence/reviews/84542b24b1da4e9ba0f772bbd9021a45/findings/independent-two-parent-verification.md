# Independent Reviewer 3 evidence — WQ10 legacy-writer fence two-parent merge

**Disposition: Accepted for this exact integration-resolution merge only. Production migration, fleet-wide writer fence, Scheduler, Manager and cutover remain NO-GO.**

Review: `84542b24b1da4e9ba0f772bbd9021a45`. Frozen commit: `b10d836c7cdf233aff309d202fa6e8d27149a4b1`. Reviewed clean registered worktree `legacy-writer-fence-main-resolution-w4-20261116`, source ref `refs/heads/integration/legacy-writer-fence-main-worker4-20261116`. No source edits by reviewer.

## Exact ancestry and conflict resolution

- `git show -s --format=%H%n%P%n%s b10d836c` verifies exactly two parents: first `2ce8a258d8e5d1579bc005df978b235a3192aee9` (independently Accepted under review `4d1321a123bc47edb041daa94fb6a663`, verdict seq 5289) and second `d0065952efc47789ef81a6bef056c515954ee52c` (current-main source).
- `git show --format= --remerge-diff --stat b10d836c` and the targeted remerge diff isolate the sole manual conflict to `Client.Tests/Client.Tests.fsproj`. The conflict was two adjacent F# Compile entries, and the resolution retains BOTH `LegacyWriterProtocolFenceTests.fs` and `WorkQueueProjectMigrationPrepareTests.fs`; `WorkQueueNativeCredentialDispatchAdapterTests.fs` remains included later in the project. No leftover conflict markers.
- Parent comparisons: `git diff --exit-code b10d836^1 b10d836` restricted to accepted fence module, SignalR hooks, fence tests and fence docs exits 0, confirming these source files are byte-for-byte preserved. `git diff --exit-code b10d836^2 b10d836` restricted to main's native credential adapter/launch intent, project Prepared module and tests, and independent Agent-free fixture project/test exits 0. In `Client/Client.fsproj` the auto-merged F# compile order contains the legacy fence, project Prepared and native credential adapter.
- Diff against the main parent adds exactly the approved legacy fence module, SignalR check and tests/docs. The SignalR checks are before implicit Ready->Working and before common command dispatch; no new dispatch mechanism or production activation was introduced by resolution.
- `git diff --check b10d836^1 b10d836` and `git diff --check b10d836^2 b10d836` both exit 0; final `get_git_status` reports only the expected integration branch with no dirty files.

## Independently executed local .NET 11 Release evidence

1. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter 'FullyQualifiedName~LegacyWriterProtocolFenceTests|FullyQualifiedName~SignalRDispatchTests|FullyQualifiedName~OrchestrationApiTests|FullyQualifiedName~WorkQueueProjectMigrationPrepareTests|FullyQualifiedName~WorkQueueNativeCredentialDispatchAdapterTests'`: **28/28 pass**, 0 fail, 0 skip; full Release Client compile succeeds. The SignalRDispatchTests filter by itself has no test cases, so it is not counted as a separate fixture.
2. `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~DispatchProbeTests|Name~SignalR'`: **17/17 pass**. Separate validated `FullyQualifiedName~DispatchProbeTests` filter: **13/13 pass**.
3. Separate `FullyQualifiedName~LegacyWriterProtocolFenceTests`: **5/5 pass**. This explicitly covers two disposable Git account/project remotes and two clones, default-off / LegacyActive, Frozen/Preparing negatives, stale heads and host binding fail-closed outcomes.
4. `dotnet test Client.WorkNoAgentFixtures/Client.WorkNoAgentFixtures.fsproj -c Release --no-restore`: **4/4 pass**, 0 fail, 0 skip. Test-only no-persistent-Agent lifecycle is preserved.
5. `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore`: **294/294 pass**, 0 fail, 0 skip; full Server Release test suite.
These results overlap; do not add their counts to claim unique case coverage. Existing F# compiler warnings were emitted; no compilation/test failure. Submitter's unfiltered full Client run timed out without NUnit completion; it is NOT a pass, was not independently rerun, and should not be silently treated as an acceptance gate.

## Scope limitations retained from previous Accepted source

The guard is **DEFAULT-OFF** and proves only the tested authenticated SignalR/client dispatch boundary. Direct `ReviewWorkflow`/`ChallengeWorkflow`/`RoleInstructions`/`LedgerStore.transact` library and external Git writers, older/disconnected clients, already-admitted writes, previously persisted Manager/Scheduler actions and background rearm, original writer high-water, authoritative Freeze signer, trusted host/fleet protocol witness and account/project cross-ledger CAS remain unproven. None was fixed by this compile-include merge. Work-native bootstrap/Prepared evidence does not itself grant cutover. No production enablement, migration, Scheduler/Manager launch, hosted CI or Codex occurred.

**Recommendation:** Accept the two-parent integration resolution at the exact frozen commit; Lead may integrate it under ordinary exact-source review rules, but keep production writer fence/migration/Scheduler/cutover NO-GO.
