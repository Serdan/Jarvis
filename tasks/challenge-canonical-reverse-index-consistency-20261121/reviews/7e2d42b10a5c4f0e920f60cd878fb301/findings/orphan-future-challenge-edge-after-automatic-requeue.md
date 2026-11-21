# P1 BLOCKING — Owner inbox overlooks a forged future-generation by-Challenge index after automatic requeue

**Reviewed immutable production source:** `48f1a8c6030677960a3d84629c4e29cecf56a132` (Worker 4, only this review request `7e2d42b10a5c4f0e920f60cd878fb301`). **Scope:** `Client/Modules/ReviewWorkflow.fs:863-961,3336-3341`, `Client/Modules/LedgerStore.fs:1306-1327`.

## Independent reproduction

In a disposable, real bare-Git two-clone fixture, compose the unchanged expanded matrix test file and project include from the **separately unreviewed** Worker 7 follow-up source `332020cd50f3a2a3986933763edb8f81251e0b1c` with exact Worker 4 production `48f1a8c6030677960a3d84629c4e29cecf56a132`. Do not interpret this composition as approval of Worker 7's own test source.

Run `dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeReverseIndexFaultMatrixTests --verbosity minimal`. Independent .NET 11.0.100 Release execution, Jarvis job `f683e3a2eded4cba9ba83b5b9f87269f`: **exit 1; 14 passed, 3 failed, 17 total**. The specific **mode 16** trace is:

1. Register owner and create task/attempt generation N=1, Challenge C, and block on C.
2. Satisfy C legitimately; automatic requeue issues N=2. Historical N=1 wait and its by-owner/by-Challenge edges remain valid.
3. Inject one **unauthenticated orphan N=3** `challenge-waits/by-challenge/C/{task}/{attempt}/3.json` with no matching WaitOnChallenges operation, no N=3 assignment, and no mirrored owner edge.
4. Read `ReviewWorkflow.getAgentInbox` for the genuine owner. **Actual: `Ok`**; expected `LedgerIntegrityError`. The fixture verified remote HEAD unchanged during the read (`a6db46c7f8b708b1d3474ddabe06ae71a46a67a7` before and after). This is not a test harness exception.

## Root cause and impact

`verifyOwnerWaitIndexesAt` enumerates only `challenge-waits/by-owner/{owner}`; `verifiedWaitIndexAt` checks required counterpart by-Challenge edges, but never looks for **unexpected extra** records in the known linked Challenge's own prefix. A historical legitimate owner index thus verifies successfully even when its associated Challenge index contains an unissued future-generation edge. The new per-prefix completeness check only detects **missing historically added paths**, not this extra path. This violates the task's exact two-direction completeness and fail-closed integrity contract. It is an integrity/read-authority failure; this trace does **not** establish that the forged edge independently requeues or alters the task.

## Required correction

On owner-scoped verification, corroborate completeness of by-Challenge paths for the **known, historically linked Challenge IDs** against issued, pinned canonical wait/operation/lifecycle provenance. Reject any unissued generation/path with `LedgerIntegrityError`. Keep reads bounded to related Challenges/owner, leave legitimate N->N+1->N+2 historical edges valid, and do not heal, delete, requeue, notify, or mutate corrupted canonical history. Add a two-clone negative regression for the post-requeue orphan and a positive history control, then request a new immutable review SHA. A phantom Challenge ID with no canonical link (expanded mode 8) has a different, global-discovery visibility problem; do not claim a bounded owner-scoped reader can find every arbitrary unknown prefix without an explicit index design.