# Cross-project notifications

Jarvis supports durable notifications between agents registered in different Git-backed project ledgers. A notification is written **only to the destination project's remote ledger**. The source ledger remains untouched. No global database, queue or server persistence is involved.

## MCP

Existing calls remain unchanged:

```json
{"projectName":"Wayfold","fromAgentId":"Agent 10","toAgentId":"Lead","message":"..."}
```

For a cross-project delivery, supply `toProjectName`:

```json
{"projectName":"Wayfold","fromAgentId":"Agent 10","toProjectName":"Jarvis","toAgentId":"Jarvis Lead","message":"..."}
```

`projectName` identifies the **source** project; `toProjectName` is the **destination**. An empty or omitted `toProjectName` means same-project behavior. The server first verifies the sender exists in the source ledger, then routes the destination write to a connected client with the destination ledger binding. The destination validates the recipient. `taskId` is optional; for cross-project messages it is interpreted as a source-project task reference and is not looked up in the destination.

The recipient's `agent_inbox` exposes `FromAgentId` and `FromProjectName`. Older same-project notices have an empty/null `FromProjectName` and remain readable. The notification ID and acknowledgements continue to use the destination ledger. Operation IDs may be omitted on new writes; the server returns the generated ID in the tool response. For uncertain outcomes, retry with the exact returned ID rather than creating another notification.

## Multi-client routing

Only authenticated user's registered clients are considered. The conversation-selected client is preferred, then other connected clients in deterministic order. Each candidate is checked through `LedgerHealth` before dispatch. Explicit `deviceId` restricts the request to that device; it never silently falls back. Destination writes require a client on protocol **3.48** or later. Source validation may use an older compatible client. Once a client with a valid binding is selected, a timed-out or failed write is **not** automatically retried on a different device: check the operation status first.

## Rollout

Deploy the updated server and update/reconnect the Jarvis client(s) before agents start using `toProjectName`. Until that happens, the existing shared `Safety Reporter` identity and `/home/serdan/Projects/AGENTS.md` instructions remain in effect. After deployment, change the workspace safety-report instruction to send `projectName` as the actual source project, `fromAgentId` as the actual reporting agent, `toProjectName: "Jarvis"`, and `toAgentId: "Jarvis Lead"`. Keep immediate user notification and no-workaround behavior.
