# Reviewer 2 — independent historical cancellation proof review

Frozen source: `d1b528bb47a018b1904adf7659454bda325a43c1`; base: `e4a874f67e4b13bf465c655e28da338ed97122c3`.
**Verdict: Accept for the narrowly scoped historical cancelled-child acceptance-proof persistence correction.** No blocking finding in the reviewed two-file diff.

## Evidence and reasoning

- `Server/WorkComposition.fs:133-138` defines a cancelled-child proof as `-child.SpecVersion`; `Reopen` bumps the spec version at lines 345-350. A child cancelled for the second time therefore has version `-2`, not `-1`. The prior `Client/Modules/WorkCompositionGitLedger.fs:37-45` validator only accepted negative `-1`, erroneously rejecting valid immutable parent acceptance histories on canonical reload.
- The new ledger validator `version < 0 && version >= -child.SpecVersion` accepts historical cancellations from any positive spec version not greater than the child's current monotonically increasing spec version. It preserves checks that the child still exists in the graph, that positive graph acceptance versions are positive, and that the overall graph, canonical serialization/hash and ledger identity are sound (WorkCompositionGitLedger.fs:28-80). It does not claim to independently authenticate a historical cancellation event from a proof alone; canonical acceptance creation still requires the existing independently verified Git-CAS acceptance adapter (WorkCompositionGitLedger.fs:112-155).
- Current parent acceptance is recomputed from the **current** set of children and their exact current evidence, rather than treating a historical proof as an evergreen completion bit (Server/WorkComposition.fs:140-179). New corrective children invalidate current parent acceptance without deleting old immutable accepted targets.
- The new two-clone disposable Git test explicitly creates a cancel/reopen/cancel cycle with child proof `-2`, writes a canonical envelope for a pre-proven pure graph as a **test fixture**, reads it from another clone, performs a legitimate CAS addition of corrective work, then verifies old proof is retained but no longer current. No runtime production evidence verification bypass is introduced by the test.
- Independent .NET 11 **Release** verification in frozen worktree `worker4-composable-work-units-20261116`:
  - `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~WorkCompositionLedgerTests --no-restore`: **4/4 passed**.
  - `dotnet test Server.Tests/Server.Tests.fsproj -c Release --filter FullyQualifiedName~WorkCompositionTests --no-restore`: **17/17 passed**.
  - `git diff --check e4a874f67e4b13bf465c655e28da338ed97122c3 d1b528bb47a018b1904adf7659454bda325a43c1`: clean.

This verdict does not certify the entire composable-work epic, integration, or downstream scheduler activation; it is confined to the frozen historical-proof validation change.