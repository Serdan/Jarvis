# Notification retirement: isolated regression package

Task: `robustness-notification-retirement-regression-20261127`  
Assignment: generation 4, issued at ledger sequence 20711; pinned assignment spec `dd3dae786d8258d5688f55edfbf58b6b5d2184ce`. The current task spec blob differs only by a final newline (`9a0d18efecddac6b030e151de9653c0273ce5219`); the substantive instructions are identical.

## Scope and source provenance

- Worktree: `notification-retirement-regression-w18-20261128`, branch `test/notification-retirement-regression-w18-20261128`.
- Test-only source HEAD at the start of final evidence capture: `0a7a6944c698e20e58315c8b6f05d52fdfc8a97e` (clean; `git status --short` was empty). This is the committed package exercised by both exact Release jobs below. No test or production source changes were made after either run; this final evidence update changes only this preflight document.
- The last verified base `d5788cecf7eafa9ec946a5a67bb974784f6901eb` is an ancestor of this branch. This is not a direct checkout at that SHA: the branch inherits the later `914907d` integration baseline. The branch's own delta from `914907d` is limited to this document and `tests/NotificationRetirement20261127/{NotificationRetirement.Tests.fsproj,NotificationTests.fs}`. No production source or shared project registration was added by this task.
- Notification send/delivery/ACK/pending implementation in `Client/Modules/ReviewWorkflow.fs` is unchanged from `d5788ce`; inherited changes in that file concern taskless state and review authority. Inherited `OrchestrationApi.fs` and `Server/McpTools.fs` changes concern observation/reviewer/taskless routes. `Client/SignalR/Client.fs` does contain inherited ingress and response-alert admission changes, so registered-client ingress results below are specifically evidence for the actual `914907d` integration state, not a claim that the entire client is byte-identical to `d5788ce`.
- `git diff --check` passed for the package commit. The two-line compile correction in `NotificationTests.fs` (an explicit `string` annotation for the snapshot commit and boxing the NUnit collection expectation) is already included in the committed package. The worktree was clean before this documentation-only finalization.
- The standalone project targets .NET 11, references Client, and is not included in the solution or normal configured test workflow. Search for `NotificationRetirement20261127` across `*.slnx`, `*.sln`, and `*.json` returned no matches; configured tasks remain only `build` and `test`.

## Fixture isolation

The tests use the real Git-backed `LedgerStore`, `ReviewWorkflow`, and (where stated) `Client.SignalR.Client.processIncomingCommand`. Each fixture creates fresh local bare Git repositories and clones, randomized project/ledger IDs, and a private Linux XDG config root. It sets an empty global Git config and `GIT_CONFIG_NOSYSTEM=1`, asserts that the Jarvis config path resolves inside that private root, and deletes the root in `finally`. No shared project registration, server/network ingress, live ledger, live notification, or production service is used.

The direct workflow cross-project cases verify sender membership in the separate synthetic source ledger, but do not claim to execute the server source-validation hop. The registered-ingress cases bind only the local fixture checkouts and exercise the actual client command processor.

## Release evidence

### Intentional RED — reconciled once

Exact job `cb4af13d48d94e10a05be8ccd4227ba8` was retrieved once under Worker 18 after recovery. Terminal status: **Completed, exit code 1**. The Release build succeeded (Client and the standalone test assembly were produced); the only compiler output was the existing FS0760 IDisposable-construction warning in `Client/Program.fs`. NUnit discovered 6 of 6 selected retirement-contract cases; all six failed at their explicit `INTENTIONAL RED` assertions:

| Case | Observed legacy behavior |
| --- | --- |
| `RegisteredFreshSendMustReject(false)` | Fresh registered same-project send succeeded; ledger changed; one pending notice |
| `RegisteredFreshSendMustReject(true)` | Fresh registered cross-project send succeeded; ledger changed; one pending notice |
| `FreshSendMustRejectWithoutLedgerMutation("", false)` | Fresh same-project send returned Delivered; ledger changed; one pending notice |
| `FreshSendMustRejectWithoutLedgerMutation("", true)` | Fresh same-project muted send returned Blocked; ledger changed; zero pending notices |
| `FreshSendMustRejectWithoutLedgerMutation("foreign", false)` | Fresh cross-project send returned Delivered; ledger changed; one pending notice |
| `FreshSendMustRejectWithoutLedgerMutation("foreign", true)` | Fresh cross-project muted send returned Blocked; ledger changed; zero pending notices |

This is the intended red-first evidence: the historical implementation still writes a notification or suppression record for a fresh operation. It is not a compilation, fixture, authentication, or infrastructure failure. This exact job was not rerun.

Reproduction of the isolated red category (do not mix it into green CI):

```sh
dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory=RetirementContract' --logger 'console;verbosity=normal'
```

### Green compatibility controls — completed

Exact job `1e31bbed3dda4310869a151a3d4d7edc` was reconciled once under Worker 18. Terminal status: **ToolCompleted, exit code 0, not timed out**. The Release build succeeded (Common, Client, and the standalone test assembly were produced); the only compiler output was the existing FS0760 IDisposable-construction warning in `Client/Program.fs`. NUnit discovered 4 of 4 selected control cases; all four passed:

