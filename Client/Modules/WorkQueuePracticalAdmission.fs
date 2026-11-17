module Client.WorkQueuePracticalAdmission

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter

// No live host or Scheduler invokes this adapter. ExplicitEnabled must be set
// by an authorized offline/test caller; the ordinary account admit entrypoint
// remains default-off. The selected original work/blob are assertions, not
// portable admission grants, and are checked again at the project Git CAS.
type PracticalAdmissionDemand =
    { WorkId: string
      OriginBlobOid: string }

module WorkQueuePracticalAdmission =
    let private invalid message = InvalidBinding ("WQ04 practical admission: " + message)

    let private sameAccountToken (account: AccountBinding) (project: LedgerBinding)
                                 (request: AccountAdmissionRequest) = result {
        let! snapshot =
            AccountCapacityLedger.fetch account
            |> Result.mapError (fun e -> invalid (sprintf "Account head unknown: %A" e))
        let authority = AccountAdmissionAdapter.projectAuthority project
                        |> AccountCapacityLedger.projectAuthoritySha256
        if not (snapshot.State.Tokens |> Array.exists (fun token ->
            token.Status = "Held"
            && token.LaunchId = request.LaunchId.ToString("D")
            && token.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
            && token.ProjectId = project.ProjectId.ToString("D")
            && token.ProjectLedgerId = project.LedgerId.ToString("D")
            && token.ProjectLedgerEpoch = project.Epoch
            && token.ProjectAuthoritySha256 = authority
            && token.RoleId = request.RoleId
            && token.RoleConfigVersion = int64 request.RoleVersion)) then
            return! Error(invalid "Original global cap token is absent, foreign, released or wrong-role.")
    }

    // The WQ04 producer already checked the original source at publication.
    // Recheck its immutable current origin plus actual canonical task receipt
    // at admission, rather than trusting a candidate supplied by a caller.
    // This bounded first bridge deliberately supports task planning demand;
    // review, source-writing and browser effects remain closed.
    let private sourceAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                         (state: Project) (demand: PracticalAdmissionDemand)
                         requireAvailable = result {
        let! stage =
            match state.Works.TryFind demand.WorkId with
            | Some item when item.Kind = WorkKind.Planning
                             && (not requireAvailable || item.State = WorkState.Available)
                             && item.QueueId = "planning" -> Ok item
            | _ -> Error(invalid "No available canonical planning task with the exact selected work ID.")
        let! maybeOrigin = WorkQueueGitLedger.readOriginAt binding snapshot stage.Id
        let! origin =
            match maybeOrigin with
            | Some (record, oid) when oid = demand.OriginBlobOid -> Ok record
            | _ -> Error(invalid "Missing, replaced or stale WQ04 work-origin blob.")
        if origin.SchemaVersion <> 1
           || origin.ProjectId <> binding.ProjectId.ToString("D")
           || origin.WorkId <> stage.Id
           || origin.AccountCreditStatus <> "NotReserved"
           || String.IsNullOrWhiteSpace origin.RoleId || origin.RoleVersion < 1
           || origin.SourceOperationKind <> "CreateTask"
           || String.IsNullOrWhiteSpace origin.SourceRequestHash
           || String.IsNullOrWhiteSpace origin.SourceSelector
           || origin.SourceHighWater >= snapshot.Header.NextSequence then
            return! Error(invalid "WQ04 canonical planning origin has inconsistent identity/coverage.")
        let! sourceId =
            match Guid.TryParse origin.SourceOperationId with
            | true, id when id <> Guid.Empty -> Ok id
            | _ -> Error(invalid "WQ04 original source operation ID is malformed.")
        let! maybeOp = LedgerStore.getOperationAt binding snapshot sourceId
        let! sourceOp =
            match maybeOp with
            | Some op when op.OperationId = origin.SourceOperationId
                           && op.Sequence = origin.SourceOperationSequence
                           && op.Type = origin.SourceOperationKind
                           && op.Actor = origin.SourceOperationActor
                           && op.RequestHash = origin.SourceRequestHash -> Ok op
            | _ -> Error(invalid "Original task operation or request hash differs from WQ04 origin.")
        let! originHead = LedgerStore.getOperationOriginAt binding snapshot sourceOp
        if originHead <> origin.SourceOriginHead then
            return! Error(invalid "Original task operation moved from its verified Git commit.")
        let! old = LedgerStore.verifiedHistoricalSnapshot binding originHead
        let! raw, oid = LedgerStore.readAt binding old origin.SourceEntityPath
        match raw, oid with
        | Some text, Some blob when blob = origin.SourceEntityBlobOid
                                    && (SHA256.HashData(Encoding.UTF8.GetBytes text)
                                        |> Convert.ToHexString
                                        |> fun digest -> digest.ToLowerInvariant())
                                       = origin.SourceEntitySha256 -> ()
        | _ -> return! Error(invalid "Original source entity/receipt is not identical to WQ04 history.")
    }

    let private verifyPinnedAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                               (state: Project) (request: AccountAdmissionRequest)
                               (demand: PracticalAdmissionDemand) = result {
        let launch: Launch =
            { Id = request.LaunchId.ToString("D")
              ProjectId = binding.ProjectId.ToString("D")
              RoleId = request.RoleId; RoleVersion = request.RoleVersion
              AccountTokenId = request.AdmissionOperationId.ToString("D")
              State = LaunchState.ReservedUnregistered
              Lease = None; Version = 0; Credit = None
              PrimaryWorkId = None; EverClaimed = false
              AncillaryMessages = Set.empty; DispatchCount = 0 }
        let! view = WorkQueueRoleAuthority.loadAt binding snapshot
        if view.Project <> state then
            return! Error(invalid "WQ04 role view does not match the winning WQ02 project snapshot.")
        let! _ = WorkQueueRoleAuthority.inspect view (AdmitNew launch)
        let! selected =
            WorkQueueProducers.selectRoleDemandPractical view.Catalog state request.RoleId
            |> Result.mapError (fun e -> invalid (sprintf "No pinned role demand: %A" e))
        match selected.Chosen with
        | Some chosen when chosen.WorkId = demand.WorkId
                           && chosen.RoleId = request.RoleId
                           && chosen.RoleVersion = request.RoleVersion -> ()
        | _ -> return! Error(invalid "Pinned role selected different work or role revision.")
        do! sourceAt binding snapshot state demand true
        // The original published role demand must match the WQ07 token and
        // prospective project admission role; no cross-role stage laundering.
        let! originalRole = WorkQueueGitLedger.readOriginAt binding snapshot demand.WorkId
        match originalRole with
        | Some (origin, _) when origin.RoleId = request.RoleId
                                && origin.RoleVersion = request.RoleVersion -> ()
        | _ ->
            return! Error(invalid
                "Original WQ04 stage is pinned to another role/version; account admission denied.")
        let! role =
            match view.Catalog.Versions.TryFind request.RoleId with
            | Some history ->
                match history.TryFind request.RoleVersion with
                | Some definition when definition.Enabled -> Ok definition
                | _ -> Error(invalid "Pinned original role was missing or disabled.")
            | None -> Error(invalid "No issued role definition.")
        let stored: StoredRole =
            { RoleId = role.RoleId; Version = role.Version
              Instructions = role.Instructions; Enabled = role.Enabled
              QueueSubscriptions =
                  role.QueueSubscriptions
                  |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a,b))
                  |> Seq.toArray }
        let! roleOid = LedgerStore.computeBlobOid binding (JsonSerializer.Serialize stored)
        let witness: WorkQueueRoleAuthority.StoredRoleAdmission =
            { SchemaVersion = 1; ProjectId = launch.ProjectId; LaunchId = launch.Id
              AccountTokenId = launch.AccountTokenId
              RoleId = launch.RoleId; RoleVersion = launch.RoleVersion
              CatalogBlobOid = view.Proof.CatalogBlobOid
              RoleDefinitionOid = roleOid
              AdmissionOperationId = request.AdmissionOperationId.ToString("D") }
        return JsonSerializer.Serialize witness
    }

    // Credits can be reassigned or cleared by a legitimate later ClaimNext.
    // Original admission replay must compare against the immutable *issuing*
    // commit's selected work, not the mutable current credit field.
    let private originalCreditAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                 (request: AccountAdmissionRequest)
                                 (demand: PracticalAdmissionDemand) = result {
        let launchId = request.LaunchId.ToString("D")
        let admissionId = request.AdmissionOperationId.ToString("D")
        let decisionPath = WorkQueueGitLedger.admissionPath launchId
        let credentialPath = WorkQueueGitLedger.credentialPath launchId
        let legacyPaths = [| WorkQueueGitLedger.RecordPath; decisionPath;
                             WorkQueueRoleAuthority.roleAdmissionPath launchId |]
        let credentialPaths = [| WorkQueueGitLedger.RecordPath; decisionPath; credentialPath;
                                 WorkQueueRoleAuthority.roleAdmissionPath launchId |]
        let rolePath = WorkQueueRoleAuthority.roleAdmissionPath launchId
        let! maybeOperation =
            LedgerStore.getOperationAt binding snapshot request.AdmissionOperationId
        let! operation =
            match maybeOperation with
            | Some original when original.Type = "WQ02.Mutate"
                                 && original.OperationId = admissionId
                                 && original.Actor = request.Actor
                                 && (original.Paths = legacyPaths ||
                                     original.Paths = credentialPaths) ->
                Ok original
            | _ -> Error(invalid "Original admission command/paths are missing or have different identity.")
        let! sourceCommit = LedgerStore.getOperationOriginAt binding snapshot operation
        let! issuedHead = LedgerStore.verifiedHistoricalSnapshot binding sourceCommit
        let! originalProject = WorkQueueGitLedger.loadAt binding issuedHead
        let! decision =
            WorkQueueGitLedger.readAdmissionAt binding issuedHead launchId
            |> Result.bind (function
                | Some (record, _) when record.SchemaVersion = 1
                                        && record.Kind = "AdmissionWon"
                                        && record.ProjectId = binding.ProjectId.ToString("D")
                                        && record.LaunchId = launchId
                                        && record.AccountTokenId = admissionId
                                        && record.AdmissionOperationId = admissionId
                                        && record.AbortOperationId = "" -> Ok record
                | _ -> Error(invalid "Original immutable admission decision is absent."))
        // The immutable secret verifier must belong to this SAME original
        // admission commit and match its account token. No later credential
        // record can be retroactively attached to a legacy launch.
        let! _ =
            if operation.Paths = credentialPaths then
                WorkQueueGitLedger.readCredentialAt binding issuedHead launchId
                |> Result.bind (function
                    | Some (record, _) when record.SchemaVersion = 1
                                            && record.ProjectId = binding.ProjectId.ToString("D")
                                            && record.LaunchId = launchId
                                            && record.AccountTokenId = admissionId -> Ok ()
                    | _ -> Error(invalid "Original Work credential verifier is absent or foreign."))
            else Ok ()
        match originalProject.Launches.TryFind decision.LaunchId with
        | Some original when original.Credit = Some demand.WorkId
                             && original.AccountTokenId = decision.AccountTokenId
                             && original.RoleId = request.RoleId
                             && original.RoleVersion = request.RoleVersion ->
            return ()
        | _ -> return! Error(invalid "Original issuing CAS selected another work ID or versioned role.")
    }

    let private verifyIssuedCredentialAt (binding: LedgerBinding) snapshot launchId
                                         (credential: IssuedWorkCredential option) =
        WorkQueueGitLedger.readCredentialAt binding snapshot launchId
        |> Result.bind (fun stored ->
            match credential, stored with
            | None, None -> Ok ()
            | Some issued, Some (verifier, _)
                when not (isNull (box issued))
                     && issued.LaunchId = launchId
                     && verifier.SchemaVersion = 1
                     && verifier.ProjectId = binding.ProjectId.ToString("D")
                     && verifier.LaunchId = launchId
                     && verifier.SecretSha256 = issued.VerifierSha256 -> Ok ()
            | _ -> Error(invalid "Original admission credential does not match its Git verifier."))

    // This opt-in is intentionally not callable from a timer or live dispatch
    // registration. WQ07 first reserves globally, then this project CAS binds
    // the exact WQ04 role/source and WQ02 chosen credit. An uncertain global or
    // project write is never retried with a different operation ID.
    let private admitExplicitCore (credential: IssuedWorkCredential option)
                      (explicitEnabled: bool) (account: AccountBinding)
                      (project: LedgerBinding) (request: AccountAdmissionRequest)
                      (demand: PracticalAdmissionDemand) =
        if not explicitEnabled then
            TokenRetained "WQ04 practical admission is disabled by default."
        elif credential |> Option.exists (fun issued ->
            isNull (box issued) || issued.LaunchId <> request.LaunchId.ToString("D")) then
            TokenRetained "Work credential issuance is scoped to a different launch."
        elif String.IsNullOrWhiteSpace demand.WorkId || String.IsNullOrWhiteSpace demand.OriginBlobOid then
            TokenRetained "Missing exact pinned canonical work/origin assertion."
        else
            // Fail ordinary missing-role/source assertions BEFORE consuming a
            // global token, then verify all of them again at the winning CAS.
            let preflight = result {
                let! snapshot = LedgerStore.fetch project
                let! state = WorkQueueGitLedger.loadAt project snapshot
                match state.Launches.TryFind (request.LaunchId.ToString("D")) with
                | Some already ->
                    // Replays must not re-run new-admission matching after the
                    // original CAS has already consumed the selected credit.
                    let! _ = WorkQueueRoleAuthority.loadAt project snapshot
                    if already.AccountTokenId <> request.AdmissionOperationId.ToString("D")
                       || already.RoleId <> request.RoleId
                       || already.RoleVersion <> request.RoleVersion then
                        return! Error(invalid "Original replay has a different launch/credit/role.")
                    do! originalCreditAt project snapshot request demand
                    do! verifyIssuedCredentialAt project snapshot (request.LaunchId.ToString("D")) credential
                    do! sourceAt project snapshot state demand false
                    return "verified replay"
                | None ->
                    return! verifyPinnedAt project snapshot state request demand
            }
            match preflight with
            | Error error -> TokenRetained(sprintf "No token reserved; preflight rejected: %A" error)
            | Ok _ ->
                let execute binding actor operationId version mutation =
                    match mutation with
                    | AdmitProject launch ->
                        let pinnedAt snapshot state = result {
                                do! sameAccountToken account binding request
                                return! verifyPinnedAt binding snapshot state request demand
                            }
                        match credential with
                        | None ->
                            WorkQueueGitLedger.executePinnedRoleAdmission binding actor operationId
                                version launch demand.WorkId pinnedAt
                        | Some issued ->
                            WorkQueueGitLedger.executePinnedRoleCredentialAdmission binding actor operationId
                                version launch demand.WorkId issued pinnedAt
                    | _ -> Error(invalid "Only exact original project admission is allowed.")
                let outcome =
                    AccountAdmissionAdapter.admitUsing execute
                        AccountAdmissionAdapter.abortUnbound account project request
                match outcome with
                | ProjectAdmitted head ->
                    // LedgerStore may return an original replay without running
                    // the planner. Reverify its immutable role and origin.
                    let replay = result {
                        let! view = WorkQueueRoleAuthority.load project
                        let! launch =
                            match view.Project.Launches.TryFind (request.LaunchId.ToString("D")) with
                            | Some item when item.AccountTokenId = request.AdmissionOperationId.ToString("D")
                                             && item.RoleId = request.RoleId
                                             && item.RoleVersion = request.RoleVersion -> Ok item
                            | _ -> Error(invalid "Original replay launch credit or role identity differs.")
                        let! snapshot = LedgerStore.fetch project
                        do! originalCreditAt project snapshot request demand
                        do! verifyIssuedCredentialAt project snapshot (request.LaunchId.ToString("D")) credential
                        do! sourceAt project snapshot view.Project demand false
                        return launch
                    }
                    match replay with
                    | Ok _ -> ProjectAdmitted head
                    | Error reason -> TokenRetained(sprintf "Original admission replay needs repair: %A" reason)
                | other -> other

    // Preserve the legacy entry point for existing callers. No downgrade is
    // accepted when an original launch has a credential verifier.
    let admitExplicit enabled account project request demand =
        admitExplicitCore None enabled account project request demand

    // Opt-in, host-owned reservation. The issued secret exists only in the
    // caller's transient delivery object; Git stores its digest in the same
    // winning project CAS as WQ04 role/source/account admission.
    let admitExplicitWithCredential enabled account project request demand
                                    (issued: IssuedWorkCredential) =
        admitExplicitCore (Some issued) enabled account project request demand
