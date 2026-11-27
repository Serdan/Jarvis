# Default policy grants ordinary escalation edges to retired roles

## Reproduction

At `Client/Modules/ChallengeRolePolicy.fs:34-40`, `jarvisDefault` puts `Health` and `Manager` in `ordinary`, then adds an edge from every member of that set to `Lead`. `PolicyTests.fs:30-36` duplicates those identities in the expected default graph, and `PolicyTests.fs:54-61` explicitly expects both to be authorized to create Lead-owned conditions.

The canonical role records say otherwise: `Health` version 5 is explicitly the retired legacy LLM Health role, with the deterministic in-process Health service owning liveness and recovery. The current `Lead` role version 19 explicitly says not to reactivate retired Manager and that legacy LLM Health and Manager are retired. The parent task's pinned requirements also identify both as retired.

A direct value-level reproduction is already in the committed positive matrix: `authorizeAdmission (Some (jarvisDefault project)) (AgentRoles(project, ["Health"])) [OwnedBy(AgentRoles(project, ["Lead"]))]` and the equivalent `Manager` principal return `Ok ()` by construction. The default allow-list therefore treats retired identities as valid ordinary executor principals and grants them an escalation edge.

## Requirement and impact

Stage B requires a default policy for Worker and ordinary executor roles, not retired service/dispatcher roles. The parent specification separately says legacy LLM Health and Manager are retired and prohibits incidental privilege through stale identities or aliases. A stale or reintroduced agent carrying either role is accepted by the pure policy instead of failing closed as an unknown/non-authorized role. The current tests make this behavior part of the expected default contract.

## Requested correction

Remove `Health` and `Manager` from the default agent-role allow-list and default edges. Update the standalone fixture matrix to prove that these retired role identities are denied (rather than treating them as ordinary executors). Keep any future exception behind explicit, current role configuration; do not infer active authorization from the continued existence of a retired role record.

This finding is limited to the Stage B pure policy and fixture. It does not request production wiring, role migration, live configuration changes, or any broader authorization redesign.