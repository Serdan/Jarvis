# One-shot agent response alerts

Jarvis exposes `queue_response_alert`, `get_response_alerts`, and
`clear_response_alert` for an authenticated operator/Lead. Queue an instruction
for an exact project and registered target agent. A message beginning with
`STOP` is presented before other pending alerts. The content is advisory:
there is no process termination, task lifecycle transition, acknowledgement
or proof of obedience. Agents may voluntarily commit their safe work and
`yield_agent` after receiving a STOP.

Pending instructions are stored in the project's verified orphan Git ledger
at `records/response-alerts/{agentKey}.json`, not in a client-local cache.
A maximum of four alerts per target may be pending, with a limit of 1536 UTF-8
bytes per alert. The operator can inspect messages without consuming them
and clear one using its exact alert ID. Enqueue/clear uses idempotent
caller-stable operation IDs. Recipient notification mute/block rules do not
affect operator alerts.

The destination client checks the verified ledger when constructing each
eligible agent tool response, **including an operation error or a job-result
poll**, and prepends alerts to the typed `Instructions` envelope. Existing
AGENTS.md and Working-turn duration guidance remain intact. Polling neither
records agent activity nor activates Ready assignments. Project health probes,
cross-project coordination routing and observer-only operations must not
establish the destination agent identity.

For slow commands promoted to client-owned jobs, the pending handle is an
agent-visible response and may carry a queued alert. Background job completion
is **not** a response boundary: it never consumes a queued alert or stores one
in the immutable completed job response. The next completed-job poll or any
other ordinary tool reply may deliver an alert queued after the pending handle.
Repeated completed polls do not replay earlier alerts, even if an old stored
job result contains an alert from a previous client version.

Delivery uses a Git compare-and-swap to clear the pending queue **before**
returning instructions. This is best-effort at-most-once: a lost response after
the consume commit can lose an alert; if the read or mutation fails, pending
messages remain for a later call. Jarvis does not try to replay an uncertain
response or coordinate exactly-once acknowledgements. A subsequent tool call
after successful consumption does not re-display the alert.

This feature requires a coordinated server/client release and review. It does
not enable scheduling, restart any running agent, or bypass existing local
workspace/ledger permissions.
