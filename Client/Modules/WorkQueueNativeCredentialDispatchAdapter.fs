module Client.WorkQueueNativeCredentialDispatchAdapter

open System
open System.Text.Json
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueDurableLaunchIntent
open Client.OriginalBrowserJournal
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Jarvis.WorkQueues

// No production provider bridge exists. Only a disposable, explicit caller may
// supply the fake provider below; live Scheduler/browser/MCP never register it.
// The Work credential is held in volatile memory, not a prompt, ledger or log.
[<Sealed>]
type NativeCredentialTicket internal (context: DurableIntentContext, project: LedgerBinding,
                                      original: DurablePlanningLaunchIntent,
                                      executionId: string, issued: IssuedWorkCredential) =
    let gate = obj()
    let mutable firstConnectVersion: int option = None
    member _.Original = original
    member _.ExecutionId = executionId
    member internal _.Context = context
    member internal _.Project = project
    member internal _.Issued = issued
    member internal _.FirstConnectVersion(version: int) =
        lock gate (fun () ->
            match firstConnectVersion with
            | Some pinned -> pinned
            | None ->
                firstConnectVersion <- Some version
                version)
    override _.ToString() = "NativeCredentialTicket(<redacted>)"

[<Sealed>]
type NativePrivateBootstrap internal (ticket: NativeCredentialTicket) =
    let gate = obj()
    let mutable bearer: IssuedWorkCredential option = Some ticket.Issued
    member _.LaunchId = ticket.Original.LaunchId
    member _.ExecutionId = ticket.ExecutionId
    // The execution identifier is a cooperative continuity check, NOT remote
    // attestation. A real provider must independently offer a private channel.
    member _.TakeOnceFor(executionId: string) =
        lock gate (fun () ->
            if executionId <> ticket.ExecutionId then None
            else
                match bearer with
                | None -> None
                | Some credential ->
                    bearer <- None
                    Some(credential.RevealForPrivateDelivery()))
    member _.Consumed = lock gate (fun () -> bearer.IsNone)
    override _.ToString() = "NativePrivateBootstrap(<redacted>)"

[<RequireQualifiedAccess>]
type NativeCredentialAdmission =
    | Disabled
    | HostBridgeUnavailable
    | Ready of NativeCredentialTicket
    | Retained of NativeCredentialTicket * reason: string
    | Rejected of reason: string

[<RequireQualifiedAccess>]
type NativeProviderReply =
    | Accepted of receipt: string
    | DefinitiveNoEffect
    | Unknown

[<RequireQualifiedAccess>]
type NativeDispatchPhase =
    | Disabled
    | HostBridgeUnavailable
    | AwaitFirstConnect
    | Connected
    | Retained
    | NoResend

type NativeDispatchReport =
    { Phase: NativeDispatchPhase
      Detail: string
      LaunchId: string
      OriginalOperationId: string }

