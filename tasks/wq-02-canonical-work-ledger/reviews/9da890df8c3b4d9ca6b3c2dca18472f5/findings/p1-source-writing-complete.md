# P1 — Source-writing Implementation can complete and wake dependents without a source handoff

**Frozen source:** `71fc78d051d6509596389abfa8fe7cb5f444cd1f`

**Evidence:** `Client/Modules/WorkQueueGitLedger.fs:268-275,346-365,366-370,379-414`; inherited `Server/WorkQueueContracts.fs:849-882,734-781`.

`CompleteOrdinary(launch, work)` is forwarded unconditionally to `Contracts.completeOrdinary`. That function rejects undecided `FrozenReview` and `Integration`, but its generic `(_, Gate.Clear, Domain.Undecided)` branch marks **Implementation** Completed without verifying source continuation, Git ref/SHA reachability, mutating jobs or any handoff. Publication permits `WorkKind.Implementation` with `SourceLineage=None` or with an incomplete caller-supplied `SourceLineage`; there is no source attestation in `validInitialStage`. By contrast, new `YieldReadOnly` and `CheckpointReadOnly` explicitly fence Implementation, demonstrating that source-writing transitions were meant to be unavailable until evidence checks exist.

**State-transition counterexample:** Publish an Implementation stage I, optionally with `SourceLineage = Some { ReachableRef=None; ContinuationSha=None; ... }`, and a downstream stage D hard-dependent on I. Admit/dispatch/register/claim I; then call `CompleteOrdinary(L,I)` against the current project version. It succeeds, marks I Completed, emits a terminal launch receipt, and `projectDependenciesChanged` can wake D despite no remotely reachable source or reconciled job evidence. CAS and replay are correct, but the domain transition is not authorized.

**Required correction:** Fail closed on `CompleteOrdinary` for every Implementation/source-writing stage, not just Integration. Introduce a separately verified completion transition only after canonical repository/task/lineage/claim-generation, continuation ref/SHA, outstanding mutating jobs and source/effect reconciliation are proven in the same transaction. If outside current bounded foundation, explicitly reject the transition now and test the rejection. Add cross-clone negative and later verified-handoff positive tests, including D remains Blocked.

**Basis:** Direct source-level transition trace. The 12 focused Git tests cover Planning and Role completions, but not Implementation completion without source proof.