# Independent G0 correction review — Accepted recommendation

**Frozen source:** `f84cc33e2a94a81a2d5215f3728dcf47ab8d1403` against exact G0 base `7712bc547c264873036975e30194da03a05cc0ba`. Scope is the new pure `Server/WorkRecovery.fs`, `Server.Tests/WorkRecoveryTests.fs`, and their compile includes. This is **G0 only**, not live restore, Scheduler wiring, or SQLite cutover authorization.

## Findings

No actionable blocker found in this frozen G0 correction.

The previously unsafe inference from an arbitrary same-Work/same-generation terminal launch is closed: `CapturedLaunchIdentity` carries original project/Work/generation/operation ID; `capturedLaunchSettled` requires an exact same-ID launch journal with verified, job-reconciled terminal or definitive no-effect evidence. A missing, malformed, duplicate or inconsistent captured launch remains fenced, and an unrelated terminal launch cannot clear it. The recovered launch identity set is included in a versioned SHA-256 evidence transcript, not merely represented by reusable receipt IDs.

The pure gate also requires explicit canonical source/review/integration proof lineage, account/project sequence-gap seals, STOP clearances and a matching scoped recovery release authorization. Cross-scope duplicate external operation IDs block release. The release digest binds the typed underlying proof payloads and effect outcomes; reusing old IDs after a meaningful payload mutation invalidates authorization. `NormalDispatchAllowed` is true only for appropriately authorized unchanged Work, and the Scheduler is explicitly required to repeat current DB version/STOP/authority checks in a single transactional gate when G1 is implemented.

## Independent checks

- Read the exact frozen source and negative tests, including original-launch identity regression cases, all-captured-launches terminal coverage, authorization-digest tampering, changed lineage, cross-project effects and stale gap seals.
- Local `.NET SDK 11.0.100`; `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --no-restore`: **324 passed, 0 failed, 0 skipped** on this worktree.
- Focused `FullyQualifiedName~ServerWorkRecoveryTests` Release suite: **23 passed, 0 failed**.
- `git diff --check 7712bc54 f84cc33e`: **exit 0**.

**Boundary:** The boolean “verified” facts are typed *adapter inputs*, not an authenticity mechanism. G1 must independently validate canonical original evidence, integrate with restored SQLite and Scheduler atomic launch fences, and pass its own separate frozen review after the E/F dependencies are integrated. Acceptance here does not authorize real host restore, live launch, deployment, or old/new writer cutover.