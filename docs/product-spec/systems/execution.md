# System: Execution and authority

- **Identity:** `telos.execution`
- **Depends on:** No other System in this experiment; exposes bounded execution to Work
- **Status:** Proposed enduring System contract

## Responsibility

This System makes authorized model and tool execution possible on the Operator's selected projects, devices and services. It must distinguish **capability** (what a client can technically access) from **authority** (what an authenticated actor is allowed to do for a particular objective).

Telos may coordinate local machines through a connected client and authenticated service. It need not copy every repository into a hosted workspace or move every action into one server. A visual client can observe and initiate approved actions without becoming an execution authority.

## Behavioral contract

An execution is bounded by its actual Work context, current permission scope, available resources, budget and valid session/claim identity. Role compatibility allows an execution to *consider* Work; it does not itself authorize file writes, protected Git changes, credential access or external effects. The Operator can grant particular manual authority and may retain control of deployment, releases and other reserved transitions.

Resources that cannot safely be shared—source write sets, native build machines, benchmarks, remote effects—must have trustworthy admission and ownership. Never represent an informal reservation as an enforceable lock. Capacity is a real constraint across simultaneous sessions, not an aspiration attached to an agent name.

When a model produces candidate source, Telos should favor isolated, attributable and reversible work inside the granted scope. Promotion into main, deployment and other irreversible actions require the independently applicable checks. The cost of verification should be proportionate to the action's risk.

Every external action has an identifiable request and outcome. On a timeout, crash or ambiguous acknowledgement, Telos preserves **unknown** until the exact effect can be reconciled; it must not blindly resend the same source push, browser submission or external request. A closed browser tab after a verified submitted prompt is not evidence that remote model execution stopped.

Execution sessions are temporary. A continuation uses canonical state and a checkpoint instead of a large reauthored launch prompt or a stale conversation tab. Successful progress and failures are recorded with honest provenance and enough detail for safe continuation.

## Evidence that this System behaves correctly

Two concurrent requests cannot both claim an exclusive source/resource grant; denied actions remain denied even if an agent can name the path. An interrupted execution can resume its intended Work without duplicating a completed external operation. A manually authorized action has an attributable scope, while a neighboring project with the same path or name does not inherit that authority. An unknown outcome remains fenced pending evidence, not treated as either success or safe retry.

**Related Features:** [Progress Work](../features/progress-work.md), [Recover safely](../features/recover.md), [Operator decisions](../features/operator-decisions.md).
