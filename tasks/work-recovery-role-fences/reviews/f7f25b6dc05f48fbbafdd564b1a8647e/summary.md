# Reviewer 5 — Changes requested (G0 Recovery correction re-review)

**Frozen source:** `46b7c9da90d755a8c7f1f85c4ca16b7eeb9838e1`, base `7712bc547c264873036975e30194da03a05cc0ba`, target `refs/heads/main`.

**Result:** **ChangesRequested** due to one independent P1 safety finding: [captured in-flight launch is not bound to its original operation](findings/captured-inflight-launch-not-operation-bound.md). In `WorkRecovery.fs:434-437`, any Launch journal matching Work ID/generation satisfies `CapturedInFlightLaunch=true`, even when the actual captured launch remains unresolved. An unrelated terminal launch can therefore allow normal dispatch after an authorization is created. Preserve identity-based, fail-closed reconciliation; add adversarial same-generation launch regression.

**Prior two P1 corrections addressed:** Original immutable source/review/integration/approval lineage now binds Work version, generation, graph version and role authority; integration explicitly checks review and original approval. A deterministic SHA-256 length-prefixed transcript covers the exact source/review/integration payload, STOP and gap seals and effect journals in the release CAS. Negative tests cover same-SHA prior-generation/role/graph replay and same-receipt changing proof contents.

**Independent evidence:** Read entire exact G0 module and focused tests, compared predecessor commit `a7a835a85ff79667e54fed0ccc17e75dd4de2a21` with the correction, verified clean registered worktree and four-file pure G0 scope. `git diff --check` clean. Independently ran `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` using .NET 11 Release: **321/321 passed**, no skipped or failed tests. No live host, Scheduler, restore, cutover, deployment, Actions or Codex used.

This review applies only to default-off G0. G1 integration and production cutover remain separately gated even after further corrections.
