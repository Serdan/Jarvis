# Project launch inventory: authority and cutover protocol

Status: design and investigation only. No sender is fenced by this document, and no baseline, occupancy, admission, or release authority is established.

Source inspected: origin/main cb3b2f54a9f586228d5b2b210f1be6600099f932, fetched 2026-11-24. This records source behavior, not the version or configuration deployed in any client. Project scope is Jarvis-managed launches within one project; provider-wide or cross-project capacity is outside scope.

## Evidence and gaps

* Client/SignalR/Client.fs:885-932 attributes a target-bearing BrowserRunInNewTab before invoking the bridge. A script with no target other than a file named launchAgent.js can be sent without this attribution. A filename restriction is not a complete sender fence.
* Client/Program.fs:229-255 prepares scheduled browser script execution independently of that dispatch path, with TargetAgentId = None. BrowserExecuteFile, BrowserExecuteJs and other direct bridge paths also require an exhaustive sender inventory before claiming coverage.
* browser-scripts/chatgpt/launchAgent.js:33-49,77-82 distinguishes possible post-send uncertainty. Neither a missing acknowledgement nor a closed tab proves no send.
* Client/AutomationNativeProviderSessionHost.fs:315-322 has no authenticated provider first-connection or terminal issuer and intentionally holds.
* ReviewWorkflow agent session associations and FeedbackStore last-seen are advisory or missing/resettable, not a complete inventory. Their absence does not prove zero occupancy.

## Required cutover invariants

1. Bind a monotonic inventory generation to the verified project ID, ledger ID and epoch, and to an exact project ledger head. A fresh generation begins closed. A missing, malformed or reset record is closed, never empty.
2. Before establishing any baseline, an enforceable barrier must prevent every supported old and concurrent sender from initiating a launch without the new journal. An ordinary Git record cannot stop old code that ignores it. Establish and independently verify the control point and the full set of clients/routes subject to it; account for already scheduled/in-flight work. No local configuration claim, script filename, voluntary callback or activity row proves this property.
3. Under that barrier, a trusted issuer must enumerate all pre-existing Jarvis-managed sessions and unresolved effects or explicitly retain the scope as unknown. Bind the evidence, issuer, enumeration scope and barrier generation to a durable baseline. Unknown or ambiguous entries stay occupied, or the entire admission remains held. If the issuer cannot establish completeness, there is no positive baseline transition.
4. Every later send must first journal the original effect identity and generation in the same authoritative project ledger lineage. Concurrent baseline, journal and capacity decisions compete via the same project CAS; on conflict, re-fetch and recompute from verified state. Operation-ID replay is reconciliation, never authority to repeat an external send. Unknown publication and send outcomes remain held across restart.
5. Capacity admission must consume verified current inventory and config at the exact transaction snapshot, atomically with the reservation and total/role limit check. Passing a caller-created coverage flag or number across a separate read/write boundary does not satisfy this invariant. Integration requires Worker 21's typed seam; Worker 19 owns client project wiring.
6. A release or exclusion needs original-bound authenticated terminal or no-send evidence. A no-send transition must also exclude a competing send for that exact original effect. A browser acknowledgement, disappearance, reset, absence of an activity record, timeout, or lifecycle update is insufficient.
7. Startup re-verifies the original baseline, sender fence and subsequent journal lineage at a verified head; it cannot assume cached coverage. Stale epochs, missing records, broken lineage, duplicate mismatched identities, and incomplete or unverifiable evidence hold. Existing uncertain effects are never discarded.

## Proof and integration gates

A disposable shared-ledger test needs independent clients and controlled barriers. Exercise old/manual/native/scheduled bypass attempts, a concurrent launch at baseline, stale generation and CAS retry, duplicate and mismatched operation IDs, crash after intent/before send and after uncertain send, restart and deleted/reset evidence, plus foreign or missing terminal/no-send evidence. Test fixture claims are not production issuer evidence.

Source review and integration do not activate production. Positive cutover additionally requires a real enforceable complete sender boundary, a trusted baseline and terminal/no-send source, verified deployment and coverage of every client and scheduled/in-flight sender, and explicit operator authorization. Until those exist, keep capacity startup HOLD.
