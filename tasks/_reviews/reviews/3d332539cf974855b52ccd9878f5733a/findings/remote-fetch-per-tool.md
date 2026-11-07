# [P1] Unconditional GitHub ledger fetches on every activatable tool call

**Locations:** `Client/SignalR/Client.fs:99-121`, `Client/Modules/OrchestrationApi.fs:139-160`, `Client/Modules/ReviewWorkflow.fs:2301-2305`, `Client/Modules/LedgerStore.fs:409-419`.

`activateReadyOnActivity` now calls `cache.Invalidate(projectId, agentId)` for **every** activatable operation, including read_file, read_files, get_git_status, search_text, etc., even if the agent was already Working. The cache loader immediately invokes `LedgerBindingConfig.load`, which calls `LedgerStore.fetch`, and then `getAgentWorkingTurn`, which calls **another** `LedgerStore.fetch`. The fetch implementation issues an authoritative remote `git fetch`, not a cheap local read. Thus an already-Working agent performs at least two serial network Git fetches before practically every repository tool operation. Ready activation may perform additional fetches.

The first fetch is inside `withConfigLock` (`OrchestrationApi.fs:144`) and holds an exclusive file lock across threads/processes while the network request runs. Concurrent agent operations using the same checkout are consequently serialized on the network.

This defeats the explicit purpose of the earlier 2-minute state cache and adds latency and GitHub availability dependencies to otherwise-local file operations. The two-client regression test validates correctness, but does not measure this cost.

**Request:** Design efficient assignment-generation/change observation that detects inter-client transitions without two mandatory remote Git fetches on every working source read. A push-based invalidation or appropriately defined consistency/refresh mechanism may be considered. Keep the authoritative Ready activation and new-turn instructions guarantees and add performance/remote-fetch-count regression coverage. At minimum reuse a single verified snapshot rather than fetching twice; avoid holding the binding lock across unnecessary network fetches if possible.
