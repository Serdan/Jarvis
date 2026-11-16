# Per-kind unread notification indicators

The ordinary agent-attributed MCP response includes an **optional** auxiliary
JSON object such as:

```json
{"notifications":{"message":2,"challenge":1}}
```

It is appended to the existing state-effects text block, and the same object
is present in MCP `Meta["jarvis/notifications"]`. The requested tool's original
result and error blocks are untouched. Count values are positive integers;
zero kinds and the entire `notifications` object are omitted when none are
pending or the canonical ledger cannot be verified. This is an **advisory**
projection, never authorization evidence and never a mutation.

## Canonical source and kinds

Every event has an immutable notification UUID and a `notifications/{id}.json`
record in the project's authenticated Git ledger, addressed to exactly one
`toAgentId`. Acknowledging with `AcknowledgeNotification` sets
`acknowledgedAtUtc` in that same record via a recipient-authorized CAS
operation. Counts include only records lacking an acknowledgement, grouped by
their explicit broad `kind` (currently `message` and `challenge`). The
`eventType` field is more specific (for example `direct`, `assigned`,
`resolved`, `blocked`, `reopened`, `split` and `cancelled`); it never
implies an unrequested priority or severity.

Schema-2 direct-message notifications written before this feature lacked
`fromProjectName`, `kind` and `eventType`. These additive properties are
optional to the reader: absent `kind` becomes `message` and absent
`eventType` becomes `direct`. No migration, rewriting or bulk re-acknowledgement
of historical messages is required.

Actionable challenge creation, assignment, resolution, operator response,
reopening, splitting and cancellation now emit typed event notifications as
part of the **same** Git CAS commit. Event UUIDs are deterministic across
retries and distinct per operation, event type and recipient. The issuer is
notified after a condition is resolved/blocked; an assigned or reopened
condition notifies its next owner; cancellation notifies outstanding owners.
No direct discretionary `NotifyAgent` call, recipient mute preference or
cross-project message route is involved. The existing acknowledgement action
works for both kinds.

## Scope, freshness and operational constraints

The client projects unread counts from batched immutable notification blobs at
a verified ledger head, keyed by canonical project ID (so worktree selectors
share the project scope). The response only returns the initiating agent's
counts from that project's snapshot, not another agent's or another project's.
Notification creation/acknowledgement and challenge mutations invalidate the
local cache; the server issues best-effort authenticated, user-scoped device
invalidation hints after confirmed writes. The cache has a 30-second fallback
refresh, also expires on reconnect, and never treats failed reads as confirmed
zero. A promoted job's completion is not itself a response; polling uses
fresh poll-time counts rather than replaying the original job's old summary.
Pending/error responses may still carry counts when an authorized project
identity exists. OAuth/invalid-identity failures omit the summary entirely.

**Deployment:** Build and deploy the client and server together for immediate
count visibility. Newer clients add `NotificationCounts` to the wire response;
older clients omit it, which the server treats as unavailable, and new
notification JSON remains readable by older readers that ignore unknown
fields. An older server cannot surface the new MCP summary even when newer
clients write typed notifications. Counts never constitute a delivery receipt;
agents still read the inbox and explicitly acknowledge notifications.
