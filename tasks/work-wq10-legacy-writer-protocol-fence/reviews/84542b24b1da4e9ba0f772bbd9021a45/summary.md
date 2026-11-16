# Integration-resolution review — WQ10 legacy writer protocol fence

**Source:** `b10d836c7cdf233aff309d202fa6e8d27149a4b1` on `refs/heads/integration/legacy-writer-fence-main-worker4-20261116` (remote ref verified exact). **Requested independent verdict:** Accepted or ChangesRequested for the two-parent merge only. This is **DEFAULT-OFF**, **NOT production fence, Scheduler, Manager or cutover GO**.

## Exact ancestry and conflict
- First parent: `2ce8a258d8e5d1579bc005df978b235a3192aee9`, independently Accepted by Reviewer 3 in `4d1321a123bc47edb041daa94fb6a663`, decision sequence 5289 (acknowledged at seq 5299). Neither rebased nor rewritten.
- Second parent: exact previously inspected remote main `d0065952efc47789ef81a6bef056c515954ee52c`.
- Only merge conflict: `Client.Tests/Client.Tests.fsproj`. Retained both `LegacyWriterProtocolFenceTests.fs` and `WorkQueueProjectMigrationPrepareTests.fs` compile includes. Relative to main the resolved file adds exactly the fence test include; relative to accepted source it adds the main-side Prepared and native credential adapter test includes. `Client/Client.fsproj` auto-merged, retaining `LegacyWriterProtocolFence.fs`, `WorkQueueProjectMigrationPrepare.fs` and `WorkQueueNativeCredentialDispatchAdapter.fs` in F# compile order.
- No hand-edits to Worker 8 permissions, Worker 12 composition or Worker 10 WQ10 source. Clean merged worktree; `git diff --cached --check` before commit and `git diff --check HEAD^1 HEAD` after commit both exit 0; `git rev-list --parents -n 1 HEAD` verifies exactly two parents.

## Local .NET 11.0.100 Release validation
- Focused `Client.Tests` fence/SignalR/dispatch/WQ10 Prepared/native credential filter, `--no-build --no-restore`: **31/31 passed**, 0 failed, 0 skipped (31 s).
- Full `Server.Tests`: **294/294 passed**, 0 failed (10 s); existing F# compiler warnings, no failures.
- Standalone `Client.WorkNoAgentFixtures`: **4/4 passed**, 0 failed (16 s); no persistent Agent lifecycle fixture preserved.
- **Full unfiltered Client.Tests is not a pass:** bounded `--no-build --no-restore` invocation reached its 180-second timeout (exit -1, TimedOut true) without an NUnit completion summary. Output included `Jarvis activity persistence` agent-seen read/save/append failures (`InvalidDataException`, `UnauthorizedAccessException`) and disk/permission diagnostic advice. This may be a test-environment or unrelated full-suite issue; no unsupported causal conclusion. It needs separate investigation if a full-suite gate is required.

## Evidence boundary
Accepted original Reviewer 3 scope remains the narrow host-selected authenticated command-path default-off fence. Direct `ReviewWorkflow`/`ChallengeWorkflow`/`RoleInstructions`/`LedgerStore.transact` writes, old/foreign clients, external Git writers, previously scheduled/rearmed actions, cross-ledger check-to-CAS races, trusted Freeze signer, minimum fleet protocol witness, original writer high-water and complete operator repair/cutover gates remain **NO-GO**. This merge does not activate any fence, Scheduler or migration cutover.
