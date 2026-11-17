# P1 — Manual grant approved on device A can bypass approval on device B

**Frozen source:** `6e5d041f16265001b84878018dcf9ebb15b363b1`  
**Review:** `_reviews/a2997d8727d8484e9764919b7fc590a1`

## Finding

The grant is bound to the OAuth owner, a *project name*, and capability families, but not to the authenticated **client device/project instance on which the operator granted permission**. An approved manual grant therefore transfers to an unrelated same-named project on another connected device for the same account, silently bypassing that device's usual per-command permission prompt.

**Source evidence:**
- `Server/ManualGrantStore.fs:12-16,61-77,100-137`: the entry has Owner/Scope/Status/CanonicalProjectId but no approved device identity. For an *existing* project, `Authorize` returns `Ok false` on Approved + matching project name/capability without checking any canonical project ID or device identity.
- `Server/McpTools.fs:875-886,896-928,1053-1062`: every later tool invocation resolves `manualDevice` from the *current* `RequestDeviceRouting.deviceOverride()`, not the device where `RequestGrant` was approved. The same owner/name/capability verifier is used for that newly chosen target, then the command is dispatched there with `manualGrantAuthorized=true`.
- `Server/McpTools.fs:1121-1165`: the operator prompt is routed to the request's selected device but the decision does not bind that device into the store.
- `Client/SignalR/Client.fs:955-962`: `manualGrantAuthorized` deliberately skips the receiving device's `PermissionPolicy.authorizeWithTrust` prompt.

## Reproduction (two-device hermetic negative fixture requested)

1. Register client devices **A** and **B** under the same authenticated OAuth account, with separate physical projects both named `Example` (independent canonical roots/ledgers). Set B's local trust to require mutation approval.
2. Request `existing/Example/Implementation` with `deviceId=A`; have the operator approve on A.
3. Invoke `WriteFile(projectName="Example", deviceId=B, manualGrantCredential=<credential from A>)`.
4. Current routing selects B at step 3. `ManualGrantStore.Authorize` succeeds because only owner, name and operation family are checked; B receives `ReceiveManualGrantedCommand` and bypasses its local permission prompt. **B's distinct project can be modified without a grant approved for B.**

This is a source-level authorization-path finding, **not** a live-host exploit run. Existing tests cover wrong *names* but do not exercise two same-named projects or device overrides.

## Required correction

Bind the approval to a verified device/project instance when requesting the grant and enforce that identity on every subsequent authorized dispatch, rejecting a changed `deviceId`/selected connection. For prospective creation preserve the established canonical Git project binding, and for existing projects prevent same-name substitution/cross-device reuse (or explicitly present and verify each additional device/project to the operator if multi-device authority is a requirement). Add a hermetic two-client same-name negative, a same-client positive, and prospective follow-up identity tests. Preserve one-approval flow, OAuth scope enforcement, CSPRNG UUIDv4, RAM-only verifier, durable scheduled Work, and default-off Scheduler behavior.

## Independent validation

Exact HEAD `6e5d041f16265001b84878018dcf9ebb15b363b1` and clean worktree. `git diff --check a2e9f80760c42039b35aa010619bc193e67b9e81 6e5d041f16265001b84878018dcf9ebb15b363b1`: exit 0. Local .NET 11 Release: `Server.Tests` **309/309 pass**; `Client.Tests --filter FullyQualifiedName~PermissionPolicyTests` **32/32 pass**; adjacent `ProjectCreationTests|SignalRDispatchTests|WorkQueueReservationPermissionsTests|WorkQueueCredentialTests` **18/18 pass**. Existing green tests do not cover this missing negative.
