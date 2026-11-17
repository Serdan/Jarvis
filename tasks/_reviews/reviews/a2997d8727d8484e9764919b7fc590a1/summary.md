# Reviewer 2 — Changes Requested (P1)

**Request:** `a2997d8727d8484e9764919b7fc590a1` (`_reviews`)  
**Frozen source:** `6e5d041f16265001b84878018dcf9ebb15b363b1` from `feat/work-operator-approved-manual-grants-worker13-20261117`  
**Base:** `a2e9f80760c42039b35aa010619bc193e67b9e81`

**Blocking finding:** `findings/cross-device-manual-grant-authority.md`. An approved existing-project manual grant is account/name/capability-scoped but not bound to the local client device/project that displayed operator approval. Subsequent tool calls can override `deviceId` to a second same-account client with a distinct same-named project, route `ReceiveManualGrantedCommand`, and bypass that client's local permission prompt. Add hermetic two-device same-name rejection and device/project-bound positive path; retain prospective canonical-ID proof. The source-level path is conclusive; no live host exploitation was performed.

**Independent verification:** exact frozen HEAD, clean checkout; `git diff --check` 0; real local .NET 11 Release `Server.Tests` 309/309 passed; `Client.Tests --filter FullyQualifiedName~PermissionPolicyTests` 32/32 passed; adjacent `ProjectCreationTests|SignalRDispatchTests|WorkQueueReservationPermissionsTests|WorkQueueCredentialTests` 18/18 passed. Green suites do not cover this cross-device negative.

**Preserved requirements:** CSPRNG UUIDv4 explicit bearer and SHA256 RAM verifier, operator-only approve/deny, no fabricated Work reservation, ordinary OAuth per-tool scopes, no new server persistence, scheduled Work durable/default-off. Re-review a new frozen SHA after correction. No source edits, Scheduler, cutover, hosted Actions, or Codex.
