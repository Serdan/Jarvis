# Recipient-only batch notification acknowledgment (source-only)

## Contract

The additive `AcknowledgeNotifications` MCP action takes `projectName`, the **recipient's** `agentId`, an explicit array of 1–64 `notificationIds`, and an optional stable `operationId`. The existing single-ID `AcknowledgeNotification` remains unchanged.

The client validates the JSON GUID array, and the workflow validates the full requested batch at a single verified ledger snapshot. Missing IDs, cross-recipient IDs, corrupt identities, empty/oversized input and empty GUIDs reject the entire batch. Duplicate IDs are collapsed before constructing mutations; previously acknowledged IDs in a mixed batch keep their original timestamps. A batch containing *only* already-acknowledged IDs returns a stale-state error rather than a misleading new success, while retrying an **applied original operationId** returns its original applied receipt.

A successful batch creates one canonical Git CAS and operation receipt for all newly acknowledged IDs. The caller must treat an unknown result as **unknown**, inspect/reconcile the original operation ID, and must not replay with a fresh ID. Only the listed recipient's records can be touched. Fetching an inbox, running a generic tool or exiting a session never implies acknowledgment.

## Ownership and cutover

This source is developed on `feat/notification-batch-ack-worker18-20261121`, whose frozen parent is Worker 11's independently Accepted `eed1fe1b74fbaf3dca867d7915ffaaeed9767c81`. Worker 11's STOP/response-alert behavior is untouched. This change is **not deployed**, does not acknowledge live notifications, and cannot be merged or activated without the existing trusted-publication gate, composed Release tests and its own independent review.

## Lifecycle tradeoff

A separate bounded batch API is the minimal durable change. Folding processed notification IDs into checkpoint/yield/complete would require expanding those existing versioned mutation schemas, pending-intent identities and independent transactional tests. That remains deferred; the smaller API lets a worker explicitly acknowledge multiple *handled* messages in one call instead of separate Git pushes. Neither a lifecycle success nor a failed/interrupted session automatically acknowledges any unlisted message. Unresolved notices remain pending and must be identified in the canonical checkpoint.

## Fixture evidence

`BatchNotificationAckUsesOneReceiptAndLeavesUnlistedUnread` checks three disposable pending notices, a two-ID/duplicate-ID batch, a single sequence increment, original-UUID retry, and one remaining unread notice. Other tests verify mixed-recipient and missing-ID atomic rejection, invalid bounds, mixed already-acknowledged behavior, stale-only batches and UUID reuse with different payloads. No production message records are fixtures.
