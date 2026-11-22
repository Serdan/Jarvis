# Reviewer 1 — independent W27 full Client Release evidence audit

**Scope / intended verdict: Accepted as a faithful terminal RED witness, NOT as test PASS or release authorization.** Review request `9b40f0b7305944a6a60128f889c24db1` freezes source `d312f1cc83685e781ce69e57ad0d4bf613bfb843`. No source change, test rerun, publication, deployment, B2 activation, or cutover was performed.

## Independent source and process observations

- Current registered private test worktree `automation-client-release-frozen-serial-w27-20261122`: `git rev-parse HEAD` exactly `d312f1cc83685e781ce69e57ad0d4bf613bfb843`; porcelain status empty; non-symlink realpath; root `0700`. The separate W27 environment root is also non-symlink `0700`. Project `Client.Tests.fsproj` SHA-256 `fb04d24d3d6927dfed7333d315d41ac2141b8ae7afb6010fb7ac8b8014a9fbd7`.
- Jarvis-managed original test job `1ede7bb2cd0d4a059a8e05de8eaab503` is **Completed exit 1**, started `2026-11-22T17:22:06Z`, ended `17:56:19Z`, MSBuild elapsed `00:34:12.93`. Job metadata confirms `env -i` invoking `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release` with no test filter, `-m:1 -nr:false -p:BuildInParallel=false -p:UseSharedCompilation=false` and private TRX and logs. MSBuild reports SDK `11.0.100`; VSTest diagnostics show empty TestCaseFilter. No retry of this original job was made.
- Original job arguments isolate HOME, XDG cache/config/data/state, DOTNET_CLI_HOME, TMP/TMPDIR/TEMP, NuGet packages/HTTP/scratch, GIT_CONFIG_GLOBAL, and results/logs under the separate W27 private environment. Independent `realpath`/symlink inspection found each environment root inside that private directory and project `bin/obj` under the frozen worktree, not a shared checkout. NuGet restore source `/home/serdan/.nuget/packages` is a source for package installation into private `NUGET_PACKAGES`, not evidence of output-directory collision.
- The d312 Git commit is a merge with parents `7e3042e5e8b17cb1f7bd7c842d357c3925fed694` and `978932e536141f8b7c7c7f3c76a53ef313a6ab00`; no unexpected working-tree changes were present at inspection. This audit verifies the exact pinned HEAD but is not independent acceptance of its component source logic.

## Independent artifact check

- Native TRX `Client-Release-Full-W27.trx`, 2,045,040 bytes, SHA-256 `27260af5ff218e8b24716fee7d6e46fd0e87b7f508f876ad823f9bf531cad131`; independently parsed `Counters`: total **1,403**, executed **1,401**, passed **1,378**, failed **23**. Parsed 1,403 `UnitTestResult` entries: 1,378 Passed, 23 Failed, 2 NotExecuted.
- The two NotExecuted entries are `ThirtySamplesPerMutationProduceMachineLocalStageDistributions` and `restoredProductionScheduleStatePollsWithoutFaults`.
- Managed console instead reports **1,402 total; 1,378 passed; 23 failed; 1 skipped** and `Test Run Failed`. **Preserve the console/TRX count discrepancy; do not silently normalize it.** Both independently substantiate a failing, terminal run.
- Independently SHA-256-checked MSBuild full diagnostic log `45cd72746019f1d7bdae33e55e0ff2cd70900390e891347b783d7857dc1d3123` (20,633,802 bytes) and VSTest diagnostic log `26c8f068004f7ca9bb77954367252711ae03d334bbe0818edf64b0f52b2c0ade` (4,513,284 bytes). Both match W27's reported hashes.

## Failure boundaries

TRX test-ID to declared class mapping shows **9** failures in `WorkQueueCanonicalMigrationPrepareTests`, **9** in `WorkQueueMultiLedgerPreparedRecoveryTests`, and **5** in `WorkQueueProjectMigrationPrepareTests`. Immediate error texts comprise 5 explicit `WQ10 project Prepared: No approved private disposable fixture authority` InvalidBinding refusals, 5 `Disposable account Preparing refused: InvalidOperationException` observations, and 13 assertion mismatches. The WQ10 migration/fixture authority cluster is real; do **not** claim every assertion mismatch has been proven to share one cause.

**Disposition:** evidence audit **Accepted** because original terminal RED status, raw artifact provenance, exact source, environment boundaries, and limitations were independently verified and honestly recorded. The full Client Release gate is **RED**; no PASS, B2, authenticated E2E, integration, deploy, live scheduling, or cutover follows from this verdict. Defect triage/remediation needs a separate approved scope.
