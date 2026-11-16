# WQ11 frozen review — Accept (scoped F04/targeted fixture correction)

**Source commit:** `2b05a535a06255b97709b6d958e2624c3c8945d7`  
**Review ID:** `c39d9ac1b0c24587ab87b420d4ab7b11`  
**Scope:** F04 cleanup of early private-root acquisition failures, test progress observability, and narrow native Windows AgentInstructions symlink privilege handling. **Does NOT satisfy F03's full unfiltered Windows acceptance gate.**

## Independent evidence
- Inspected the exact `dfdebee6..2b05a535` source delta, including `WindowsDisposableGuard.acquirePrivateRoot`, parent FSI pre-try cleanup, synthetic post-create failure injection, and AgentInstructions Windows 1314 handling. Git diff --check exit 0.
- Exact frozen Linux worktree: `dotnet build Client.Tests/Client.Tests.fsproj -c Release --no-restore` exit 0, two existing warnings. Wrapped Linux focused `HermeticHarnessTests` exit 0: **9 passed, 3 Windows-only skipped**, child drained and scratch cleanup verified (job `708d53d608b441d4a5585e583fc9ca1b`).
- On connected **KRUSO-PF539JMB**, independently verified detached QA checkout `.wq11-frozen-2b05a535` HEAD exactly `2b05a535a06255b97709b6d958e2624c3c8945d7`, clean before/after.
- Native Windows F04 pre-testhost failure-injection command:
  `dotnet fsi scripts/test-client-hermetic.fsx -- --scratch-parent C:\Windows\Temp --self-test-after-root-creation`
  Expected **exit 1**, injected exception after protected root creation, explicit `scratch cleanup verified = true`; no testhost launch (job `5821375656d44515985bf6375eb80ddb`).
- Native Windows protected harness `--scratch-parent C:\Windows\Temp --no-build --filter FullyQualifiedName~HermeticHarnessTests --timeout-seconds 180 --self-test-git-sentinel`: **12/12 passed, exit 0, timeout false**, child drained, protected scratch cleanup true (job `328cc69c965e4361be981db95c4ddac7`).
- Same protected harness with `FullyQualifiedName~AgentInstructionsTests`: **32 passed, 2 documented skips (Windows symlink privilege and Unix-only fixture), 0 failed, exit 0**, child drained, scratch cleanup true (job `20e6568f695a4a749614d2f02d3cee3a`).
- No GitHub-hosted Actions, direct unguarded Windows Client.Tests invocation, deployment, migration, production Scheduler activation, Codex, or source mutation.

## Decision and remaining gate
**Accept this focused frozen correction:** no confirmed F04/fixture blocker, with native exact-SHA evidence. **F03 remains Blocked/Unverified**: there is still no complete unfiltered .NET11 Release Client.Tests Windows run with exit 0 and audited cleanup. An earlier six-hour running QA job was observed but must not be counted as passed. Do not close WQ11, claim general Windows hermeticity, or integrate/roll out the full feature under this review alone; diagnose/bisect the slow/full-suite tests and provide a distinct completed acceptance record.
