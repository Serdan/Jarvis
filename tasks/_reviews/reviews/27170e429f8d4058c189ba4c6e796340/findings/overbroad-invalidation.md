# [P2] Every task write expires every agent cache across all devices

**Locations:** `Server/Services/ClientService.fs:72-99`; `Server/McpTools.fs:659-668`; `Client/SignalR/Client.fs:28-35`; `Client/Modules/WorkingTurnGuidance.fs:78-81`.

The server currently decides invalidation using `afterSuccessfulCommand`: **every** `TaskWriteCommand` returns `true`, regardless of whether it changed a Ready/Working assignment. It then sends a project-wide invalidation to every registered device. Each client removes the cached state of **every agent** in that project. This applies to ordinary notifications, checkpoints and task metadata changes that may leave all agents' Working state unchanged.

With many concurrent agents, routine notifications and checkpoints repeatedly expire other agents' warm caches. Their next local `read_file`, Git status or similar operation must reacquire the GitHub ledger inside `LedgerBindingConfig.loadAtRootWithSnapshot`, including its cross-process binding lock. A busy project can therefore approach a network fetch on every otherwise-local call despite the intended two-minute cache.

The new offline-source test only checks reads without intervening task writes; it does not cover this high-churn case or bound the refresh count.

**Request:** Scope invalidations to the identities whose Working-turn/assignment data changed, and do not broadcast when there was no relevant mutation. Prefer a typed mutation result produced from the canonical operation over string/action-based estimates in the MCP layer; test unrelated task writes and parallel agent activity with a counted ledger loader.
