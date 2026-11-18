namespace Jarvis.WorkQueues

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

// Internal, disposable fixture adapter ONLY. This interface is injected by
// test infrastructure; it does not establish hosted GitHub credential
// exclusivity or authorize real original-Git mutations.
type internal IOriginalGitPublisherFixtureRemote =
    abstract member AuthenticatedIdentity: unit -> OriginalGitPublisherIdentity option
    abstract member TrustedInventory: unit -> OriginalGitPublisherTarget list
    abstract member ReadHead:
        OriginalGitPublisherTarget -> Result<string option, OriginalGitPublisherFailure>
    // Must perform the actual remote update-ref/CAS, not issue a mayPush token.
    abstract member ExactRemoteCas:
        OriginalGitPublisherCas -> Result<unit, OriginalGitPublisherFailure>
    // For disposable remotes, independently install/verify deny-all receive
    // hooks and drain in-flight effects under the fixture's exclusive access.
    // This cannot be mapped to a hosted GitHub all-client witness.
    abstract member InstallFixtureRemoteDenial:
        OriginalGitPublisherTarget list -> Result<unit, OriginalGitPublisherFailure>
    abstract member VerifyFixtureRemoteDenial:
        OriginalGitPublisherTarget list -> Result<unit, OriginalGitPublisherFailure>
    // Entire ORIGINAL pending-intent/source-effect history, not the new journal.
    abstract member ReadOriginalHistory:
        unit -> Result<int64 * int64, OriginalGitPublisherFailure>

module private OriginalGitPublisherFixtureHash =
    let digest (values: string array) =
        JsonSerializer.Serialize values
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun x -> x.ToLowerInvariant()

    let target (t: OriginalGitPublisherTarget) =
        let purpose =
            match t.Purpose with
            | OriginalGitRefPurpose.ProjectLedger -> "project"
            | OriginalGitRefPurpose.AccountLedger -> "account"
            | OriginalGitRefPurpose.SourceEffect -> "source"
        String.concat ":" [
            purpose; t.AccountId.ToString("N")
            t.ProjectId |> Option.map (fun p -> p.ToString("N")) |> Option.defaultValue ""
            t.LedgerId.ToString("N"); string t.LedgerEpoch
            t.CanonicalRepositoryId; t.ApprovedRemoteFingerprintSha256; t.GitRef
        ]

    let cas (operation: OriginalGitPublisherCas) =
        digest [|
            target operation.Target
            operation.Publisher.PublisherId.ToString("N")
            operation.Publisher.DeviceId.ToString("N")
            string operation.Publisher.ProtocolVersion
            string operation.Publisher.CredentialGeneration
            operation.OperationId.ToString("N")
            operation.RequestSha256
            operation.ExpectedRemoteHead |> Option.defaultValue ""
            operation.ProposedRemoteHead |> Option.defaultValue ""
            operation.SourceEffectOperationId |> Option.map (fun x -> x.ToString("N")) |> Option.defaultValue ""
        |]

    let targets (inventory: OriginalGitPublisherTarget list) =
        inventory
        |> List.map target
        |> List.sort
        |> List.toArray
        |> digest

    let witness (operationId: Guid) (operatorDecisionId: Guid) (epoch: int64) targetHash (highWater: int64 * int64) (heads: (OriginalGitPublisherTarget * string option) list) =
        let sorted =
            heads
            |> List.map (fun (target, head) -> target.CanonicalRepositoryId + ":" +
                                                 target.GitRef + "=" + (head |> Option.defaultValue "<deleted>"))
            |> List.sort
        digest (Array.ofList (
            [operationId.ToString("N"); operatorDecisionId.ToString("N"); string epoch; targetHash
             string (fst highWater); string (snd highWater)] @ sorted))

