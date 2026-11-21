# P1 — Mixed issuance oracle exercises only null source-identity pins

**Frozen candidate:** `87bbadfe0bac60ec4d14f51d382841076f427d18`.

The new `MixedChallengeAttemptFixture.checkCanonical` does directly inspect raw assignment files, and compares `targetRef`, `sourceRepositoryId`, `sourceRemote`, `sourcePushRemote`, `sourceRef`, `sourceBaseCommit`, `specBlobOid` and generation across issuance records (`Client.Tests/ChallengeStateMachinePropertyTests.fs:1267–1277, 1305–1318`). This is valuable, but its **only real-Git mixed fixture is not source-pinned**:

- `MixedChallengeAttemptFixture.prepare` calls `ChallengeDependencyChecks.assign` (`ChallengeStateMachinePropertyTests.fs:1150–1158`).
- `ChallengeDependencyChecks.assign` calls the legacy `ReviewWorkflow.assignTask` (`...fs:507–517`), which delegates to `assignTaskAs`.
- `ReviewWorkflow.assignTaskAs` constructs an attempt with **all six source fields explicitly null**: `BaseCommit`, `TargetRef`, `SourceRepositoryId`, `SourceRemote`, `SourcePushRemote`, `SourceRef` (`Client/Modules/ReviewWorkflow.fs:1791–1799`).
- `checkCanonical` obtains its `pinned` values from that same already-produced attempt and compares later issuance records against that all-null baseline.

Consequently, the fixture can catch null-to-nonnull drift, but **cannot detect a regression that drops/corrupts actual nonempty frozen source provenance during N→N+1 auto/manual requeue**; such an input state never occurs. The requirement is specifically to verify exact source/spec issuance provenance, not merely the presence of JSON fields or preservation of nulls.

**Required:** Keep the existing legacy/null case as a compatibility control, but add a bounded real two-clone mixed case with independently authored **nonempty, valid source repository/ref, base commit and remote identities**, preserved across at least one manual and one automatic generation increment. Assert initial issuance and all later immutable raw assignment/attempt records against those authored pins (including the spec OID), not only against the first production projection. Do not overlay or modify the separately reviewed production candidate.