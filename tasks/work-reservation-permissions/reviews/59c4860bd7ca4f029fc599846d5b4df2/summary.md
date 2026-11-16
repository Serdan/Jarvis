# Independent Reviewer 1 — ChangesRequested

Review: `work-reservation-permissions/59c4860bd7ca4f029fc599846d5b4df2`; frozen source `58d4223c6c5e0ff20e41d61c170a15d2ab668fac`; base `dfb73853bac06e3076a4e546517a2a640f493418`.

## Independent verification
- Read `AGENTS.md`, canonical task, exact frozen diff (five files: new `WorkQueueReservationPermissions.fs`, four NUnit tests, compile inclusions, scope document), WQ02 credential/CAS and WQ03 source checkout contracts.
- Private review checkout `review/reviewer1-wq11-permissions-58d4` stayed clean and exactly at pinned SHA. No main push, Scheduler/host activation, cutover, hosted Actions or Codex.
- SDK `11.0.100`. `dotnet restore Client.Tests/Client.Tests.fsproj --ignore-failed-sources` succeeded; `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore` succeeded (three preexisting warnings, zero errors).
- Actual NUnit execution, not just an MSBuild success: `dotnet vstest Client.Tests/bin/Release/net11.0/Client.Tests.dll /TestCaseFilter:FullyQualifiedName~WorkQueueReservationPermissionsTests` = **4/4 passed** (two independent real Git clones). Combined WQ02 Git/credential plus WQ11 fixture VSTest filter = **34/34 passed**, zero failed/skipped. An earlier `dotnet test --no-restore` without restored assets produced only a build success and is NOT counted as a test run.

## Correct scope guards observed
- `reviseAt` looks up the original credential verifier and admitted project witness in the original Git snapshot, binds the grant to account/project/launch/credited Work/stage generation, and publishes current revision + immutable revision history in a single LedgerStore Git CAS.
- `authorizeAt` checks SHA-256 bearer in constant time against the original credential verifier, current registered/non-stopped launch and lease, same project/Work/account/stage, grant revision/expiry/revocation and requested operation family. New and cross-project writes fail; cross-project reads are separately opt-in. No mutable caller role label supplies authority.
- Claim and read-only checkpoint fixture gates execute their checks inside the winning original CAS. Blocker proposals require Prerequisite family, Work owner, current graph version and evidence digest; they remain **ProposedOnlyNotRunnable**, with no graph or queue mutation.
- Serialization of reviewed mutation/grant/proposal intents includes IDs/digests but not raw launch bearer. `git grep` shows no call sites outside the new module and test suite, so current public tool paths, legacy repair, and default-off Scheduler are unchanged. An `operator:[hex]` actor string is NOT itself OAuth proof; an authenticated operator host adapter and trusted clock remain unimplemented.
- This is a **default-off internal authorization seam**, not source-job authorization, public Work-native enforcement, a working linked-prerequisite writer, or production permission isolation.

## Blocking finding
- **P1: Source authority is not pinned to the launch-generation source checkout.** See `findings/source-scope-pinning.md`. `SourceWorktree` is syntactically checked but not proven to belong to this Work/launch/claim generation; `SourceRef` records the destination `SourceLineage.TargetRef`, not the actual source continuation. String equality alone cannot authorize an actual source operation. The focused tests use Planning Work without source lineage, so this failure is not caught. Add source-stage positive/foreign-tree/branch/generation negative fixtures, and bind source identity from canonical provenance before live source operations.

## Additional acceptance/cutover limits (not treated as proof of current live bypass)
- `checkpointFixtureAt` uses `CheckpointReadOnly`; WQ02 explicitly rejects Implementation and Integration stages (`Client/Modules/WorkQueueGitLedger.fs:1262-1277`). The present successful checkpoint test is Planning Work, not a positive source-Implementation checkpoint. Actual source work and integration family authorization remain unproven.
- Blocker proposals are evidence-only, NOT linked Work. Canonical WQ04 graph/WQ02 handoff must still create/link a hard dependency atomically, with its own authority checks. Do not recursively work on unrelated backlog.
- No public MCP, source file/job tool, account-host role/approval bridge or live Scheduler is gated here. No cutover GO is implied by passing fixtures.

**Verdict:** ChangesRequested on exact frozen `58d4223`, because the source grant's core source/ref/worktree binding is not yet suitable as a security decision for the specified implementation/integration operation families. Preserve valid default-off credential/CAS work and submit a new exact frozen source after correction and two-clone tests.