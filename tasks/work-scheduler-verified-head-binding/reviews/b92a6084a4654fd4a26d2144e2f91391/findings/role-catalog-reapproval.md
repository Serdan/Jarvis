# [P1] Changed WQ04 role version/enablement bypasses approved membership

**Frozen source:** `d28998ae80d4ab3769e14440428f8af59667e967`
**Location:** `Client/Modules/WorkQueueSchedulerHeadBinding.fs:17-24, 169-225, 298-306, 326-335, 373-392`

## Finding

The host boundary's independently approved `SchedulerMembershipEvidence.RoleSubscriptions` records **only** role IDs and queue-subscription sets. `verifyProject` compares `Catalog.Current` IDs to those keys and compares each current `RoleConfiguration.QueueSubscriptions` against the approved set. It does **not** constrain the current role version, `Enabled` flag, role instructions, catalog revision, or catalog blob OID. It returns a successful verified-head result for a new WQ04 catalog that preserves the same queue subscriptions, even when the role was reconfigured after the operator's approval.

This contradicts the source's stated contract at lines 21-22 ("A changed role catalog requires renewed approval before a host callback") and the task's fail-closed requirement for complete explicit role/subscription authority. `ReadVerifiedHeads` (328-335) passes that result into the host, and `runOneManualStep` (373-392) calls `Host.RunOnce` for a changed project head. The downstream coordinator independently rereads current Git role data but has **no access to the observer's approved role revision** (`WorkQueueReactiveWake.fs:136-254`). Its CAS checks do not replace an approval fence at this preflight boundary.

## Deterministic reproduction to add as a negative test

1. Provision the existing two-project disposable fixture. Approve `Worker` version 1, enabled, with `QueueSubscriptions = set ["implementation"]`.
2. Construct `Observer(scope, enabled = true)`, run the initial scan, and verify dormancy.
3. Execute the legitimate WQ04 `configure` transaction to create `Worker` version 2 with **unchanged** `QueueSubscriptions` but changed `Instructions` or `Enabled = false`.
4. Do **not** renew the independent `SchedulerMembershipEvidence`; call `observer.ReadAll()`, `Scan(false)`, or `runOneManualStep`.
5. **Actual result by inspection:** `verifyProject` accepts the catalog because role names and subscriptions match; `Scan` yields `ManualReconcile` hints and the manual step can call the supplied hosts.
6. **Required result:** `NeedsAttention` / explicit role-approval mismatch, with zero host/planner/executor callbacks, until new authoritative membership evidence approves the exact new catalog or role revision.

The existing `RoleSubscriptionChangeRequiresRenewedApprovalInsteadOfSelectingAnotherRole` test only changes subscriptions; it cannot catch this same-subscriptions revision bypass.

## Required fix

Pin the independently approved role/catalog **revision/identity**, not just subscription names: e.g. an approved WQ04 catalog blob OID/revision and/or each current role version plus enabled state. Reject a changed current revision before returning verified heads or invoking hosts. Add adversarial tests for same-subscription new version, disabled role and instruction-only revision; keep the source ref frozen and default-off.

## Secondary diagnostic edge (P2)

`Observer.ProjectIds` (318-319) accesses `scope.Projects` before `ReadAll` validates scope. For an enabled observer built with a null/missing project list, `runOneManualStep` (377) throws rather than returning the specified actionable `NeedsAttention`. Guard this path or resolve the validated membership before accessing `ProjectIds`; ensure no host callback.

These findings do **not** claim an already active production scheduler or provider route. The reviewed module remains default-off and read-only. They block accepting its proposed authority/preflight contract as complete.