# Cross-device committed job-wake invalidation — 2026-11-26

Task: automation-job-wake-cross-device-invalidation-20261126, generation 1; pinned spec f3478f18eaa554a8fe3e56cb90420a4fc0923759.

## Scope and authority

The job-terminal reconciliation publishes a bounded typed invalidation only after its canonical ledger resolution CAS wins. The publication carries the exact binding identity used by that transaction (logical project name, project ID, ledger ID and epoch) and changed agent IDs. Local invalidation and the existing reconciliation fast path remain in place. Send failure is reported but never rolls back or replays the commit.

The existing authenticated SignalR connection is reused. HubService verifies the current registered connection and matching authenticated user before forwarding to that user's other registered devices. The server has no independent project authority; each receiver checks its approved local binding for exact project/ledger/epoch identity before invalidating the specified agent state and requesting the existing wake reconciliation. No all-user broadcast, global cache invalidation, terminal evidence, eligibility or launch credentials are carried. Authenticated hints can be stale or duplicated; they are not proof. The scheduler's existing verified canonical head reads and full scans, including reconnect/startup, recover missed hints without inference polling. Default-OFF runtime gate and existing STOP and eligibility gates are unchanged.

## Evidence

Focused Release command:
`dotnet test tests/AutomationJobTerminalWake20261126/AutomationJobTerminalWake20261126.fsproj -c Release --nologo`

Final job de9fe62703ea45dab5eae2240e128f6c: exit 0, 16 passed, 0 failed, 0 skipped, duration 26 seconds (net11.0). Includes 13 prerequisite regression tests and three hosted disposable cases (delivered, disconnected/reconnected, dropped receiver callback plus controlled full scan). The hosted tests use the actual configured server ingress, registered JWT-authenticated loopback clients with separate Git clones and the production publisher/receiver registration. They exercise unregistered and replaced sessions, malformed project input, foreign user, wrong local project/ledger/epoch, precommit and duplicate hints; terminal resolution uses real disposable receipts and canonical Git CAS; launch is fake. Earlier test-only compile errors (F# namespace and assertion overload) were corrected before this run. Git diff --check clean.

## Integration and boundaries

Isolated branch based on verified origin/main e15d880a9a212f1771951ad43faddf82691e7948, explicitly merged exact accepted registration prerequisite 8d7664aa6db2fb6b84c8af69e2ae919a7839cac1 (review bb831b6452f444dda4ab567c3a5d9149), which was not yet in that base's ancestry. Integrator owns publication and reconciliation with newer main and Worker 14 receipt retention; no edits to retention or Worker 6 MCP changes. Mechanical typed interface mock updates keep existing test fixtures consistent. No live sends, real launches, activation, deployment or full suite performed. Lost hints recover a committed canonical requeue, not an evicted terminal receipt that was never committed; retention remains separate. Old/nonmatching receiver bindings may drop a hint and rely on existing recovery. Independent review required before integration.
