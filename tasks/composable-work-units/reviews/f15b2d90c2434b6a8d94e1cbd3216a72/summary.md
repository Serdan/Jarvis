# Independent review — composable-work-units, first slice

**Verdict: Changes requested** on frozen head `45b9d9582ddd55fc46a5c27ac41509b24f1c4d8c` (base `944bca7fa38cb5e973b04ce7829966cca351202b`).

The proposed slice adds a pure recursive Project/System/Feature composition graph and a single-project canonical Git-CAS persistence adapter. It correctly separates parent acceptance from child acceptance, retains historical proof snapshots, checks containment and dependency cycles, rejects unsupported public acceptance without a verifier, and does not create a second execution authority or activate the production scheduler.

**Release tests:** `dotnet test Server.Tests/Server.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkComposition` passed 11/11; `dotnet test Client.Tests/Client.Tests.fsproj --configuration Release --filter FullyQualifiedName~WorkComposition` passed 2/2.

**Required correction:** See `findings/ready-without-eligibility.md` (P2). `phase` reports `Runnable` on `WorkState.Available` without the same gate, dependency, completeness, and migration eligibility required by the canonical WQ/admission path. Fix the contract or distinguish a merely published stage from an actually dispatchable one, and test negative cases.

This review concerns the stated **first implementation slice** only. Deferred canonical evidence binding, stage publication, lazy reactive wake, and production operator APIs are not counted as defects in this intentionally partial submission.
