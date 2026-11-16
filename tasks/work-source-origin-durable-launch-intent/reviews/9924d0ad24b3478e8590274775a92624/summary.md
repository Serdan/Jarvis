# Reviewer 4 — Changes requested on durable source-origin planning launch-intent

**Frozen commit:** `9924d0ad24b3e78e0590274775a926244312e66f` (base `fa9a858839394abce61ec350f1f1c780fe10385f`).

The default-off project CAS preparation and immutable source-origin checks are directionally sound, and a first successful admission respects WQ07 global capacity. However **two P1 recovery violations** prevent accepting the advertised two-client/unknown-ack guarantee:

1. **Partial account reservation deadlock:** If WQ07 cap=1 commits its held token but WQ04 project admission is absent/unknown, `admitPreparedExplicit` insists on a fresh empty-capacity global selection before reaching the idempotent lower-layer reservation/reconciliation. It cannot complete the original or follow an explicit verified abort path. See `findings/partial-account-reservation-replay.md`.
2. **Cross-client actor mismatch:** The original admission actor is not stored. A second client's actor changes the request hash for the same WQ07 operation ID and fails WQ04 `originalCreditAt` actor equality on replay. Existing tests reuse one actor string. See `findings/cross-client-admission-actor-replay.md`.

Both issues retain tokens rather than duplicate sends, but fail the task's recovery/liveness requirements. Fix with original-UUID/account-token-aware reconciliation and an immutable admission actor, then add two-client, distinct-actor and account-held/project-missing negative/recovery fixtures.

**Independent verification:** `.NET 11 Release dotnet test Client.Tests/Client.Tests.fsproj --no-restore --filter FullyQualifiedName~WorkQueueDurableLaunchIntentTests|FullyQualifiedName~WorkQueueCrossProjectHostPreviewTests|FullyQualifiedName~WorkQueueWorkReplacementVerticalSliceTests|FullyQualifiedName~WorkQueueVerifiedDispatchAdapterTests`: **19 passed, 0 failed**. Frozen-range `git diff --check`: passed. Tests passing do not cover the above failure states.

**Verdict:** ChangesRequested. Do not integrate or activate the Scheduler on this frozen commit.