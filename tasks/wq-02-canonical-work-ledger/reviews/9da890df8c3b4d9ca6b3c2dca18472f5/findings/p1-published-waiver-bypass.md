# P1 — Stage publication accepts an unverified prerequisite waiver

**Frozen source:** `71fc78d051d6509596389abfa8fe7cb5f444cd1f`

**Evidence:** `Client/Modules/WorkQueueGitLedger.fs:253-275,346-357,418-423`; inherited `Server/WorkQueueContracts.fs:315-337,536-572`.

`validInitialStage` validates initial state/generation/gate/domain, but does **not** require `WaivedDependencies` to be empty or canonical-account-authorized. `witnessNewStage` trusts `stage.WaivedDependencies.ContainsKey(prereq)` when deciding whether a stage has an unmet dependency. `Contracts.publishStage` and `Contracts.ready` likewise treat that map entry as satisfied, without checking a waiver receipt. The structural check only checks that prerequisite IDs exist, not waiver provenance.

**State-transition counterexample:** Publish unfinished B. Then `PublishStage D` with `D.Dependencies={B}`, `D.WaivedDependencies={B -> "arbitrary"}`, `State=Available`, `StageGeneration=1`, `Gate=Clear`. The new adapter accepts this shape, gives D a witnessed FIFO position, and `orderAvailable`/a compatible `ClaimNext` may schedule D while B remains unfinished. The analogous `PostMessage(M, Some roleWork)` path also accepts a pre-populated waiver map. This defeats hard-dependency ordering in the *bounded foundation*, not just at future production activation.

**Required correction:** For new stage/Role-message publication, reject pre-populated waiver maps; a separate, versioned mutation with verified account authorization and original hard-edge/stage-generation provenance must be the only way to add a waiver. In persisted-state validation, ensure any nonempty waiver map has corresponding authority evidence, or fail closed until that evidence can be represented. Add real two-clone Git tests for forged prepublication waivers, both stage and Role work, plus legal B completion/wake.

**Basis:** Independently traced source/state machine; not covered by the 12 existing WQ-02 tests. No source files altered.