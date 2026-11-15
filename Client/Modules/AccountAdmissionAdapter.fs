module Client.AccountAdmissionAdapter

open System
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.AccountCapacityLedger

// This module is deliberately not called by a live Scheduler or migration path.
// Both bindings must have been established from independently trusted identities.
// Actor labels are audit metadata, never security or global-capacity identity.
type AccountAdmissionRequest =
    { Actor: string
      LaunchId: Guid
      AdmissionOperationId: Guid
      AbortOperationId: Guid
      ReleaseOperationId: Guid
      RoleId: string
      RoleVersion: int }

type AccountAdmissionOutcome =
    | ProjectAdmitted of projectHead: string
    | UnboundAbortedAndReleased of accountHead: string
    | GlobalReservationRejected of AccountFailure
    | GlobalReservationUncertain of operationId: Guid
    | TokenRetained of reason: string

module AccountAdmissionAdapter =
    let private explain error = sprintf "%A" error

    // The immutable operation must have originated the exact witness alongside
    // the canonical project-state change. Its origin and witness bytes must
    // still be identical at the latest independently fetched, trusted head.
    let private verifyOrigin (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                             (operationId: Guid) (witnessPath: string) =
        LedgerStore.getOperationAt binding snapshot operationId
        |> Result.mapError explain
        |> Result.bind (function
            | None -> Error "Missing exact immutable WQ-02 operation."
            | Some operation when operation.Type = "WQ02.Mutate"
                                  && operation.Paths = [| WorkQueueGitLedger.RecordPath; witnessPath |] ->
                LedgerStore.getOperationOriginAt binding snapshot operation
                |> Result.mapError explain
                |> Result.bind (fun origin ->
                    let atOrigin = { snapshot with Commit = origin }
                    match LedgerStore.readAt binding snapshot witnessPath,
                          LedgerStore.readAt binding atOrigin witnessPath with
                    | Ok(Some current, Some currentOid), Ok(Some original, Some originalOid)
                        when currentOid = originalOid && current = original ->
                        Ok(atOrigin, currentOid, operation)
                    | Error e, _ | _, Error e -> Error(explain e)
                    | _ -> Error "Witness was replaced, missing or not created by its operation.")
            | Some _ -> Error "Operation did not atomically write the exact WQ-02 witness." )

    // Use only validated WQ-02 state read from the trusted project Git head.
    // No caller-authored document, boolean, STOP, tab state or timeout is proof.
    type VerifiedProjectTerminalReceipts(binding: LedgerBinding) =
        interface IProjectTerminalReceiptVerifier with
            member _.VerifyCurrentTerminal locator =
                let launchId = locator.LaunchId.ToString("D")
                let admissionId = locator.AdmissionOperationId.ToString("D")
                let terminalId = locator.TerminalOperationId.ToString("D")
                let projectId = binding.ProjectId.ToString("D")
                if locator.ProjectId <> binding.ProjectId || locator.LaunchId = Guid.Empty
                   || locator.AdmissionOperationId = Guid.Empty
                   || locator.TerminalOperationId = Guid.Empty
                   || locator.TerminalOperationId = locator.AdmissionOperationId then
                    Error "Project binding or immutable operation identities mismatch."
                else
                    WorkQueueGitLedger.load binding
                    |> Result.mapError explain
                    |> Result.bind (fun (snapshot, state) ->
                        match state.Launches.TryFind launchId with
                        | None -> Error "No current canonical project launch/tombstone."
                        | Some launch when launch.ProjectId = projectId
                                           && launch.Id = launchId
                                           && launch.AccountTokenId = admissionId
                                           && launch.PrimaryWorkId.IsNone
                                           && not launch.EverClaimed
                                           && launch.Lease.IsNone
                                           && launch.Version = 0
                                           && launch.DispatchCount = 0
                                           && (match launch.State with
                                               | LaunchState.TerminalPendingRelease marker -> marker.StartsWith("abort:", StringComparison.Ordinal)
                                               | _ -> false) ->
                            // Handled by the dedicated abort branch below; this
                            // branch only checks shape, never grants on shape.
                            WorkQueueGitLedger.readAdmissionAt binding snapshot launchId
                            |> Result.mapError explain
                            |> Result.bind (function
                                | Some(decision, decisionOid)
                                    when decision.SchemaVersion = 1
                                         && decision.ProjectId = projectId
                                         && decision.LaunchId = launchId
                                         && decision.AccountTokenId = admissionId
                                         && decision.AdmissionOperationId = admissionId
                                         && decision.Kind = "AbortOnlyUnbound"
                                         && decision.AbortOperationId = terminalId
                                         && launch.State = LaunchState.TerminalPendingRelease("abort:" + terminalId) ->
                                    verifyOrigin binding snapshot locator.TerminalOperationId
                                        (WorkQueueGitLedger.admissionPath launchId)
                                    |> Result.bind (fun (origin, originalOid, _) ->
                                        if originalOid <> decisionOid || state.Receipts.ContainsKey admissionId then
                                            Error "Abort-only decision was overwritten or original project admission won."
                                        else
                                            WorkQueueGitLedger.loadAt binding origin
                                            |> Result.mapError explain
                                            |> Result.bind (fun initial ->
                                                match initial.Launches.TryFind launchId,
                                                      initial.Receipts.TryFind terminalId with
                                                | Some first, Some(ProjectOperationPayload.OtherMutation text)
                                                    when first = launch
                                                         && text = "abort-unbound|" + launchId
                                                         && not (initial.Receipts.ContainsKey admissionId) ->
                                                    Ok { ProjectId = projectId; LaunchId = launchId
                                                         AdmissionOperationId = admissionId
                                                         ProjectLedgerId = binding.LedgerId.ToString("D")
                                                         ProjectHead = snapshot.Commit
                                                         TerminalOperationId = terminalId
                                                         TerminalBlobOid = decisionOid
                                                         TerminalKind = "AbortOnlyUnbound"
                                                         // C01 has no provider launch; its first
                                                         // exclusive absence fence is generation 1.
                                                         LaunchGeneration = 1L }
                                                | _ -> Error "Abort origin does not contain the exclusive unbound tombstone."))
                                | _ -> Error "No exact immutable C01 abort-only decision." )
                        | Some launch ->
                            let currentTerminal =
                                match launch.State with
                                | LaunchState.TerminalPendingRelease marker ->
                                    marker.EndsWith(":" + terminalId, StringComparison.Ordinal)
                                | _ -> false
                            if launch.ProjectId <> projectId || launch.AccountTokenId <> admissionId
                               || not currentTerminal || launch.Version < 1 then
                                Error "Launch is not terminal at the current verified project head."
                            else
                                WorkQueueGitLedger.readAdmissionAt binding snapshot launchId
                                |> Result.mapError explain
                                |> Result.bind (function
                                    | Some(decision, decisionOid)
                                        when decision.SchemaVersion = 1
                                             && decision.ProjectId = projectId
                                             && decision.LaunchId = launchId
                                             && decision.AccountTokenId = admissionId
                                             && decision.AdmissionOperationId = admissionId
                                             && decision.Kind = "AdmissionWon"
                                             && decision.AbortOperationId = "" ->
                                        verifyOrigin binding snapshot locator.AdmissionOperationId
                                            (WorkQueueGitLedger.admissionPath launchId)
                                        |> Result.bind (fun (admissionOrigin, originalDecisionOid, admissionOperation) ->
                                            if decisionOid <> originalDecisionOid then
                                                Error "Original project admission decision changed."
                                            else
                                                WorkQueueGitLedger.loadAt binding admissionOrigin
                                                |> Result.mapError explain
                                                |> Result.bind (fun admittedState ->
                                                    match admittedState.Receipts.TryFind admissionId,
                                                          admittedState.Launches.TryFind launchId with
                                                    | Some(ProjectOperationPayload.AdmitProject original), Some first
                                                        when first.State = LaunchState.ReservedUnregistered
                                                             && original.Id = first.Id
                                                             && original.ProjectId = first.ProjectId
                                                             && original.RoleId = first.RoleId
                                                             && original.RoleVersion = first.RoleVersion
                                                             && original.AccountTokenId = first.AccountTokenId
                                                             && first.Version = 0
                                                             && first.DispatchCount = 0
                                                             && first.Lease.IsNone
                                                             && original.AccountTokenId = admissionId
                                                             && first.ProjectId = projectId
                                                             && first.RoleId = launch.RoleId
                                                             && first.RoleVersion = launch.RoleVersion
                                                             && admissionOperation.Sequence > 0L ->
                                                        WorkQueueGitLedger.readTerminalAt binding snapshot launchId
                                                        |> Result.mapError explain
                                                        |> Result.bind (function
                                                            | Some(receipt, terminalOid)
                                                                when receipt.SchemaVersion = 1
                                                                     && receipt.ProjectId = projectId
                                                                     && receipt.LaunchId = launchId
                                                                     && receipt.AccountTokenId = admissionId
                                                                     && receipt.AdmissionOperationId = admissionId
                                                                     && receipt.TerminalOperationId = terminalId
                                                                     && receipt.TerminalKind = "LaunchTerminal"
                                                                     && receipt.LaunchGeneration = int64 launch.Version ->
                                                                verifyOrigin binding snapshot locator.TerminalOperationId
                                                                    (WorkQueueGitLedger.terminalPath launchId)
                                                                |> Result.bind (fun (terminalOrigin, originalTerminalOid, terminalOperation) ->
                                                                    if originalTerminalOid <> terminalOid
                                                                       || terminalOperation.Sequence <= admissionOperation.Sequence then
                                                                        Error "Terminal witness overwritten or ordered before admission."
                                                                    else
                                                                        WorkQueueGitLedger.loadAt binding terminalOrigin
                                                                        |> Result.mapError explain
                                                                        |> Result.bind (fun terminalState ->
                                                                            match terminalState.Launches.TryFind launchId,
                                                                                  terminalState.Receipts.TryFind terminalId with
                                                                            | Some finished, Some _
                                                                                when finished = launch ->
                                                                                Ok { ProjectId = projectId; LaunchId = launchId
                                                                                     AdmissionOperationId = admissionId
                                                                                     ProjectLedgerId = binding.LedgerId.ToString("D")
                                                                                     ProjectHead = snapshot.Commit
                                                                                     TerminalOperationId = terminalId
                                                                                     TerminalBlobOid = terminalOid
                                                                                     TerminalKind = "LaunchTerminal"
                                                                                     LaunchGeneration = int64 launch.Version }
                                                                            | _ -> Error "Terminal origin is not the matching launch transition."))
                                                            | _ -> Error "No exact immutable project terminal witness.")
                                                    | _ -> Error "Original admission operation did not publish this launch."))
                                    | _ -> Error "Missing immutable original project admission decision."))

    let private initialLaunch (project: LedgerBinding) (request: AccountAdmissionRequest) =
        { Id = request.LaunchId.ToString("D")
          ProjectId = project.ProjectId.ToString("D")
          RoleId = request.RoleId
          RoleVersion = request.RoleVersion
          AccountTokenId = request.AdmissionOperationId.ToString("D")
          State = LaunchState.ReservedUnregistered
          Lease = None; Version = 0; Credit = None
          PrimaryWorkId = None; EverClaimed = false
          AncillaryMessages = Set.empty; DispatchCount = 0 }

    let private valid request =
        not (String.IsNullOrWhiteSpace request.Actor)
        && not (String.IsNullOrWhiteSpace request.RoleId)
        && request.LaunchId <> Guid.Empty && request.AdmissionOperationId <> Guid.Empty
        && request.AbortOperationId <> Guid.Empty && request.ReleaseOperationId <> Guid.Empty
        && request.AdmissionOperationId <> request.AbortOperationId
        && request.AdmissionOperationId <> request.ReleaseOperationId
        && request.AbortOperationId <> request.ReleaseOperationId
        && request.RoleVersion > 0

    let releaseTerminal (account: AccountBinding) (project: LedgerBinding)
                        (actor: string) (releaseOperationId: Guid) (locator: ProjectTerminalLocator) =
        if locator.ProjectId <> project.ProjectId then
            Error(AccountTerminalProofRejected "Release is not bound to the requested project ledger.")
        else
            let verifier = VerifiedProjectTerminalReceipts(project) :> IProjectTerminalReceiptVerifier
            // Even on an immutable account receipt replay, demand fresh project
            // proof and independently match the original role/versioned token.
            match verifier.VerifyCurrentTerminal locator with
            | Error reason -> Error(AccountTerminalProofRejected reason)
            | Ok _ ->
                AccountCapacityLedger.fetch account
                |> Result.bind (fun accountSnapshot ->
                    match accountSnapshot.State.Tokens
                          |> Array.tryFind (fun token -> token.LaunchId = locator.LaunchId.ToString("D")) with
                    | Some token when token.ProjectId = project.ProjectId.ToString("D")
                                      && token.AdmissionOperationId = locator.AdmissionOperationId.ToString("D")
                                      && (token.Status = "Held" || token.Status = "Released") ->
                        WorkQueueGitLedger.load project
                        |> Result.mapError (fun e -> AccountTerminalProofRejected(explain e))
                        |> Result.bind (fun (_, projectState) ->
                            match projectState.Launches.TryFind (locator.LaunchId.ToString("D")) with
                            | Some launch when launch.RoleId = token.RoleId
                                               && int64 launch.RoleVersion = token.RoleConfigVersion
                                               && launch.AccountTokenId = token.AdmissionOperationId ->
                                AccountCapacityLedger.release account verifier
                                    releaseOperationId actor locator
                            | _ -> Error(AccountTerminalProofRejected "Versioned project role differs from account token."))
                    | _ -> Error(AccountTerminalProofRejected "No matching held/released original account token."))

    // Explicit C01 recovery: a verified held account token and project CAS
    // tombstone precede account release. A missing operation or uncertain fetch
    // NEVER releases capacity. The original and abort transitions compete on
    // the same launch-ID absence constraint.
    let abortUnbound (account: AccountBinding) (project: LedgerBinding)
                     (request: AccountAdmissionRequest) =
        if not (valid request) then
            TokenRetained "Invalid stable admission, abort or release identity."
        else
            let launch = initialLaunch project request
            match AccountCapacityLedger.fetch account with
            | Error e -> TokenRetained("Cannot verify held global token: " + explain e)
            | Ok snapshot ->
                match snapshot.State.Tokens |> Array.tryFind (fun t -> t.LaunchId = launch.Id) with
                | None -> TokenRetained "Original account token is not present at a trusted head."
                | Some token when token.ProjectId <> launch.ProjectId
                                  || token.AdmissionOperationId <> launch.AccountTokenId
                                  || token.RoleId <> launch.RoleId
                                  || token.RoleConfigVersion <> int64 launch.RoleVersion ->
                    TokenRetained "Account token does not own the original project launch/role."
                | Some token when token.Status = "Released" ->
                    let locator =
                        { ProjectId = project.ProjectId; LaunchId = request.LaunchId
                          AdmissionOperationId = request.AdmissionOperationId
                          TerminalOperationId = request.AbortOperationId }
                    match releaseTerminal account project request.Actor request.ReleaseOperationId locator with
                    | Ok applied -> UnboundAbortedAndReleased applied.CurrentHead
                    | Error e -> TokenRetained("Released-token replay cannot be verified: " + explain e)
                | Some token when token.Status <> "Held" ->
                    TokenRetained "Account token has unknown status; preserve reconciliation evidence."
                | Some _ ->
                    match WorkQueueGitLedger.load project with
                    | Error e -> TokenRetained("Project head unavailable before abort: " + explain e)
                    | Ok (_, projectState) ->
                        let existing = projectState.Launches.TryFind launch.Id
                        let alreadyAborted =
                            match existing with
                            | Some l when l.State =
                                LaunchState.TerminalPendingRelease("abort:" + request.AbortOperationId.ToString("D"))
                                && l.AccountTokenId = launch.AccountTokenId -> true
                            | _ -> false
                        let aborted =
                            if alreadyAborted then Ok ()
                            elif existing.IsSome then Error "Project admission/launch exists; cannot C01-abort."
                            elif projectState.Receipts.ContainsKey (request.AdmissionOperationId.ToString("D")) then
                                Error "Original project admission receipt exists."
                            else
                                WorkQueueGitLedger.execute project request.Actor request.AbortOperationId
                                    projectState.Version
                                    (AbortUnboundProjectAdmission(launch, request.AdmissionOperationId.ToString("D")))
                                |> Result.map ignore
                                |> Result.mapError explain
                        match aborted with
                        | Error error -> TokenRetained("C01 outcome not verified: " + error)
                        | Ok () ->
                            let locator =
                                { ProjectId = project.ProjectId; LaunchId = request.LaunchId
                                  AdmissionOperationId = request.AdmissionOperationId
                                  TerminalOperationId = request.AbortOperationId }
                            match releaseTerminal account project request.Actor request.ReleaseOperationId locator with
                            | Ok released -> UnboundAbortedAndReleased released.CurrentHead
                            | Error e -> TokenRetained("C01 committed but account release unverified: " + explain e)

    let private heldFor (account: AccountBinding) (project: LedgerBinding)
                        (request: AccountAdmissionRequest) =
        // A returned reserve receipt is also replayable after terminal release.
        // Never admit project demand from a previously released global slot.
        match AccountCapacityLedger.fetch account with
        | Error _ -> false
        | Ok snapshot ->
            snapshot.State.Tokens
            |> Array.exists (fun token ->
                token.LaunchId = request.LaunchId.ToString("D")
                && token.ProjectId = project.ProjectId.ToString("D")
                && token.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
                && token.RoleId = request.RoleId
                && token.RoleConfigVersion = int64 request.RoleVersion
                && token.Status = "Held")

    // One global reservation across all projects, then project demand CAS.
    // A definitive lack of compatible work can be fenced by the separate C01
    // operation. Ambiguous account/project transport preserves the token.
    let admit (account: AccountBinding) (project: LedgerBinding)
              (request: AccountAdmissionRequest) =
        if not (valid request) then
            GlobalReservationRejected(InvalidAccountBinding "Invalid admission request.")
        else
            match AccountCapacityLedger.reserve account request.AdmissionOperationId
                    request.Actor request.LaunchId project.ProjectId request.RoleId
                    (int64 request.RoleVersion) with
            | Error(AccountOutcomeUnknown _) ->
                GlobalReservationUncertain request.AdmissionOperationId
            | Error e -> GlobalReservationRejected e
            | Ok _ when not (heldFor account project request) ->
                TokenRetained "Global reservation cannot be reverified as held for this project and versioned role."
            | Ok _ ->
                let launch = initialLaunch project request
                match WorkQueueGitLedger.load project with
                | Error e -> TokenRetained("Cannot verify project admission snapshot: " + explain e)
                | Ok (verifiedHead, projectState) ->
                    match projectState.Launches.TryFind launch.Id with
                    | Some existing when existing.State =
                        LaunchState.TerminalPendingRelease("abort:" + request.AbortOperationId.ToString("D")) ->
                        abortUnbound account project request
                    | Some existing when
                        (match existing.State with
                         | LaunchState.TerminalPendingRelease _
                         | LaunchState.DemandInvalidatedPendingTerminal _ -> true
                         | _ -> false) ->
                        TokenRetained "Project launch is terminal or invalidated; no new admission."
                    | Some existing when existing.AccountTokenId = launch.AccountTokenId
                                          && existing.ProjectId = launch.ProjectId
                                          && existing.RoleId = request.RoleId
                                          && existing.RoleVersion = request.RoleVersion
                                          && projectState.Receipts.ContainsKey (request.AdmissionOperationId.ToString("D")) ->
                        match WorkQueueGitLedger.readAdmissionAt project verifiedHead launch.Id with
                        | Error e -> TokenRetained("Original admission witness unreadable: " + explain e)
                        | Ok(Some(decision, _)) when decision.SchemaVersion = 1
                                                    && decision.Kind = "AdmissionWon"
                                                    && decision.AbortOperationId = ""
                                                    && decision.LaunchId = launch.Id
                                                    && decision.ProjectId = launch.ProjectId
                                                    && decision.AccountTokenId = launch.AccountTokenId
                                                    && decision.AdmissionOperationId =
                                                        request.AdmissionOperationId.ToString("D") ->
                            match verifyOrigin project verifiedHead request.AdmissionOperationId
                                    (WorkQueueGitLedger.admissionPath launch.Id) with
                            | Ok _ -> ProjectAdmitted verifiedHead.Commit
                            | Error reason -> TokenRetained("Original admission origin unverified: " + reason)
                        | _ -> TokenRetained "No matching original immutable admission decision."
                    | Some _ -> TokenRetained "Project launch ID is already bound to another decision."
                    | None ->
                        match WorkQueueGitLedger.execute project request.Actor request.AdmissionOperationId
                                projectState.Version (AdmitProject launch) with
                        | Ok written -> ProjectAdmitted written.CurrentHead
                        | Error(InvalidBinding reason) when reason.Contains("NoCompatibleMatching", StringComparison.Ordinal) ->
                            abortUnbound account project request
                        | Error e -> TokenRetained("Project CAS was not conclusively admitted: " + explain e)
