# Nonblocking MCP response-alert reconciliation (Worker 11)

This source change is not authorization to deploy, restart clients/server, activate
schedulers, or change legacy Git-ledger authority.

## Delivery boundary

Response alerts are a project- and agent-specific durable Git queue. Operator queue
and clear use versioned canonical transactions. An agent sees text only after a
newly verified ledger read and an existing fenced, version-checked consume CAS
commit. Concurrent clients cannot both consume the same version. A cached stale
positive hint cannot revive a cleared/replaced alert. STOP sorts first.

This is at-most-once best-effort transmission, not end-to-end exactly once:
if CAS commits but the MCP/SignalR response is lost, the alert is also lost.
There is no receipt acknowledgment protocol. Uncertain transactions must be
resolved using their original canonical operation identity and receipt; a cache
never authorizes text delivery.

## Fast path, invalidation, and bounded recovery

Client/SignalR/Client.fs caches only verified boolean *presence*, not message text.
The key is canonical ProjectId plus exact agentId. A negative hint means ordinary
MCP replies, including GetJobResult and errors, do not wait for alert Git I/O.
An initial/unverified hint can defer delivery, not authorize stale output.

Successful immediate QueueResponseAlert/ClearResponseAlert calls broadcast the
existing targeted SignalR invalidation; hints can be lost and are not authority.
A client schedules verified Git reconciliation off the response path on the first
relevant reply, on invalidation, on reconnect/re-registration
(InvalidateAllAgentStates), and on a recurring 15-second timer for active agents.
Entries idle for 3 minutes are pruned. Per-project snapshot sharing bounds normal
periodic remote fetches to roughly one per active project per 15-second interval,
rather than one per agent. Invalidation discards the shared snapshot.

Only a confirmed positive presence hint allows the existing visible-response
read+fenced consume path. Any consumption attempt triggers new background
reconciliation. A remote error never establishes an empty queue; the previously
verified positive observation survives verification failures unless a delivery
attempt has been made. Offline, ordinary responses continue without unverified
alert text. A newly queued alert may miss the immediate next response when
a hint is lost or reconciliation remains incomplete; subsequent verified
reconciliation permits a later response to carry it. Background job completion
is never the alert consumption point.

## Verification and remaining shipping gate

.NET 11 Release response-alert tests cover zero synchronous Git fetch for
no-alert GetJobResult, errored replies, queues from another client, STOP order,
stale-positive clear, lost-hint reconciliation, one-shot consumption and
promoted-job/poll behavior. Existing canonical queue tests cover durable
cross-client consumption, bounds and idempotence. Release builds include
Client, Client.Tests and Server.

Before shipping, obtain representative native Windows and Linux concurrent
poll p50/p95 with per-stage command, response alert, Git subprocess and SignalR
timings. Native Windows verification is not available from the currently
disconnected KRUSO client. Passing local Git fixture tests does not substitute
for these measurements. Do not deploy until independent review and operator
approval.
