# WQ-01 Phase A — independent frozen review #3

**Reviewer:** Reviewer 2  
**Review ID:** `6c4c94a61e584207b37f3a520d5d7ea1`  
**Exact source:** `ecf7b40f98fddb428e0c73d8659a6522298083e2`  
**Accepted architecture/base:** `90ed868d04524d0483ccd979cffb5029d8cf7027`  
**Prior ChangesRequested:** `a4b87d29f5d14dcbbe52e5604a8fb1c2` decision sequence 2897, exact source `143a73a35336305afaab7d796f16f70782982709`  
**Verdict:** **ChangesRequested** — one newly identified P1 hard-dependency gate violation affecting ancillary Role-message work; exact frozen source must not be integrated.

## Inspection and independent validation

Inspected the exact frozen `Server/WorkQueueContracts.fs` state machine (1,259 lines), previous review finding documents, full delta from the prior frozen commit across all three changed files, relevant NUnit fixtures and accepted architecture §6–§7/§11. Verified the registered `worker5-wq01-corrections-2` worktree has this exact SHA and is clean. Independently executed, from that checkout:

- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore --filter FullyQualifiedName~WorkQueueContractTests`: **44/44 PASS**, 0 failed; compilation successful with existing F# warnings.
- `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-build --no-restore`: **179/179 PASS**, 0 failed.
- Exact source `git show`, `git diff` and revision verification all succeeded. Submitter separately reports Release solution build and `git diff --check` PASS; those two claims were not independently rerun by this reviewer.
- No source modifications, merge, deployment, production Scheduler, migration, browser action, or build-script activation performed. Client.Tests and Node tests were not independently run, and none is claimed as passed.

## Prior findings: corrected at this source

1. **P1 FIFO wake timestamp:** `wakeBlockedStages now` now uses separate `AvailableAt=now` and monotonic `AvailableSequence`; all relevant dependency-change CAS paths (completion, hard-edge, cancellation, waiver, external gate resolution) and terminal ancillary requeue receive the same time witness. The new fixture uses 1000/2000/3000 timestamps, multi-wake distinct sequences, deterministic replay, operation ID payload binding and stale-version rejection.
2. **P2 inconsistent Blocked publication:** `normalizePublishedStage` now rejects a clear-gate `Blocked` state with no unmet prerequisite, including Role-message work, and preserves genuinely blocked cyclical/other stages; the new tests cover unrelated edits/completions and Role-message wake on actual prerequisite satisfaction.
3. **Earlier four P1 findings:** blocked wake/replay, injective demand-credit invalidation, typed publish/post/admit operation payload replay and verified source-handoff lineage remain materially addressed by the retained implementation and regression fixtures. These contracts remain pure Phase A seams, not production proof.

## New P1 finding — hard-gated ancillary Role-message completion and recovery

**Canonical finding:** `findings/01-ancillary-role-hard-gate.md`.

An ancillary Role-message stage is legitimately `Claimed` under an active session whose primary work is something else. `setHardPrerequisite` on that claimed Role work correctly installs `DependencyGatePending` when an unfinished hard prerequisite B is added. Two subsequent legal operations violate the accepted gate contract:

- **Safety:** `completeAncillaryMessage` ignores the pending gate/unsatisfied dependencies, marks the message Handled and Role work Completed, allowing `isSatisfied` and `projectDependenciesChanged` to awaken a downstream dependent even though B was not satisfied. There is no analogue of `completeOrdinary`'s gate fence. See `Server/WorkQueueContracts.fs:898-916`, `:936-963`, `:308-312`.
- **Liveness:** if the primary work instead completes first, `releaseAncillaryOnTerminal` returns that Role work to `Available` but retains `Gate=DependencyGatePending`. It is excluded from `orderAvailable`, skipped by `wakeBlockedStages`, and cannot invoke `resolveDependencyGate` because it is no longer a live primary claim. The pending Role message is stranded. See `:774-800`, `:325-329`, `:495-516`, `:802-819`, `:1098-1133`.

Both paths are reachable from valid, explicitly typed snapshots and are independent of the two corrected prior findings. The static transition proof is provided in the finding; no executed reproduction is claimed. A disposable out-of-tree script attempt was denied by the local Jarvis shell execution policy, so it was not run. The existing 44 focused tests do not exercise an ancillary Role-message hard edge added **after** claim.

## Required before acceptance

Gate ancillary Role-message acknowledgements/completion by current hard-edge/graph/claim generations; on primary terminal, reconcile or safely block gated ancillary work instead of producing `Available + DependencyGatePending` without an owning launch. Add two-client negative fixtures for both sequences, reversed CAS race, exact replay, eventual prerequisite completion/waiver wake and preserved other launch credits. Run fresh Release tests, push a new exact commit to an isolated branch, and request a new independent frozen review. Do not mutate this frozen review ref or treat passing pure fixtures as evidence of production Git-ledger CAS, authoritative waivers, browser dispatch/STOP, or all 80 acceptance scenarios.