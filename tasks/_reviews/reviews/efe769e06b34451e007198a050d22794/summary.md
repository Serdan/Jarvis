# Independent re-review — cross-project notification activity isolation

**Revision:** `efe769e06b34451e007198a050d227947fd83410`  
**Branch:** `feat/incremental-agent-instructions`  
**Base:** `main`  
**Verdict:** **Accepted**. No blocking findings.

## Findings resolved

The previous P2 bug arose because `NotifyAgent` carried the source sender ID through a destination-project TaskWrite, and destination's implicit activation treated matching project-local IDs as the same agent. The current implementation defines a non-local activity guard for foreign `NotifyAgent` and internal ledger `Health` probes. The command still executes against the destination ledger but neither triggers Ready→Working activation nor updates destination agent last-seen or injects unrelated turn guidance.

Crucially, the guard is applied consistently during attribution and instruction filtering. The source-project `AgentInbox` check remains active and can start the actual source sender's Ready assignment, returning mutation effects for cross-device invalidation; recipient notification remains durable with original provenance. Local `NotifyAgent` retains normal implicit activation.

The two earlier review findings around typed state-change propagation and overbroad cache invalidation remain addressed at this commit. No regression in the changed paths was identified.

## Independent validation

- `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore` — **392 passed, 0 failed, 0 skipped**.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` — **95 passed, 0 failed, 0 skipped**.
- The new `CrossProjectNotificationDoesNotActivateSameNamedDestinationAgent` test exercises separate source/destination Git ledgers, identical local agent IDs, source verification, destination state/turn isolation, last-seen isolation, durable notification provenance and positive same-project delivery.
- `git diff --check e84a267..efe769e` — clean.
- Worktree clean; branch synchronized with `origin`.

This review does not authorize integration or deployment. The protocol 3.61 client/server changes require a coordinated release.

**No findings.**
