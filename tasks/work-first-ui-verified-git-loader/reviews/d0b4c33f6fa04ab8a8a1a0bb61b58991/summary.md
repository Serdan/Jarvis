# Independent follow-up review — Work-first verified Git dashboard loader

**Frozen source:** `aeab8b208deafda979339249ead473ba8c7f7809` (base `ee50af8b68694e0e5fda1c28a251d07db3363aab`).

**Verdict:** RequestChanges. See `findings/sha256-head-format-rejected.md` (P2): the new `validHead` only accepts 40-character Git commit identifiers, whereas the canonical ledger accepts both SHA-1 40-character and SHA-256 64-character verified Git IDs. This incorrectly renders an approved SHA-256 project's Work view UNKNOWN. Share the ledger OID grammar and add positive SHA-256 fixtures.

**Positive checks:** This follow-up fixes the prior WQ04 provenance false-positive: it only accepts an exact positive same-head role-catalog proof string and rejects unknown or mismatched role authority. The code retains a default-off, read-only host-injected port, deterministic approved multi-project membership, per-project UNKNOWN when remote unavailable, and no silent fallback to legacy agent data. No production host or scheduler activation is wired.

**Reviewer test:** `dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueDashboardReadBinding`: **7 passed, 0 failed**. The authored fixtures cover two remotes, missing WQ04 proof, remote outage, missing binding, incomplete membership, duplicates and absent host source; they do not cover SHA-256 Git object IDs.

**Scope:** The operator-approved registry/host installation gap is acknowledged as intentionally unwired, not a finding.