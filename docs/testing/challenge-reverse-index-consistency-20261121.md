# Challenge reverse-index consistency — 2026-11-21

**Task:** `challenge-canonical-reverse-index-consistency-20261121` (Worker 4, attempt `3cddaa627e274a57a89ad51cceea4998`, generation 1).

**Base:** actual `main` / `origin/main` `d702352ef705f9991ac1cb14dc15a13cce78aa44`. All implementation was developed on the isolated branch `fix/challenge-canonical-reverse-index-worker4-20261121`. No direct main publication, deployment, scheduler activation, or notifications.

## Reproducible red evidence

The **unchanged** Worker 7 frozen tests-only source `cd895d493b419e127ededea15f814ab53e484407`, review `a17c4d9f4e584c449c0876a205e317bb` (Reviewer 1, ChangesRequested seq 9780), was temporarily composed into the isolated source worktree, without editing the test fixture. On the unmodified production base, this command with .NET SDK 11.0.100 returned **exit 1, 1 pass / 3 fail / 4 total**:

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeReverseIndexFaultTests --verbosity minimal
```

The failing two-clone disposable bare-Git probes were seed **9000** (orphan N+1 by-Challenge index), **9001** (wrong-owner by-Challenge locator), and **9002** (orphan N+1 by-owner inbox locator). Mode 3 correctly returned a ledger integrity error.

## Authority and fail-closed invariants

- One shared pinned-snapshot verification path validates both index directions, expected Challenge-ID set, canonical owner/task/attempt identity, current assignment generation, and mirrored edges. It rejects missing or extra/future-generation indices instead of silently skipping them.
- Both **current and historical** index generations must match the original `WaitOnChallenges` operation and lifecycle event. Original operation provenance is verified against its immutable first-parent Git origin and request hash; its original path set must include every expected owner and by-Challenge reverse edge. Old entries remain valid but inert following a legitimate N→N+1 requeue and reblock.
- Per-prefix immutable Git *additions* provide a completeness witness even if an attacker deletes every current edge under a Challenge or owner index directory. These reads are scoped to the requested owner or Challenge on the verified snapshot; they do not walk unrelated tasks.
- A canonical blocked wait independently requires its owner edge even if the owner index directory is absent. Linked Challenge records must exist at the pinned snapshot, retain matching IDs and task association, and have a valid status. Cancellation and partial dependencies never make a blocked attempt ready.
- The existing CAS writer remains the only mutation authority. Integrity failures produce no new challenge resolution, requeue, notification, or replacement ledger record.

## Regression coverage

The source-owned `Client.Tests/ChallengeWorkflowTests.fs` additions use real disposable bare-Git remotes and two clients. Negative probes exercise wrong-owner mirrored locator, omitted by-Challenge sibling edge, missing canonical Challenge, missing entire owner edge, and missing entire Challenge edge. All assert rejection before mutation, unchanged verified remote HEAD, and no notifications. Existing tests cover valid N→N+1 reblock, multiple distinct dependents, occupied owner/Eligible release, canceled/mixed dependencies, and replay.

For compatibility verification the exact Worker 7 property test file and its `Client.Tests.fsproj` include were temporarily copied **unchanged** from `cd895d4...` into this isolated worktree. Those copied files are deliberately **excluded** from Worker 4's frozen source/commit; the tests-only source remains independently owned and subject to its own review. Recompose it against Worker 4's production commit to rerun the cross-source gate.

```sh
dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~Challenge --verbosity minimal
```

Review/integration must use the exact source SHA named in the independent review request. Do not integrate this correction or Worker 7's tests-only source without their respective independent reviews and a compatible composed Release gate.

## Cross-task Git trust boundary

The index proof delegates the underlying commit/operation authenticity to the existing `LedgerStore.fetch`, `getOperationAt`, and `getOperationOriginAt` trust boundary. The separate P0 project-ledger clone-local Git replace-ref hardening owned by Worker 1 is **not** reimplemented or claimed fixed here. Before integration, independent reviewers must confirm the combined source prevents local replace/graft/environment substitution at the project ledger reader; this branch alone makes no independent replacement-ref security claim. No shared ledger records were modified or repaired.

## Verified Release results

- SDK: .NET 11.0.100. Before the fix, the exact frozen Worker 7 four-case reverse-index gate returned **1 passed / 3 failed**, matching seeds 9000–9002.
- After the final production corrections, the focused exact composition gate returned **10 passed / 0 failed** (`dotnet test -c Release --filter FullyQualifiedName~ChallengeReverseIndexFaultTests|... `, Jarvis job `b3312266fb0845d896256d7a5f8d232a`).
- The complete composed Challenge-focused gate returned **54 passed / 0 failed / 0 skipped** in **1 minute 46 seconds**, on the final production source with Worker 7's untouched fixture temporarily included (`dotnet test Client.Tests/Client.Tests.fsproj -c Release --no-build --no-restore --filter FullyQualifiedName~Challenge --verbosity minimal`, Jarvis job `fc23f9f272c446ba91713f4fd8d03ee2`).
- The temporary `ChallengeStateMachinePropertyTests.fs` and its project include were restored/removed immediately after this gate. Both are excluded from Worker 4's frozen source; all five source-owned paths are enumerated by the commit.