type internal OriginalGitPublisherFixtureExecutor(
        store: OriginalGitPublisherStore,
        remote: IOriginalGitPublisherFixtureRemote) =

    let inventory () =
        let trusted = remote.TrustedInventory()
        if isNull (box trusted) || trusted.IsEmpty then
            Error OriginalGitPublisherFailure.UntrustedAuthority
        elif trusted
             |> List.exists (fun target ->
                 OriginalGitPublisherAdmission.validateTarget target <> Ok ()) then
            Error OriginalGitPublisherFailure.UntrustedAuthority
        elif trusted
             |> List.map (fun t -> t.CanonicalRepositoryId, t.GitRef)
             |> List.distinct
             |> List.length <> trusted.Length then
            Error OriginalGitPublisherFailure.UntrustedAuthority
        else Ok trusted

    let readHeads trusted =
        trusted
        |> List.fold (fun state target ->
            match state with
            | Error failure -> Error failure
            | Ok heads ->
                match remote.ReadHead target with
                | Error error -> Error error
                | Ok head -> Ok ((target, head) :: heads)) (Ok [])
        |> Result.map List.rev

    let receipt (operation: OriginalGitPublisherCas) seq epoch hash =
        { OriginalOperationId = operation.OperationId
          CanonicalRequestSha256 = operation.RequestSha256
          Target = operation.Target
          OldHead = operation.ExpectedRemoteHead
          NewHead = operation.ProposedRemoteHead
          AppliedBarrierEpoch = epoch
          RemoteReceiptSha256 = hash }

    let uncertain operationId =
        // Persist uncertainty if possible; on disk-write failure the
        // originally committed Prepared entry is held on next restart.
        try store.MarkUnknown operationId with _ -> ()
        Error(OriginalGitPublisherFailure.OutcomeUnknown operationId)

    member _.PublishCas(operation: OriginalGitPublisherCas) =
        lock store.Lane (fun () ->
            match OriginalGitPublisherAdmission.validateCas operation, inventory () with
            | Error error, _ -> Error error
            | _, Error error -> Error error
            | Ok (), Ok trusted ->
                match remote.AuthenticatedIdentity() with
                | None -> Error OriginalGitPublisherFailure.UntrustedAuthority
                | Some authorized when authorized <> operation.Publisher ->
                    Error OriginalGitPublisherFailure.UntrustedAuthority
                | Some _ ->
                    match OriginalGitPublisherAdmission.validateCasAgainstTrustedInventory trusted operation with
                    | Error error -> Error error
                    | Ok () ->
                        let envelope = OriginalGitPublisherFixtureHash.cas operation
                        match store.Prepare(operation.OperationId, envelope) with
                        | Error error -> Error error
                        | Ok(OriginalGitFixturePrepare.Replay prior) ->
                            match prior.Status, prior.ReceiptSha256 with
                            | OriginalGitFixtureCasStatus.Applied, Some hash ->
                                Ok (receipt operation prior.Sequence 0L hash)
                            | OriginalGitFixtureCasStatus.RejectedStale, _ ->
                                Error OriginalGitPublisherFailure.StaleRemoteHead
                            | _ -> Error(OriginalGitPublisherFailure.OutcomeUnknown operation.OperationId)
                        | Ok(OriginalGitFixturePrepare.Fresh sequence) ->
                            // The committed Prepared row predates every external
                            // action. A timed-out child can continue after crash.
                            try
                                match remote.ReadHead operation.Target with
                                | Error _ -> uncertain operation.OperationId
                                | Ok current when current <> operation.ExpectedRemoteHead ->
                                    store.MarkRejectedStale operation.OperationId
                                    Error OriginalGitPublisherFailure.StaleRemoteHead
                                | Ok _ ->
                                    match remote.ExactRemoteCas operation with
                                    // An error returned *after* initiating remote CAS
                                    // cannot prove that no ref changed, even when typed
                                    // StaleRemoteHead. Only pre-effect ReadHead mismatch
                                    // is safe to journal as a definite stale rejection.
                                    | Error _ -> uncertain operation.OperationId
                                    | Ok () ->
                                        match remote.ReadHead operation.Target with
                                        | Ok observed when observed = operation.ProposedRemoteHead ->
                                            let epoch = store.ReadState().FenceEpoch
                                            let hash =
                                                OriginalGitPublisherFixtureHash.digest [|
                                                    envelope; string sequence
                                                    operation.ExpectedRemoteHead |> Option.defaultValue ""
                                                    operation.ProposedRemoteHead |> Option.defaultValue ""
                                                    string epoch
                                                |]
                                            store.MarkApplied(operation.OperationId, hash)
                                            Ok (receipt operation sequence epoch hash)
                                        | _ -> uncertain operation.OperationId
                            with _ ->
                                uncertain operation.OperationId)

    member this.AcquireExclusiveFixtureBarrier(request: OriginalGitPublisherFenceRequest) =
        lock store.Lane (fun () ->
            match inventory () with
            | Error error -> Error error
            | Ok trusted ->
                match OriginalGitPublisherAdmission.validateFenceRequestAgainstTrustedInventory trusted request with
                | Error error -> Error error
                | Ok () when trusted.Length <> request.Targets.Length ||
                             Set.ofList trusted <> Set.ofList request.Targets ->
                    Error OriginalGitPublisherFailure.UnknownCredentialChannel
                | Ok () ->
                    let targetSha = OriginalGitPublisherFixtureHash.targets trusted
                    match store.BeginFence(request.FenceOperationId, request.OperatorDecisionId, targetSha) with
                    | Error error -> Error error
                    | Ok { Phase = OriginalGitFixturePhase.Fenced _ } ->
                        match store.ReadBarrier() with
                        | None -> Error OriginalGitPublisherFailure.UntrustedAuthority
                        | Some record ->
                            let prior =
                                { FenceOperationId = record.OperationId; FenceEpoch = record.FenceEpoch
                                  ExactTargets = trusted; RemoteWitnessSha256 = record.WitnessSha256
                                  OriginalPendingIntentHighWater = record.OriginalPendingHighWater
                                  OriginalSourceEffectHighWater = record.OriginalSourceEffectHighWater }
                            this.ReinspectFixtureBarrier prior
                    | Ok _ ->
                        // A durable FencePending is written before any remote
                        // hook installation. Incompleteness leaves it pending.
                        try
                            match remote.InstallFixtureRemoteDenial trusted with
                            | Error error -> Error error
                            | Ok () ->
                                match remote.VerifyFixtureRemoteDenial trusted,
                                      remote.ReadOriginalHistory() with
                                | Ok (), Ok highWater when fst highWater >= 0L && snd highWater >= 0L ->
                                    match readHeads trusted with
                                    | Error error -> Error error
                                    | Ok heads ->
                                        let nextEpoch = store.ReadState().FenceEpoch + 1L
                                        let witness =
                                            OriginalGitPublisherFixtureHash.witness
                                                request.FenceOperationId request.OperatorDecisionId nextEpoch targetSha highWater heads
                                        match store.FinishFence(request.FenceOperationId, targetSha,
                                                               witness, fst highWater, snd highWater) with
                                        | Error error -> Error error
                                        | Ok record ->
                                            Ok { FenceOperationId = record.OperationId
                                                 FenceEpoch = record.FenceEpoch
                                                 ExactTargets = trusted
                                                 RemoteWitnessSha256 = record.WitnessSha256
                                                 OriginalPendingIntentHighWater = record.OriginalPendingHighWater
                                                 OriginalSourceEffectHighWater = record.OriginalSourceEffectHighWater }
                                | Error error, _ -> Error error
                                | _, Error error -> Error error
                                | _ -> Error OriginalGitPublisherFailure.UnresolvedOriginalEffect
                        with _ -> Error OriginalGitPublisherFailure.UntrustedAuthority)

    member _.ReinspectFixtureBarrier(receipt: OriginalGitPublisherBarrierReceipt) =
        lock store.Lane (fun () ->
            match store.ReadBarrier(), inventory () with
            | None, _ -> Error OriginalGitPublisherFailure.Fenced
            | _, Error error -> Error error
            | Some record, Ok trusted when
                record.OperationId <> receipt.FenceOperationId ||
                record.FenceEpoch <> receipt.FenceEpoch ||
                record.TargetSetSha256 <> OriginalGitPublisherFixtureHash.targets trusted ||
                Set.ofList trusted <> Set.ofList receipt.ExactTargets ||
                record.WitnessSha256 <> receipt.RemoteWitnessSha256 ||
                record.OriginalPendingHighWater <> receipt.OriginalPendingIntentHighWater ||
                record.OriginalSourceEffectHighWater <> receipt.OriginalSourceEffectHighWater ->
                    Error OriginalGitPublisherFailure.UntrustedAuthority
            | Some record, Ok trusted ->
                try
                    match remote.VerifyFixtureRemoteDenial trusted,
                          remote.ReadOriginalHistory(), readHeads trusted with
                    | Ok (), Ok highWater, Ok heads
                        when highWater = (record.OriginalPendingHighWater,
                                          record.OriginalSourceEffectHighWater) ->
                            let actual =
                                OriginalGitPublisherFixtureHash.witness
                                    record.OperationId record.OperatorDecisionId record.FenceEpoch record.TargetSetSha256 highWater heads
                            if actual = record.WitnessSha256 then Ok receipt
                            else Error OriginalGitPublisherFailure.UntrustedAuthority
                    | _ -> Error OriginalGitPublisherFailure.UntrustedAuthority
                with _ -> Error OriginalGitPublisherFailure.UntrustedAuthority)
