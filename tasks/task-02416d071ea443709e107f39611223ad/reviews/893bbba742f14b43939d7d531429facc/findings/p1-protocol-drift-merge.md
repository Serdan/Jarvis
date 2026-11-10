# P1 — Frozen browser branch is not integrable with current main

Frozen source: `66d4e06566c3df5e8b771875f421815e19bc6770`; source base `f7ff9012005ceb387b27d7f211f4106711b93716`.

The branch's `Common.AgentProtocol.version` is **3.22** (`git grep` HEAD), while current `main` is **3.63**. The branch introduces browser command DU cases and MCP routes against an obsolete transport and tool catalog. Independent `git merge-tree --write-tree main 66d4e065...` exits **1**, with seven content conflicts:
`Client/Client.fsproj`, `Client/Modules/PermissionPolicy.fs`, `Client/Program.fs`, `Common/Messages/AgentCommand.fs`, `Server.Tests/ServiceTests.fs`, `Server/McpTools.fs`, `readme.md`.

The 290/60 Release tests pass **against obsolete branch source**, not the mandatory combined current main+bridge. A naïve integration would either lose protocol 3.63 behavior or leave conflicts unresolved; coordinated server/client deployment cannot proceed from this frozen commit.

**Fix:** Forward-port the feature onto current main with preservation of all newer protocol 3.63 commands, state-change receipts and authorization semantics; merge with no unresolved conflicts; build/test the combined source and submit the resulting frozen commit for independent re-review. Do not bump backward to protocol 3.22.

**Severity: P1** — material integration and compatibility blocker, distinct from code-level browser behavior.
