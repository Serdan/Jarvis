module Client.LegacyDirectWriterFence

open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.LegacyWriterProtocolFence
open Jarvis.WorkQueues

// A verified original Git operation sequence does NOT establish the all-client
// pending intent journal, source-effect journal or minimum-client high-water.
// These evidence gaps remain explicit cutover NO-GO conditions.
type LegacyDirectHighWaterObservation =
    { AccountHead: string
      ProjectHead: string
      ProjectOperationHighWater: int64
      OriginalPendingJournal: string
      OriginalSourceEffectJournal: string
      MinimumClientProtocol: string }

module LegacyDirectWriterFence =
    let private invalid reason =
        InvalidBinding("OldWriterFenceUnknown/" + reason)

    // No caller-supplied actor, intent Kind, role, client label or version can
    // exempt a write: this wrapper is used at concrete legacy workflow
    // publication call sites, not at the universal Work-native Git transport.
    let internal decideAt (scope: LegacyFenceScope) (binding: LedgerBinding)
                          (snapshot: LedgerSnapshot) =
        if not (LegacyWriterProtocolFence.scopeValid scope) then
            LegacyWriterDecision.Unknown "FenceProtocolOrAuthorityInvalid"
        elif not (LegacyWriterProtocolFence.sameProjectAuthority scope.Project binding) then
            LegacyWriterDecision.Unknown "DirectProjectAuthorityMismatch"
        else
            match AccountCapacityLedger.fetch scope.Account with
            | Error _ -> LegacyWriterDecision.Unknown "AccountEpochUnreadable"
            | Ok account when account.State.MigrationPhase = "LegacyActive" ->
                // An independently initialized WQ02 Frozen fixture is NOT a
                // freeze of original persistent Agent/Manager writers.
                LegacyWriterDecision.Allowed
            | Ok account when account.State.MigrationPhase = "Aborted" ->
                LegacyWriterDecision.Unknown "AbortedEpochRequiresOperatorReconciliation"
            | Ok account when not (List.contains account.State.MigrationPhase
                                       [ "Frozen"; "Preparing"; "Validated"; "Activated" ]) ->
                LegacyWriterDecision.Unknown "UnknownAccountMigrationPhase"
            | Ok account when account.Head <> scope.ExpectedAccountHead ->
                LegacyWriterDecision.Unknown "AccountEpochHeadChanged"
            | Ok _ when snapshot.Commit <> scope.ExpectedProjectHead ->
                LegacyWriterDecision.Unknown "ProjectEpochHeadChanged"
            | Ok account ->
                match WorkQueueGitLedger.loadAt binding snapshot with
                | Error _ -> LegacyWriterDecision.Unknown "ProjectMigrationProofMissing"
                | Ok project when project.Migration = MigrationEpoch.Frozen
                                  || project.Migration = MigrationEpoch.Preparing
                                  || project.Migration = MigrationEpoch.Validated
                                  || project.Migration = MigrationEpoch.Activated ->
                    LegacyWriterDecision.Blocked(
                        "OldWriterFenced", account.Head, snapshot.Commit)
                | _ -> LegacyWriterDecision.Unknown "ProjectMigrationEpochMismatch"

    let private authorize scope binding snapshot =
        match decideAt scope binding snapshot with
        | LegacyWriterDecision.Allowed -> Ok ()
        | LegacyWriterDecision.Blocked(code, accountHead, projectHead) ->
            Error(InvalidBinding(
                $"{code}: direct legacy write denied at original account {accountHead} and project {projectHead}."))
        | LegacyWriterDecision.Unknown reason -> Error(invalid reason)

    let internal transactWithScope (scope: LegacyFenceScope)
                                   (binding: LedgerBinding) (intent: LedgerIntent)
                                   (plan: LedgerSnapshot -> Result<LedgerFileChange list, LedgerFailure>) =
        // Preflight rejects replay of an already-applied UUID after freeze:
        // LedgerStore returns such a receipt without running its planner.
        match LedgerStore.fetch binding with
        | Error error -> Error error
        | Ok first ->
            match authorize scope binding first with
            | Error error -> Error error
            | Ok () ->
                // On each losing original project Git CAS the planner runs at
                // the new head and re-evaluates the verified account epoch.
                LedgerStore.transact binding intent (fun current ->
                    authorize scope binding current
                    |> Result.bind (fun () -> plan current))

    // Legacy review/integration workflows also publish immutable *source Git
    // refs* before their canonical ledger transaction. Guard those actual
    // irreversible side-effect boundaries, not merely their later CAS writes.
    let preflightSourcePublication (binding: LedgerBinding) =
        match LegacyWriterProtocolFence.readHostScope () with
        | Error reason -> Error(invalid reason)
        | Ok None -> Ok ()
        | Ok(Some scope) when not (LegacyWriterProtocolFence.scopeValid scope) ->
            Error(invalid "FenceProtocolOrAuthorityInvalid")
        | Ok(Some scope) when not (LegacyWriterProtocolFence.sameProjectAuthority scope.Project binding) ->
            Error(invalid "DirectProjectAuthorityMismatch")
        | Ok(Some scope) ->
            LedgerStore.fetch binding
            |> Result.bind (authorize scope binding)

    // LedgerStore.findApplied returns a prior success without reaching the
    // transaction planner. Direct workflows must check the enabled host fence
    // before inspecting an old UUID, including when no ref is published.
    // Default-off and LegacyActive keep their original idempotency semantics.
    let findAppliedForLegacyWrite (binding: LedgerBinding) (intent: LedgerIntent) =
        preflightSourcePublication binding
        |> Result.bind (fun () -> LedgerStore.findApplied binding intent)

    let transact (binding: LedgerBinding) (intent: LedgerIntent)
                 (plan: LedgerSnapshot -> Result<LedgerFileChange list, LedgerFailure>) =
        match LegacyWriterProtocolFence.readHostScope () with
        | Error reason -> Error(invalid reason)
        | Ok None -> LedgerStore.transact binding intent plan // DEFAULT OFF
        | Ok(Some scope) -> transactWithScope scope binding intent plan

    // Inspection only. Do not promote Git's project NextSequence into a
    // fictitious original local intent journal or historical source witness.
    let inspectHighWater (binding: LedgerBinding) =
        match LegacyWriterProtocolFence.readHostScope () with
        | Error reason -> Error(invalid reason)
        | Ok None -> Error(invalid "DirectFenceNotEnabled")
        | Ok(Some scope) when not (LegacyWriterProtocolFence.scopeValid scope)
                              || not (LegacyWriterProtocolFence.sameProjectAuthority scope.Project binding) ->
            Error(invalid "DirectProjectAuthorityMismatch")
        | Ok(Some scope) ->
            match AccountCapacityLedger.fetch scope.Account, LedgerStore.fetch binding with
            | Ok account, Ok project ->
                Ok { AccountHead = account.Head
                     ProjectHead = project.Commit
                     ProjectOperationHighWater = project.Header.NextSequence - 1L
                     OriginalPendingJournal = "Unknown: no authenticated all-client intent journal witness"
                     OriginalSourceEffectJournal = "Unknown: no original source-effect high-water witness"
                     MinimumClientProtocol = "Unknown: no connected and disconnected fleet proof" }
            | _ -> Error(invalid "OriginalGitAuthorityUnavailable")
