# Independent frozen WQ-01 review — ChangesRequested

**Review:** `36a90a18735e40eda086c879f78f6f05` | **Reviewer:** Reviewer 1 | **Owner:** Worker 5  
**Exact frozen source:** `4f3b47d85dfe26cbd5a48da37e98f49e4cdfc6bf`  
**Frozen review ref:** `refs/heads/jarvis-review/wq-01-contract-fixtures/36a90a18735e40eda086c879f78f6f05/r1`  
**Accepted design/base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`, `docs/specs/work-queues-and-ephemeral-executions.md`  
**Previous review:** `f07aa51cda7b4e97a13f6c78a9b243ef` (ChangesRequested seq3024, frozen SHA `4b8d76eeccf02b2dd12d0291281401447bc6d700`)  
**Explicit independent verdict: ChangesRequested — 1 P1 and 1 P2. Do not integrate this source.**

## Previously requested corrections — materially addressed

The exact patch against the prior rejected SHA modifies `Server/WorkQueueContracts.fs` and `Server.Tests/WorkQueueContractTests.fs` (332 insertions, 14 deletions). The shared `hasLivePrimaryActivation` predicate now requires a Registered launch, present non-STOP lease, live matching primary claim with positive generation, clear gate, undecided domain and satisfied hard dependencies; both ancillary intake and acknowledgement use it. STOP-before-claim fresh/stale CAS and claim-before-STOP terminal tests fence new Role/Task handler acquisition, preserve primary terminal/recovery and token accounting, and allow exact read-only operation receipt replay.

The formerly unguarded passive Task `WorkId=None` path now calls `completeCoverage` at both claim and acknowledgement. New fixtures cover independently false queue, graph, message and source high-water flags; restoration, stale producer CAS, legitimate passive ack, handler-generation replay and no spurious Scheduler demand. In the intended well-formed Task input state, these changes correct both prior review findings.

I inspected the immutable patch, full surrounding ClaimNextWork/Message/Post/STOP/Terminal/Gate and coverage/credit code, accepted architecture §§5–11, and the existing/new regression fixtures; the prior generation-fence corrections remain. This is a **Phase A pure contract and fixture reference** only, not yet production Scheduler, canonical Git-ledger migration, authenticated waiver or all 80 architecture E2E scenarios.

## Additional independently found contract defects

### P1 — Malformed passive Task WorkId can claim unrelated runnable work and strand the launch

See `findings/01-task-workid-cross-queue-claim.md`. `postMessage` at `Server/WorkQueueContracts.fs:569-575` accepts `Subject.Task` with `roleWork=None` but retains an arbitrary caller-supplied `message.WorkId=Some "Victim"`. `claimAncillaryMessage` at lines 900-950 then treats this as the Role-work branch without requiring Role subject, `WorkKind.RoleMessage(messageId)` or launch/work queue compatibility. An active Lead with Plan as primary can thereby claim an unrelated Available Implementation Victim when no other unclaimed launch credits exist. `completeAncillaryMessage` rejects its non-RoleMessage kind; primary completion through `releaseAncillaryOnTerminal` rejects the same unreconciled claim and cannot write terminal proof. Reject invalid Task shape at publication **and** independently guard the `Some workId` intake path; add malformed-message, cross-queue, stale-CAS and terminal/token regression tests. **Blocking P1 in pure exported contract, not a claimed deployed external exploit.**

### P2 — Dedicated Role-message primary can handle a second Role message as ancillary

See `findings/02-dedicated-role-primary-ancillary-width.md`. A single Lead launch L with only two pending Role messages M1/M2 claims M1 as dedicated primary through `claimNext`. `hasLivePrimaryActivation` and `claimAncillaryMessage` allow L to take M2 as ancillary because its primary `Kind=RoleMessage M1` is never excluded and `matchCredits([])` succeeds. It can finish M2 and then M1 under one dedicated launch, contrary to the accepted one-message dedicated-primary lifecycle. Existing `case_52_53` only verifies M2 remains pending immediately after primary claim with a competing L2 credit and misses this no-credit intake. Fence the dedicated Role primary from opportunistic second-message handling and add the one-launch/two-message negative test, or explicitly revise the accepted architecture if different semantics are intended.

## Independent exact-source validation

| Evidence | Result |
| --- | --- |
| Registered isolated worktree HEAD and cleanliness | Exact `4f3b47d85dfe26cbd5a48da37e98f49e4cdfc6bf`; clean |
| `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueContractTests` | **71/71 PASS** |
| `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` | **206/206 PASS**, no skipped/failed |
| `dotnet build Jarvis.slnx -c Release --no-restore` | **PASS**, 0 errors, 2 unrelated Client/Client.Tests warnings |
| `git diff --check 90ed868..4f3b47d` | **PASS** |
| Node tests | Owner reports three Node suites **108/108 PASS**; not independently rerun here |
| Client.Tests runtime | Not independently run here |
| New counterexamples | Reachable by static inspection; no new negative fixtures were added to the frozen source |

Existing passing tests do not address the two missing negative-space transitions. The source has not been edited by this reviewer.

## Disposition

Maintain frozen review and source refs. Worker 5 should acknowledge this exact verdict, correct both issues in a **new isolated source branch and commit**, preserve STOP/coverage/handler-generation fixes, add deterministic negative and positive-control regressions, revalidate .NET 11 Release, and request another independent frozen review. No Integrator merge, Scheduler/migration activation or deployment is authorized by this ChangesRequested verdict.
