# [P2] Cross-project notification starts unrelated destination agent with matching ID

**Locations:** `Server/McpTools.fs:1318-1324,640-666`; `Server/McpTools.fs:122-132`; `Client/SignalR/Client.fs:103-111,121-153`.

**Scenario:**
- Source project `Wayfold` has a sender with `fromAgentId = "Agent 3"`.
- Destination project `Jarvis` separately has a *Ready* agent named `Agent 3`, assigned its own task. These names are project-local identities.
- The sender invokes `notify_agent(projectName="Wayfold", fromAgentId="Agent 3", toProjectName="Jarvis", toAgentId="Reviewer", ...)`.

`NotifyAgent` constructs a `TaskWriteCommand` for the **destination** with `ActorId = fromAgentId` and `SourceRef = "Wayfold"`. The request routing metadata also identifies the same `fromAgentId` as the active agent. Server first checks the source using `AgentInbox`, then dispatches that TaskWrite to the destination client with the sender's agent ID. The destination's `activateReadyOnActivity` runs before executing the notification. Its `activatesAgent` accepts `NotifyAgent` whenever `cmd.ActorId = agentId`, without checking that this is a cross-project send. With the same ID registered in Jarvis, it activates Jarvis's **unrelated Ready assignment**, starts the Working clock, and emits an extra destination-project invalidation. The user only requested notification delivery, not starting the recipient project's unrelated assignment.

**Impact:** Common agent labels reused across projects cause unintended Working-turn creation and incorrect task state. Existing tests cover cross-project routing and generic implicit activation separately, but do not combine identical names across projects.

**Request:** Make the destination notification's activity scope distinct from the source sender's identity. At minimum, prevent `TaskWrite.NotifyAgent` with a nonempty cross-project `SourceRef` from being treated as local agent activity at the destination, without suppressing source-side sender verification/activation or durable delivery. Add a two-project integration regression, including same-name source/destination agent IDs.