| Passed control | Evidence |
| --- | --- |
| `BlockedRetrySurvivesPreferenceChangesAndStopRemainsIndependent` | Immutable Blocked retry remains stable across preference changes; STOP remains independent |
| `CrossProjectSourceIdentityAndRecipientAuthority` | Cross-project source identity and recipient-only acknowledgement authority |
| `DeliveredRetryAndRecipientAckPreserveHistory` | Delivered retry and recipient acknowledgement preserve historical records/body |
| `ForeignRouteAndRecipientAckUseActualClientIngress` | Foreign route and recipient acknowledgement use the actual registered-client ingress |

Terminal summary: **Total tests: 4; Passed: 4; Failed: 0; Test Run Successful; elapsed 7.0775 seconds**. The job was not rerun.

```sh
dotnet test tests/NotificationRetirement20261127/NotificationRetirement.Tests.fsproj -c Release --filter 'TestCategory!=RetirementContract' --logger 'console;verbosity=normal'
```

Purpose: prove the existing Delivered/Blocked immutable retry behavior, historical record/body preservation, duplicate suppression, cross-project and recipient-only ACK authority, stale ACK handling, and independent STOP/ResponseAlerts behavior. The command used only the console logger; no TRX logger was requested and no `.trx` artifact was produced. The durable test evidence is the exact buffered Jarvis job transcript and terminal counters above, not a TRX attachment.

## Current route and authority evidence

- `Server/McpTools.fs:380-389` lists the actual ledger task methods. Notification-related entries are `GetNotificationPreferences`, `GetResponseAlerts`, `NotifyAgent`, `AcknowledgeNotification`, `QueueResponseAlert`, `ClearResponseAlert`, `SetNotificationSenderBlock`, and `SetNotificationMute`. There is no administrative dismissal method.
- `Server/McpTools.fs:2113-2123` exposes NotifyAgent and recipient acknowledgement. The ACK tool is explicitly described as marking a notice read by its recipient and preserving the message.
- `Client/Modules/OrchestrationApi.fs:2850-2873` dispatches NotifyAgent and AcknowledgeNotification; there is no dismissal dispatch.
- `Client/Modules/ReviewWorkflow.fs:2518-2535` derives immutable Delivered/Blocked outcomes from the original NotifyAgent operation. Lines 2537-2592 write either a suppression receipt or a notification for a fresh operation based on recipient preferences; there is no retirement gate.
- `Client/Modules/ReviewWorkflow.fs:2703-2721` enforces recipient-only acknowledgement with a ledger transaction/CAS and updates only the acknowledgement timestamp on the historical record. A different actor is denied; an already acknowledged record is stale.
- `Client/Modules/ReviewWorkflow.fs:2594-2701` implements the separate one-shot ResponseAlerts/STOP queue. It is not a notification dismissal mechanism and must remain independent.
- Complete bounded source searches for `DismissNotification` returned zero matches: Client (136 files), Server (48 files), Common (8 files), each with `StopReason=Finished`. No fake dismissal implementation or recipient impersonation was introduced.

## Later real dismissal transaction tests — deferred

These cases require the actual new administrative transaction and the parent task's separate source lease. None are claimed executable by this package. Use only a newly disposable ledger, never the live 42 notices. Create distinct GUIDs from decimal indices 001-046 (for example `00000000-0000-4000-8000-000000000001` through `...000046`). Seed 001-042 as historical delivered notices before activating retirement, alternating two registered recipients and local/foreign sources. Capture the canonical head, original JSON/body, operation receipts, and unread views. IDs 043 and 044 are unrelated/post-snapshot notices; 045 is nonexistent; 046 exists only in the foreign ledger.

| Case | Required authoritative result |
| --- | --- |
| Exact 42 IDs under authenticated administrative actor | One atomic auditable disposition for those IDs; original records/bodies and historical operation outcomes retained; recipient pending/dashboard agree; no fabricated recipient ACK |
| 043 unrelated and 044 added after snapshot | Both remain untouched; no wildcard or dynamic query expansion |
| Unknown 045, malformed/empty UUID, duplicate or foreign-only 046 | Entire request rejected atomically; head, records, disposition state and unread views unchanged |
| Sender, recipient, unrelated worker, mismatched actor or project | Denied by real ingress before mutation; no self-asserted administrator identity |
| Recipient ACK and exact dismissal race, including a pre-ACKed notice | CAS serializes without lost updates, duplicate disposition or invented ACK; already-read behavior follows approved contract |
| Stale snapshot/CAS | Reject or defined conflict; never widen selection; retry only after reading verified canonical head |
| Lost reply, new process/restart, same stable operation UUID and args | Reconcile original immutable receipt without repeat mutation; changed arguments under same ID rejected |
| Historical Delivered/Blocked retry after retirement and dismissal | Original outcome/autoreply retained; no new notice/suppression; old schema body remains readable |
| Same-project and cross-project new sends, including would-be suppression | Fail closed before any ledger change; stable operation receipts remain accessible; no unauthorized source/destination activity |
| Operator STOP and ResponseAlerts | Existing independent one-shot control behavior remains unchanged |

These tests must target the actual future administrative transaction, not a parallel engine or a fake route.

## Safety boundary

No live notification mutation, raw live-ledger edit, old ACK replay, deployment, production implementation, source edit, shared project registration, or normal-suite registration was performed. The parent task remains the sole production implementation and cleanup task; this child package does not satisfy the parent Stage B challenge or grant its source lease.
