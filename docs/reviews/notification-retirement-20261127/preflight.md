# Notification retirement: isolated regression preflight

Task: robustness-notification-retirement-regression-20261127. Generation 3, pinned specification dd3dae786d8258d5688f55edfbf58b6b5d2184ce. Baseline 914907d14803ddf861e438419147903dcbf8f7f7, registered test/notification-retirement-regression-w18-20261128 worktree. Lead condition c33d14674d19c9900262ab9c22cd3ed0 was satisfied with an actual bounded authorization probe, not inferred from catalog metadata. The parent production lease remains separately blocked. This is a standalone test project and is intentionally not registered in the normal test suite.

## Reproduction

From the isolated checkout, run on Linux with .NET 11:

```sh
dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory!=RetirementContract' --logger 'console;verbosity=normal'
dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --no-build --filter 'TestCategory=RetirementContract' --logger 'console;verbosity=normal'
```

The first command establishes baseline controls; the second is intentionally red on the historical implementation. Do not interpret an unrelated compilation, fixture, authentication or infrastructure error as the intended red. The intentional assertion explicitly identifies whether the actual fresh operation returned Delivered/Blocked or succeeded at registered client ingress and whether the ledger changed. A rejected fresh operation must leave the ledger and pending view unchanged. Terminal outputs and exact test source commit will be recorded separately after execution; this file does not claim a test pass.

The fixture uses the real Git-backed LedgerStore, ReviewWorkflow and client command ingress, two clones of a fresh local bare destination remote, an independent local foreign project with the same synthetic agent names, randomized project/ledger IDs, per-clone Git identity, a private Linux XDG root verified before use, and cleanup in finally. No shared project registration or live service is used. The direct cross-project workflow probes verify sender membership in the independent source ledger; the ingress probes bind only local fixture checkouts in the private config and use the actual client command processor. The server source-validation hop is traced from source, not claimed as executed network ingress.

## Authority and compatibility trace (baseline source)

- Client/Modules/ReviewWorkflow.fs:2518–2535 derives immutable Delivered/Blocked outcomes from existing operation paths. 2537–2592 canonicalizes the source, uses LegacyDirectWriterFence.transact and decides fresh delivery or suppression based on recipient preferences; no retirement check. The operation ID is also the stable notification identity.
- Same-project calls verify registered sender and recipient and optional task; foreign source verification is the server's responsibility before forwarding to the destination. Client/Modules/OrchestrationApi.fs:2850–2873 dispatches NotifyAgent, preference changes, and AcknowledgeNotification. Client/SignalR/Client.fs processIncomingCommand and the cross-project routing guard keep the foreign sender from activating a same-named destination agent. Server/McpTools.fs source-project inbox validation and cross-project forwarding must remain an authority boundary; a client-side destination workflow invocation alone does not establish server authentication.
- Client/Modules/ReviewWorkflow.fs:2703–2721 enforces recipient-only CAS acknowledgement; 2723 onward reads pending notices. Acknowledgement changes the timestamp on the original historical record, not its body or identity. An exact operation retry must continue reporting its original outcome after acknowledgement or preference changes. A different request under the same UUID must not be accepted.
- Client/Modules/ReviewWorkflow.fs:2594–2701 implements independent durable one-shot ResponseAlerts/STOP. Preserve this independent operator control path.
- Complete baseline searches for DismissNotification in Client, Server and Common F# source found zero matches (135, 48, 8 files respectively; Finished). The existing actual command catalog audit in checkpoint 19422 lists GetNotificationPreferences, GetResponseAlerts, NotifyAgent, AcknowledgeNotification, QueueResponseAlert, ClearResponseAlert, SetNotificationSenderBlock, SetNotificationMute, with no administrative dismissal. The source dispatch has no such command; recipient ACK is not an authorized administrative substitute.

## Later real dismissal transaction tests — deferred, no substitute implementation

The following cases require the actual new route and parent lease; none are claimed executable today. Use only a newly disposable ledger, never the live 42 notices. Create distinct GUIDs from decimal indices 001–046, e.g. 00000000-0000-4000-8000-000000000001 through 00000000-0000-4000-8000-000000000046. Seed 001–042 as historical delivered notifications before activating retirement (using the prior real route or frozen historical fixture), alternating two registered recipients and both local and foreign sources. Capture exact snapshot head, original record JSON/body and operation receipts, and unread views. IDs 043 and 044 are unrelated and post-snapshot notices; 045 is nonexistent and 046 exists only in the foreign ledger.

| Case | Expected authoritative result |
| --- | --- |
| Exact 42 IDs under authenticated administrative actor | One atomic auditable disposition for those IDs, original immutable records/body and historical operation outcomes retained; recipient pending/dashboard agree; no fabricated recipient ACK |
| 043 unrelated and 044 added after snapshot | Both remain untouched, no wildcard or dynamic query expansion |
| Unknown 045, malformed/empty UUID, duplicate or foreign-only 046 | Entire request rejected atomically; compare head, records, disposition state and unread views |
| Sender/recipient/unrelated worker, mismatched actor or project | Denied by real ingress before mutation; no self-asserted administrator identity |
| Recipient ACK and exact dismissal race, including a pre-ACKed notice | CAS serializes; no lost updates, duplicate disposition or invented ACK; explicit already-read behavior per approved contract |
| Stale snapshot/CAS | Reject or defined conflict, never silently widen selection; retry after reading verified canonical head |
| Lost reply, new process/restart, same stable operation UUID and args | Reconcile original immutable receipt without repeat mutation; changed arguments under same ID rejected |
| Historical Delivered/Blocked retry after retirement and dismissal | Original outcome and autoreply retained; no new notice/suppression; old schema body still readable |
| Same-project and cross-project new sends, including would-be suppression | Fail closed before any ledger change; stable operation receipts still accessible; no unauthorized source/destination activity |
| Operator STOP and ResponseAlerts | Unchanged separate one-shot control behavior |

These cases distinguish authority, immutable history, disposition and unread projection. They must be executed against the actual future administrative transaction, not a parallel engine or a fake route.

No deployment, production edits, old ACK replay, live notification mutation or dismissal was performed by this preflight.
