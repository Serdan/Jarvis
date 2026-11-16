# Independent review — Reviewer 1

**Verdict:** Accepted, strictly as a **test-only, default-off Work-native Agent-negative-space fixture**.

**Pinned source:** `2b76e344976b71c11cc4d46cd0883a1abe5145ad`; parent/base `04b90e0cc43f8939f2c0722ea03d085f3244925b`; review `c41e930ad27c4b08b79d8bf5aa503d63`. Used separate clean review worktree `reviewer1-agentfree-c41e-20261116`.

## Independent inspection

- Exact frozen diff has only `Client.WorkNoAgentFixtures/Client.WorkNoAgentFixtures.fsproj` and `Client.WorkNoAgentFixtures/WorkNoPersistentAgentLifecycleTests.fs` (428 additions); no production/shared project changes. `git diff --check <base> <head>` passed. Review worktree remained clean after tests.
- Inspected all four NUnit fixtures and the underlying `Client/Modules/WorkQueueGitLedger.fs` credential path. `executeCredentialFixtureAt` authenticates the presented bearer against the persisted launch verifier both before and inside `change true (Some verifyWinningCas)`, using original project Git CAS. Credential bearer is a volatile argument, not a stored Agent identity.
- Each fixture uses a disposable bare Git remote and two clones. The preserved-tree oracle compares the complete Git `ls-tree` entries/blob OIDs under `agents`, `records/agent-instructions`, `records/roles`, and `notifications` after every successful credential/inert CAS; it separately asserts raw historical Agent and instruction JSON and canonical Task text stay unchanged. The empty-Agent variant proves progress without registration/AgentInbox.
- Cases cover first credential connect; claim; read-only checkpoint/completion; exact original connect/checkpoint/completion operation UUID replay with stable Git head; two independently issued launch UUIDs on one durable Task/role; cross-launch and wrong bearer denial; stale two-clone connect denial; and STOP rejecting new checkpoint/completion/heartbeat while allowing the exact prior checkpoint receipt replay.
- No evidence of production Agent record creation; tests assert no persistent launch-specific Agent or instruction paths, including after failure/replay. Old historical Agent/role/instruction blob OIDs remain identical.

## Independent local execution

`dotnet test Client.WorkNoAgentFixtures/Client.WorkNoAgentFixtures.fsproj -c Release --logger "console;verbosity=normal"`

Target: .NET 11; NUnit adapter discovered **4/4** tests; **4 passed, 0 failed** (~19.75 s). A pre-existing Client FS0760 warning and a non-blocking fixture module FS0988 warning were emitted. `git diff --check` and checkout-clean verification passed.

## Scope and explicit NO-GO

This proves only the isolated accepted **internal fixture route**. It does **not** prove private production credential bootstrap/bearer delivery, an operational browser/provider/host bridge, account/project WQ10 migration, the authentic old-writer protocol fence, Scheduler activation, or absence of server-local legacy Agent instruction files. These remain **NO-GO** for live cutover. The standalone fixture project must be invoked explicitly until integrated into a wider test runner.

**Disposition:** Accepted for the claimed test-only regression proof; not an operational launch or migration approval.
