# Independent Review #4 — ChangesRequested

**Task:** `wq-01-contract-fixtures` (Worker 5)  
**Frozen review:** `d6b52a9cc4c8478e83c4e24f39145a7d`  
**Exact reviewed SHA:** `00a6b6f63d45651f6b619d5b52f71f427afa851e` (parent `ecf7b40f98fddb428e0c73d8659a6522298083e2`)  
**Reviewed source:** `refs/heads/jarvis-review/wq-01-contract-fixtures/d6b52a9cc4c8478e83c4e24f39145a7d/r1`  
**Decision:** **ChangesRequested — 1 P1 contract liveness blocker.** Do not integrate this frozen commit.

## Outcome

The two P1 errors from review #3 are corrected within the cases exercised:
- `completeAncillaryMessage` now requires the claimed Role work, clear gate, and satisfied current prerequisites in the atomic project CAS. The new fixtures prove hard-edge-first denies Handled/Completed, stale clones reject, and complete-first denies late hard-edge insertion; original operation-ID replay is covered.
- `releaseAncillaryOnTerminal` now fences pure, side-effect-free Role work and changes an unresolved gated ancillary claim to coherent `Blocked + Gate.Clear + message Pending` upon the *unrelated primary launch's terminal*, or to `Available` with a newly witnessed FIFO key once satisfied. Fixtures exercise subsequent prerequisite completion, waiver, credit rematching, and invalid source-effect state.

The earlier four P1 and two FIFO/Blocked publication corrections are retained in the source; changed code is localized to the ancillary completion/terminal paths and shared satisfaction predicate. These existing regression tests passed independently.

## Blocking finding

**[P1] Gated ancillary Role-message handler has no independent resolution while primary remains active.** See `findings/01-ancillary-gate-resolution-with-active-primary.md` for the exact code path, reachable Plan/W/B/D interleaving, the failed `resolveDependencyGate` primary-work check, and required CAS-safe recovery and tests.

A new W->B dependency can gate W while W is ancillary to launch L's unrelated primary Plan. Later completion/waiver of B does not clear W's `DependencyGatePending`; `completeAncillaryMessage` rejects, and `resolveDependencyGate` calls `finishClaim`, which rejects because `L.PrimaryWorkId=Plan`, not W. Only terminating Plan releases W. An ongoing Plan can therefore strand an operator Role message despite all prerequisites being satisfied. This violates accepted §11's explicit ability to answer ancillary role messages without waiting for primary completion. Introduce an ancillary-only safe resolution/requeue CAS preserving Plan/launch/token; add positive and racing negative fixtures.

## Independent validation

- Worktree HEAD confirmed exact frozen SHA; clean checkout.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~ServerWorkQueueContractTests --nologo`: **47/47 PASS**, 0 skipped.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --nologo`: **182/182 PASS**, 0 skipped.
- `dotnet build Jarvis.slnx -c Release --no-restore --nologo`: **PASS**, 0 errors, two unrelated existing F# warnings in Client/Program.fs and Client.Tests/BrowserSchedulesTests.fs.
- `git diff --check ecf7b40f..00a6b6f6`: **PASS**. The broader base-to-head diff has an unrelated trailing whitespace in `docs/specs/work-queues-phase-a-contracts.md:50` inherited from previous commits; non-blocking.
- Did **not** execute Client.Tests or Node suites independently; only Server F# source/tests changed in this correction patch.

**Boundary:** WQ-01 supplies pure Phase A contracts and deterministic fixture semantics, not production Git-ledger CAS, real waiver authorization, dispatch, deployment, or proof of all 80 architecture acceptance scenarios. The blocking finding is a reachable model defect, not a claim of a production incident.

**Next action for Worker 5:** read this review and its finding, acknowledge the decision sequence, correct on a NEW isolated source ref and SHA, add a regression where B completes/waiver commits *before* unrelated Plan terminates, rerun Release suites, submit a new independent frozen review; retain this review source/ref unchanged. No integration until Accepted.
