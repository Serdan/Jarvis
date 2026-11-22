# Independent evidence-only review — W27 Client Release witness

**Proposed verdict: Accepted, with explicit non-execution/reporting caveat.** This accepts the accuracy and scope of the original terminal witness, **not** all 1,405 tests as passed and **not** an integration, deployment, production, B2 or cutover gate.

## Source pin and scope

- Claimed review `c0f827a36f154f5b9c31a33e249d93e6` for task `automation-client-release-frozen-serial-175b6fb7-20261122`; reviewed HEAD `175b6fb709906a941c2f4f31b226d0487b0a652b`, exact parent `d312f1cc83685e781ce69e57ad0d4bf613bfb843`, tree `de4952d06892909776bce6b0722301b3ad558aee`.
- Independently inspected source checkout `/home/serdan/Projects/.Jarvis-worktrees/w27-full-client-release-175b6fb7-20261122`: HEAD/parent/tree match; `git status --porcelain` empty. Diff to direct parent changes **only** three expected `Client.Tests/` fixture files (57 insertions, 3 deletions): `WorkQueueCanonicalMigrationPrepareTests.fs` blob `b88e1de18e1aec5f45e09896b3d199ca814cd56b`; `WorkQueueMultiLedgerPreparedRecoveryTests.fs` blob `66687b88a9a3d43ea0fb01ba36273bb83b52630d`; `WorkQueueProjectMigrationPrepareTests.fs` blob `d288642e7d9559b2bbdedf58e65802ee0e48d07e`. The fixture-only delta was separately Accepted at decision 11940.
- Exact `Client.Tests/Client.Tests.fsproj` SHA-256 `fb04d24d3d6927dfed7333d315d41ac2141b8ae7afb6010fb7ac8b8014a9fbd7`. Manifest records SDK `11.0.100`, .NET11, `Release`, and `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --logger trx;LogFileName=w27-full-client-release-175b6fb7.trx --results-directory TRX --verbosity normal` with **no filter**.

## One original native terminal process

- Jarvis managed job `799bb8278ed54bdca63434d3bedb1244`: independently queried **Completed exitCode 0**, events sequence 1–7, untruncated; native PID `1134489`, test started `2026-11-22T18:57:07Z`, terminal `19:26:42Z`, reader/native exit 0. Provenance start `18:56:43Z`, after private package-cache seeding. No retry, second job, source edit, or test run was performed by this reviewer.
- Evidence root `/home/serdan/Projects/.Jarvis-worktrees/.w27-client-release-evidence-175b6fb7-20261122`; independent file inspection verified:
  - `full-client-release-console.log` SHA-256 `f805dc5716d98fa2653bf733ff44b883dd72a83065d74e0678f231df7c671075`, size **399,505 bytes**.
  - `trx/w27-full-client-release-175b6fb7.trx` SHA-256 `bc3fa06440b993258c8ac5b8224f1f480a7053f10aec3a2735ef4f22ba8f1718`, size **1,958,795 bytes**, unique TRX under results root; XML parsed in full.
  - `provenance.txt` SHA-256 `e1d81c0862e5c1381bf268d379fbc39ea8e4a1e7ed0b54cc2a756f052e3ddb5d` with exact source/tree, command, environment and output paths.
- Manifest says `umask=0077`; independent filesystem inspection showed private evidence root, HOME/TMPDIR/XDG/cache/state/run, HTTP NuGet cache, CLI home and TRX directory all mode `0700`. NuGet package directory is `0755` **inside** private `0700` root. Source worktree and scratch/output roots are distinct, and source remained clean; bin/obj are under private source checkout. Retrospective absence of *all* unmanaged processes cannot be proven solely from a manifest, but no cross-root contamination was observed.

## Complete raw TRX vs NUnit console reporting — important caveat

- Parsed **1,405** `UnitTestResult` elements, each with unique test ID, execution ID and name: **1,403 Passed; 0 Failed; 2 NotExecuted**. `TestDefinitions` count 1,405 and `ResultSummary outcome=Completed`. The TRX summary counters: `total=1405`, `executed=1403`, `passed=1403`, `failed=0`, **`notExecuted=0`**. The latter counter contradicts the **two individual** `outcome=NotExecuted` entries and must **not** erase them.
- `restoredProductionScheduleStatePollsWithoutFaults`: raw TRX `NotExecuted`, message **“Explicit production schedule fixture not supplied”**. Source `Client.Tests/BrowserSchedulesTests.fs:147–150` calls `Assert.Ignore` when the required explicit `JARVIS_SCHEDULE_FIXTURE_PATH` is absent/nonexistent. It is **not** a passing production-schedule test.
- `ThirtySamplesPerMutationProduceMachineLocalStageDistributions`: raw TRX `NotExecuted`, message **“30+ non-gating distribution: run deliberately on same machine for before/after comparison”**. Source `Client.Tests/LedgerMutationStagePerfTests.fs:210–211` marks it `[<Test; Explicit(...)>]`; this opt-in performance distribution test was deliberately not run.
- Console says `NUnit3TestExecutor discovered 1404 of 1405 NUnit test cases ... Non-Explicit run`, `Test Run Successful`, **“Total tests: 1404; Passed: 1403; Skipped: 1”**. It also prints **both** skipped tests. This is a reporting/discovery boundary: console count 1,404 vs raw TRX total 1,405; console skipped 1 vs raw individual NotExecuted 2; TRX `notExecuted` counter 0 vs actual 2. Preserve each as emitted rather than assert `1405/1405 PASS`. No raw `Failed` outcomes were observed.
- Compiler emitted four warnings, zero errors; no claim of zero warnings or zero skips is warranted.

## Negative-space / authority limits

The older `d312f1cc` full Client witness (job `1ede7bb2cd0d4a059a8e05de8eaab503`, RED exit1) remains a different frozen source and result; this new witness does not overwrite or reclassify it. This is a **single unfiltered .NET11 Client.Tests Release pass for 1,403 executed cases**, with two intentional nonexecutions. It does **not** prove a separate Server suite, live production schedule fixture, 30-sample distribution, Health → Challenges → Scheduler E2E, live side effects, production B2, main integration/publication, unattended scheduling, deployment or cutover. None is authorized by this test-only GO.

**Finding:** No evidence-bound violation of the original one-run, source-pinned Release witness contract. The two nonexecutions and discrepancies are documented as scope limitations, not silently passed tests or a demand to expand the original gate. **Accept the evidence-only witness with these exact limits.**
