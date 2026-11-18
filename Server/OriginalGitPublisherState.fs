namespace Jarvis.WorkQueues

open System

// Synthetic P1b state only. No host registry, no production adapter, and no
// affirmative all-client GitHub credential or source-effect witness.
[<RequireQualifiedAccess>]
type OriginalGitFixturePhase =
    | Active
    | FencePending of fenceOperationId: Guid
    | Fenced of fenceOperationId: Guid * epoch: int64
    | RecoveryHold of unresolvedOperationId: Guid option

type OriginalGitFixtureState =
    { Phase: OriginalGitFixturePhase
      FenceEpoch: int64 }

module OriginalGitPublisherState =
    let initial =
        { Phase = OriginalGitFixturePhase.Active
          FenceEpoch = 0L }

    let allowMutation state =
        match state.Phase with
        | OriginalGitFixturePhase.Active -> Ok ()
        | OriginalGitFixturePhase.RecoveryHold _ ->
            Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
        | _ -> Error OriginalGitPublisherFailure.Fenced

    // Must be durably persisted before installing remote exclusion. An
    // unknown/pending external effect never becomes positive by retry.
    let beginFence operationId state =
        if operationId = Guid.Empty then
            Error(OriginalGitPublisherFailure.InvalidEnvelope "FenceOperationMissing")
        else
            match state.Phase with
            | OriginalGitFixturePhase.Active ->
                Ok { state with Phase = OriginalGitFixturePhase.FencePending operationId }
            | OriginalGitFixturePhase.FencePending id when id = operationId -> Ok state
            | OriginalGitFixturePhase.Fenced(id, _) when id = operationId -> Ok state
            | OriginalGitFixturePhase.RecoveryHold _ ->
                Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
            | _ -> Error OriginalGitPublisherFailure.ReusedOperationId

    // Caller must separately prove its disposable remote effect/coverage
    // boundary. This pure reducer CANNOT supply that proof.
    let finishFence operationId state =
        match state.Phase with
        | OriginalGitFixturePhase.FencePending id when id = operationId
                                                          && state.FenceEpoch < Int64.MaxValue ->
            let epoch = state.FenceEpoch + 1L
            Ok { Phase = OriginalGitFixturePhase.Fenced(id, epoch)
                 FenceEpoch = epoch }
        | OriginalGitFixturePhase.Fenced(id, _) when id = operationId -> Ok state
        | OriginalGitFixturePhase.RecoveryHold _ ->
            Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
        | _ -> Error OriginalGitPublisherFailure.Fenced

    let uncertain operationId (state: OriginalGitFixtureState) =
        { state with
            Phase = OriginalGitFixturePhase.RecoveryHold
                (if operationId = Guid.Empty then None else Some operationId) }