module WorkQueueNativeCredentialDispatchAdapter =
    let private nonblank (s: string) = not (String.IsNullOrWhiteSpace s)
    let private explain error = sprintf "%A" error
    let private report phase detail (ticket: NativeCredentialTicket) =
        { Phase = phase; Detail = detail; LaunchId = ticket.Original.LaunchId
          OriginalOperationId = ticket.Original.OriginalBrowserOperationId }

    // This is the production entrypoint until a provider-native private
    // bootstrap/first-connect transport is actually implemented and observed.
    // Do not reserve, write or mint a secret when the host cannot deliver it.
    let prepareProduction (_: DurableIntentContext) (_: DurablePlanningLaunchIntent) =
        NativeCredentialAdmission.HostBridgeUnavailable

    // Explicit offline admission uses the existing original WQ12 identity,
    // original WQ04 source and WQ07 credit witness in the SAME project CAS.
    // The transient ticket must survive any uncertain acknowledgement.
    let prepareDisposable enabled privateBridgeAvailable (context: DurableIntentContext)
                          (original: DurablePlanningLaunchIntent) actor executionId =
        if not enabled then NativeCredentialAdmission.Disabled
        elif not privateBridgeAvailable then NativeCredentialAdmission.HostBridgeUnavailable
        elif not (nonblank executionId) || not (nonblank actor) then
            NativeCredentialAdmission.Rejected "Missing private execution binding or actor."
        else
            match context.Projects |> List.tryFind (fun item ->
                item.Binding.ProjectId.ToString("D") = original.ProjectId) with
            | None -> NativeCredentialAdmission.Rejected "Original project is outside approved membership."
            | Some item ->
                match WorkQueueDurableLaunchIntent.readPrepared context item.Binding
                          original.WorkId original.WorkGeneration with
                | DurableIntentOutcome.Prepared (record, _, _) when record = original ->
                    // An admitted original cannot be resumed with a newly
                    // minted bearer. The hash is deliberately non-recoverable.
                    let existing =
                        match LedgerStore.fetch item.Binding with
                        | Error _ -> Error "Original project head unavailable; no credential issued."
                        | Ok head ->
                            match WorkQueueGitLedger.readCredentialAt item.Binding head original.LaunchId with
                            | Ok None -> Ok ()
                            | Ok (Some _) -> Error "Original credential already issued; retain its original ticket."
                            | Error _ -> Error "Original verifier unreadable; no credential issued."
                    match existing with
                    | Error why -> NativeCredentialAdmission.Rejected why
                    | Ok () ->
                        let issued = IssuedWorkCredential.Issue original.LaunchId
                        let ticket = NativeCredentialTicket(context, item.Binding, original, executionId, issued)
                        match WorkQueueDurableLaunchIntent.admitPreparedWithCredentialExplicit true
                              context original actor issued with
                        | ProjectAdmitted _ -> NativeCredentialAdmission.Ready ticket
                        | TokenRetained reason ->
                            // Lost CAS ACK may already have committed our verifier.
                            match LedgerStore.fetch item.Binding with
                            | Ok head ->
                                match WorkQueueGitLedger.readCredentialAt item.Binding head original.LaunchId with
                                | Ok (Some (stored, _)) when stored.SecretSha256 <> issued.VerifierSha256 ->
                                    NativeCredentialAdmission.Rejected
                                        "A competing original credential won the Git CAS."
                                | _ -> NativeCredentialAdmission.Retained(ticket, reason)
                            | Error _ -> NativeCredentialAdmission.Retained(ticket, reason)
                        | GlobalReservationUncertain _ ->
                            NativeCredentialAdmission.Retained(ticket, "Original global reservation outcome unknown.")
                        | GlobalReservationRejected failure ->
                            NativeCredentialAdmission.Rejected("Original reservation denied: " + explain failure)
                        | UnboundAbortedAndReleased _ ->
                            NativeCredentialAdmission.Rejected "Original admission rejected and verified unbound abort completed."
                | _ -> NativeCredentialAdmission.Rejected "Prepared original changed or is unreconciled."

    let private originalIntent (ticket: NativeCredentialTicket) : OriginalBrowserIntent =
        let p = ticket.Original
        let optional (s: string) = if nonblank s then Some s else None
        { LaunchId = p.LaunchId; ProjectId = p.ProjectId
          WorkId = p.WorkId; WorkGeneration = p.WorkGeneration
          RoleId = p.RoleId; RoleVersion = p.RoleVersion
          RequestFingerprint = p.SourceRequestHash
          OperationId = p.OriginalBrowserOperationId
          Provenance =
            { AccountHead = p.AccountHead; ProjectHead = p.ProjectHead
              SourceRef = optional p.SourceRef; SourceCommit = optional p.SourceCommit } }

    let private claimPath (ticket: NativeCredentialTicket) =
        "records/wq11-original-hosts/" + Guid.Parse(ticket.Original.LaunchId).ToString("N") + ".json"

    // Shares WQ11's exact canonical claim shape/path, so a different trusted
    // SQLite root or dispatch implementation cannot re-send this original.
    let private claimBody hostId (ticket: NativeCredentialTicket) =
        let p = ticket.Original
        JsonSerializer.Serialize(
            {| schemaVersion = 1
               launchId = p.LaunchId
               projectId = p.ProjectId
               workId = p.WorkId
               workGeneration = p.WorkGeneration
               roleId = p.RoleId
               roleVersion = p.RoleVersion
               admissionOperationId = p.AdmissionOperationId
               originalOperationId = p.OriginalBrowserOperationId
               sourceRequestHash = p.SourceRequestHash
               journalHostId = hostId |})

    let private verifyHostClaim (ticket: NativeCredentialTicket) journal =
        match OriginalBrowserJournal.trustedHostId journal with
        | Error _ -> Error "Trusted original host is unavailable."
        | Ok hostId ->
            let path, expected = claimPath ticket, claimBody hostId ticket
            match LedgerStore.fetch ticket.Project with
            | Error _ -> Error "Canonical original host claim head unknown."
            | Ok head ->
                match LedgerStore.readAt ticket.Project head path with
                | Ok(Some contents, Some blob) when contents = expected ->
                    match LedgerStore.getOperationAt ticket.Project head
                            (Guid.Parse ticket.Original.OriginalBrowserOperationId) with
                    | Ok(Some op) when op.Type = "PutRecord"
                                       && op.Path = path && op.BlobOid = blob
                                       && op.Paths = [| path |] -> Ok ()
                    | _ -> Error "Original host claim lacks immutable creating operation."
                | _ -> Error "Original host claim absent or belongs to another host."

    let private claimOriginalHost (ticket: NativeCredentialTicket) journal =
        match OriginalBrowserJournal.trustedHostId journal with
        | Error _ -> Error "Trusted original host is unavailable."
        | Ok hostId ->
            let p = ticket.Original
            let claim: LedgerRecordChange =
                { OperationId = Guid.Parse p.OriginalBrowserOperationId
                  Actor = p.AdmissionActor
                  Path = claimPath ticket
                  ExpectedBlobOid = None
                  Content = claimBody hostId ticket }
            match LedgerStore.putRecord ticket.Project claim with
            | Ok applied when not applied.AlreadyApplied ->
                verifyHostClaim ticket journal
            | _ ->
                // A lost Git acknowledgment may already have committed. The
                // local WQ03 marker is required for replay, never a fresh send.
                Error "Canonical original send already claimed or outcome unknown."

    let private verifyAdmitted (ticket: NativeCredentialTicket) =
        let p = ticket.Original
        match WorkQueueDurableLaunchIntent.readPrepared ticket.Context ticket.Project
                  p.WorkId p.WorkGeneration with
        | DurableIntentOutcome.Prepared (same, _, _) when same = p ->
            match LedgerStore.fetch ticket.Project with
            | Error _ -> Error "Cannot verify current original project head."
            | Ok head ->
                match WorkQueueGitLedger.loadAt ticket.Project head,
                      WorkQueueGitLedger.readCredentialAt ticket.Project head p.LaunchId,
                      AccountCapacityLedger.fetch ticket.Context.Account with
                | Ok project, Ok(Some (verifier, _)), Ok account ->
                    let launch = project.Launches.TryFind p.LaunchId
                    let token = account.State.Tokens |> Array.tryFind (fun value ->
                        value.LaunchId = p.LaunchId)
                    match launch, token with
                    | Some l, Some held when
                        l.ProjectId = p.ProjectId
                        && l.RoleId = p.RoleId && l.RoleVersion = p.RoleVersion
                        && l.Credit = Some p.WorkId
                        && l.AccountTokenId = p.AdmissionOperationId
                        && held.Status = "Held"
                        && held.ProjectId = p.ProjectId
                        && held.AdmissionOperationId = p.AdmissionOperationId
                        && verifier.ProjectId = p.ProjectId
                        && verifier.LaunchId = p.LaunchId
                        && verifier.AccountTokenId = p.AdmissionOperationId
                        && verifier.SecretSha256 = ticket.Issued.VerifierSha256 -> Ok l
                    | _ -> Error "Original role, source credit, account token or verifier diverged."
                | _ -> Error "Canonical credential or original account admission not verified."
        | _ -> Error "Canonical prepared original changed."

    // The real host/browser path is deliberately NO-GO. Caller-supplied
    // callbacks below are only cooperative disposable-provider fixtures.
    let dispatchProduction (_: NativeCredentialTicket) =
        NativeDispatchPhase.HostBridgeUnavailable

    let submitDisposable enabled journalRoot (ticket: NativeCredentialTicket)
                         (provider: OriginalBrowserIntent -> NativePrivateBootstrap -> NativeProviderReply) =
        let result phase detail = report phase detail ticket
        if not enabled then result NativeDispatchPhase.Disabled "Native dispatch is default-off."
        elif isNull (box provider) then
            result NativeDispatchPhase.HostBridgeUnavailable "No private execution bootstrap is available."
        else
            match OriginalBrowserJournal.openExisting journalRoot with
            | Error _ -> result NativeDispatchPhase.Retained "Original one-shot journal continuity unavailable."
            | Ok journal ->
                let intent = originalIntent ticket
                match verifyAdmitted ticket with
                | Error why -> result NativeDispatchPhase.Retained why
                | Ok launch ->
                    match OriginalBrowserJournal.findByLaunch journal intent.LaunchId with
                    | Error _ ->
                        result NativeDispatchPhase.Retained "Original journal read unknown; do not resend."
                    | Ok(Some existing) ->
                        if existing.Intent <> intent || verifyHostClaim ticket journal <> Ok () then
                            result NativeDispatchPhase.Retained "Original is foreign or lacks canonical host claim."
                        else
                            result NativeDispatchPhase.NoResend
                                "Original marker already exists; reconcile outcome and first-connect, never resend."
                    | Ok None ->
                        match launch.State with
                        | LaunchState.ReservedUnregistered ->
                            match claimOriginalHost ticket journal with
                            | Error why -> result NativeDispatchPhase.Retained why
                            | Ok () ->
                                match OriginalBrowserJournal.tryMarkOriginal journal intent with
                                | Error _ ->
                                    result NativeDispatchPhase.Retained
                                        "Pre-send WQ03 marker not confirmed; original send is forbidden."
                                | Ok(OriginalMarkResult.Existing _) ->
                                    result NativeDispatchPhase.NoResend "Original marker already exists; never resend."
                                | Ok OriginalMarkResult.Marked ->
                                    let bootstrap = NativePrivateBootstrap(ticket)
                                    let reply =
                                        try provider intent bootstrap
                                        with _ -> NativeProviderReply.Unknown
                                    let outcome =
                                        match reply with
                                        | NativeProviderReply.Accepted receipt
                                            when nonblank receipt && receipt.Length <= 2048
                                                 && bootstrap.Consumed
                                                 && not (receipt.Contains(ticket.Issued.RevealForPrivateDelivery(),
                                                                         StringComparison.Ordinal)) ->
                                            OriginalBrowserOutcome.Accepted receipt
                                        | NativeProviderReply.DefinitiveNoEffect ->
                                            OriginalBrowserOutcome.DefinitiveNoEffect
                                                "Private provider reported definitive no-effect."
                                        | _ ->
                                            OriginalBrowserOutcome.Unknown
                                                "Original private provider outcome unknown or bootstrap unconsumed."
                                    match OriginalBrowserJournal.recordOriginalOutcome journal intent outcome with
                                    | Error _ ->
                                        result NativeDispatchPhase.Retained
                                            "Original send occurred but durable outcome is unknown; never retry."
                                    | Ok () ->
                                        match outcome with
                                        | OriginalBrowserOutcome.Accepted _ ->
                                            match WorkQueueGitLedger.load ticket.Project with
                                            | Error _ ->
                                                result NativeDispatchPhase.Retained
                                                    "Provider accepted; WQ02 dispatch head unknown."
                                            | Ok (_, state) ->
                                                match WorkQueueGitLedger.executeInertFixtureAt
                                                    ticket.Project ticket.Original.AdmissionActor
                                                    (Guid.Parse ticket.Original.DispatchOperationId)
                                                    state.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                                    (DispatchOnce(intent.LaunchId, DispatchState.Submitted)) with
                                                | Ok _ ->
                                                    result NativeDispatchPhase.AwaitFirstConnect
                                                        "Original accepted, dispatch CAS committed; await credential first-connect."
                                                | Error _ ->
                                                    result NativeDispatchPhase.Retained
                                                        "Original accepted; WQ02 dispatch CAS unresolved, do not resend."
                                        | OriginalBrowserOutcome.Unknown _ ->
                                            // Mirror WQ11's WQ02 OutcomeUnknown witness.
                                            // WQ03 independently prevents another provider
                                            // call, even when this project CAS loses its ACK.
                                            match WorkQueueGitLedger.load ticket.Project with
                                            | Error _ ->
                                                result NativeDispatchPhase.Retained
                                                    "Original send unknown; WQ02 head unavailable, never resend."
                                            | Ok (_, state) ->
                                                match WorkQueueGitLedger.executeInertFixtureAt
                                                    ticket.Project ticket.Original.AdmissionActor
                                                    (Guid.Parse ticket.Original.DispatchOperationId)
                                                    state.Version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                                    (DispatchOnce(intent.LaunchId, DispatchState.OutcomeUnknown)) with
                                                | Ok _ -> result NativeDispatchPhase.Retained
                                                              "Original send unknown; WQ02 witness recorded, never resend."
                                                | Error _ -> result NativeDispatchPhase.Retained
                                                                 "Original send unknown; WQ02 outcome unresolved, never resend."
                                        | OriginalBrowserOutcome.DefinitiveNoEffect _ ->
                                            result NativeDispatchPhase.Retained
                                                "No-effect recorded; held WQ07 token requires proven terminal cleanup."
                        | _ ->
                            result NativeDispatchPhase.Retained
                                "Launch is not reserved or an original send already occurred."

    // Only the disposable verifier fixture can consume this on behalf of a
    // cooperating fake execution. Production must authenticate through a
    // separately accepted private provider/relay bridge.
    let firstConnectDisposable enabled journalRoot (ticket: NativeCredentialTicket)
                               executionId bearer =
        let result phase detail = report phase detail ticket
        if not enabled then result NativeDispatchPhase.Disabled "First-connect fixture is default-off."
        elif executionId <> ticket.ExecutionId then
            result NativeDispatchPhase.Retained "Execution binding changed."
        else
            match OriginalBrowserJournal.openExisting journalRoot with
            | Error _ -> result NativeDispatchPhase.Retained "Original journal continuity unavailable."
            | Ok journal ->
                match OriginalBrowserJournal.findByLaunch journal ticket.Original.LaunchId,
                      verifyHostClaim ticket journal, verifyAdmitted ticket with
                | Ok(Some entry), Ok (), Ok _ when entry.Intent = originalIntent ticket
                                                   && (match entry.Outcome with
                                                       | Some(OriginalBrowserOutcome.Accepted _) -> true
                                                       | _ -> false) ->
                    match WorkQueueGitLedger.load ticket.Project with
                    | Error _ -> result NativeDispatchPhase.Retained "First-connect project head unknown."
                    | Ok (_, state) ->
                        let version = ticket.FirstConnectVersion state.Version
                        match WorkQueueGitLedger.executeCredentialFixtureAt ticket.Project
                                  "Work native disposable first-connect"
                                  (Guid.Parse ticket.Original.FirstConnectOperationId)
                                  version (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                  ticket.Original.LaunchId bearer CredentialWorkAction.FirstConnect with
                        | Ok _ -> result NativeDispatchPhase.Connected
                                      "Credential-authenticated first-connect/identical replay verified."
                        | Error _ -> result NativeDispatchPhase.Retained
                                         "Credential-first-connect denied or uncertain; keep original operation."
                | _ -> result NativeDispatchPhase.Retained
                           "Original accepted provider/session/credential witnesses are missing."
