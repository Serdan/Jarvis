module Client.WorkQueueVerifiedDispatchAdapter

open System
open System.Text.Json
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Jarvis.WorkQueues.WorkQueueProducers
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueueCrossProjectPlanner
open Client.WorkQueuePracticalAdmission
open Client.AccountCapacityLedger
open Client.AccountAdmissionAdapter
open Client.OriginalBrowserJournal

// Deliberately explicit, default-off, and fake-provider-only. Nothing registers
// this adapter with the live client, browser, timers, or Scheduler. All project
// mutations after admission use WQ02's Frozen-only hermetic fixture executor.
type FakeOriginalAcceptance =
    { LaunchId: string
      WorkId: string
      Receipt: string
      SessionKey: string }

[<RequireQualifiedAccess>]
type FakeOriginalReply =
    | Accepted of FakeOriginalAcceptance
    | DefinitiveNoEffect of reason: string
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type FakeDispatchPhase =
    | Disabled
    | NotReady
    | AtCapacity
    | RepairNeeded
    | AwaitFirstConnect
    | Registered
    | TerminalReleased

type FakeDispatchReport =
    { Phase: FakeDispatchPhase
      Detail: string
      VerifiedSelection: bool
      AccountHead: string option
      ProjectHead: string option
      OriginBlobOid: string option
      RoleId: string
      RoleVersion: int
      WorkId: string
      SourceOperationId: string
      OriginalOperationId: string
      AdmissionOperationId: string
      DispatchOperationId: string
      FirstConnectOperationId: string
      RepairAction: string option }

module WorkQueueVerifiedDispatchAdapter =
    let private nonBlank (s: string) = not (String.IsNullOrWhiteSpace s)
    let private optional (s: string) = if nonBlank s then Some s else None
    let private explain error = sprintf "%A" error

    let private report phase detail verified accountHead projectHead originOid
                       repair (selected: PlannerCandidate)
                       (request: AccountAdmissionRequest) (dispatchId: Guid) (firstConnectId: Guid) =
        { Phase = phase
          Detail = detail
          VerifiedSelection = verified
          AccountHead = accountHead
          ProjectHead = projectHead
          OriginBlobOid = originOid
          RoleId = selected.RoleId
          RoleVersion = selected.RoleVersion
          WorkId = selected.WorkId
          SourceOperationId = selected.Source.OperationId
          OriginalOperationId = selected.OriginalBrowserOperationId
          AdmissionOperationId = request.AdmissionOperationId.ToString("D")
          DispatchOperationId = dispatchId.ToString("D")
          FirstConnectOperationId = firstConnectId.ToString("D")
          RepairAction = repair }

    let private repairText =
        "Preserve the original operation IDs and both Git heads. Inspect the WQ03 " +
        "one-shot journal, WQ02 launch/terminal witnesses and WQ07 token before " +
        "an authorized correction. Never resend or release capacity on uncertainty."

    let private validOperations (selected: PlannerCandidate)
                                (request: AccountAdmissionRequest)
                                dispatchId firstConnectId =
        let ids = [request.LaunchId; request.AdmissionOperationId; request.AbortOperationId
                   request.ReleaseOperationId; dispatchId; firstConnectId]
        let originalUuid =
            if isNull (box selected) then None
            else
                match Guid.TryParse selected.OriginalBrowserOperationId with
                | true, id when id <> Guid.Empty -> Some id
                | _ -> None
        not (isNull (box selected))
        && (ids |> List.forall ((<>) Guid.Empty))
        && (ids |> Set.ofList |> Set.count) = ids.Length
        && (originalUuid |> Option.exists (fun id -> not (ids |> List.contains id)))
        && selected.LaunchId = request.LaunchId.ToString("D")
        && selected.RoleId = request.RoleId
        && selected.RoleVersion = request.RoleVersion
        && nonBlank request.Actor

    // WQ08 is ranking only; independently pin every selected project, source,
    // current role and account fact to verified Git BEFORE the WQ07 reservation.
    // WQ04 rechecks the historical source and these facts inside its winning CAS.
    let private verifyNewSelection (planner: PlannerReport)
                                   (account: AccountBinding) (project: LedgerBinding)
                                   (selected: PlannerCandidate) = result {
        if planner.Mode <> "FakeOnlyReadOnly" || planner.CanDispatch
           || not planner.Issues.IsEmpty || planner.Decision <> PlannerDecision.WouldLaunch selected then
            return! Error "WQ08 ranking is not a clean, matching fake-only selection."
        let! capacity =
            AccountCapacityLedger.fetch account
            |> Result.mapError (fun e -> "Cannot verify WQ07 account head: " + explain e)
        if planner.AccountHead <> Some capacity.Head
           || capacity.State.MaxConcurrentAgents < 1
           || isNull capacity.State.Tokens then
            return! Error "The approved account head/cap does not match the read-only ranking."
        if AccountCapacityLedger.occupied capacity.State >= capacity.State.MaxConcurrentAgents then
            return! Error "Account-global capacity is full at the verified WQ07 head."
        let! head =
            LedgerStore.fetch project
            |> Result.mapError (fun e -> "Cannot verify WQ02 project head: " + explain e)
        let! view =
            WorkQueueRoleAuthority.loadAt project head
            |> Result.mapError (fun e -> "Cannot verify WQ04 role/source: " + explain e)
        if selected.ProjectId <> project.ProjectId.ToString("D")
           || selected.ProjectHead <> head.Commit
           || selected.ProjectVersion <> view.Project.Version
           || selected.CatalogBlobOid <> view.Proof.CatalogBlobOid
           || view.Proof.Head <> head.Commit then
            return! Error "Selected project/head/catalog is no longer the canonical verified snapshot."
        let! role =
            match view.Catalog.Current.TryFind selected.RoleId with
            | Some version when version = selected.RoleVersion ->
                match view.Catalog.Versions.TryFind selected.RoleId with
                | Some versions ->
                    match versions.TryFind version with
                    | Some current when current.Enabled
                                        && current.QueueSubscriptions.Contains selected.QueueId
                                        && nonBlank current.Instructions -> Ok current
                    | _ -> Error "Selected current role is disabled, missing, or incompatible."
                | None -> Error "Selected role revision is missing."
            | _ -> Error "Selected role version differs from current WQ04 catalog."
        let! work =
            match view.Project.Works.TryFind selected.WorkId with
            | Some work when work.Kind = WorkKind.Planning
                             && work.State = WorkState.Available
                             && work.StageGeneration = selected.WorkGeneration
                             && work.QueueId = selected.QueueId
                             && work.AvailableAt = selected.AvailableAt
                             && work.AvailableSequence = selected.AvailableSequence -> Ok work
            | _ -> Error "Selected Work is no longer the same available WQ04 planning stage."
        let! ranking =
            selectRoleDemandPractical view.Catalog view.Project selected.RoleId
            |> Result.mapError (fun e -> "Cannot re-evaluate current WQ04 demand: " + explain e)
        if not (ranking.Chosen |> Option.exists (fun chosen ->
                chosen.WorkId = work.Id
                && chosen.RoleId = role.RoleId
                && chosen.RoleVersion = role.Version)) then
            return! Error "WQ04 current role ranking selects different canonical Work."
        let blocks =
            view.Project.Works
            |> Map.exists (fun _ other ->
                other.Dependencies.Contains work.Id
                && not (other.WaivedDependencies.ContainsKey work.Id)
                && other.State <> WorkState.Completed)
        if blocks <> selected.BlocksOtherWork then
            return! Error "Blocking evidence differs from the pinned project dependency graph."
        let! found =
            WorkQueueGitLedger.readOriginAt project head work.Id
            |> Result.mapError (fun e -> "Cannot read original WQ04 source: " + explain e)
        let! origin, oid =
            match found with
            | Some (origin, oid) when
                origin.ProjectId = selected.ProjectId
                && origin.WorkId = work.Id
                && origin.SourceHead = selected.Source.Head
                && origin.SourceOperationId = selected.Source.OperationId
                && origin.SourceRequestHash = selected.Source.RequestHash
                && optional origin.SourceRef = selected.Source.SourceRef
                && optional origin.SourceCommit = selected.Source.SourceCommit ->
                Ok(origin, oid)
            | _ -> Error "WQ04 immutable origin/source operation disagrees with the selected candidate."
        return capacity, head, origin, oid
    }

    // The WQ03 UUID is enrolled by the local host, never inferred from an actor,
    // root path or caller-supplied boolean. Bind it to one immutable WQ02 Git
    // record using a create-only CAS *before* the original fake-provider call.
    // Independent trusted SQLite roots cannot each win this same launch claim.
    let private originalHostClaimPath (request: AccountAdmissionRequest) =
        "records/wq11-original-hosts/" + request.LaunchId.ToString("N") + ".json"

    let private originalHostClaimBody hostId (selected: PlannerCandidate)
                                      (request: AccountAdmissionRequest) =
        JsonSerializer.Serialize(
            {| schemaVersion = 1
               launchId = selected.LaunchId
               projectId = selected.ProjectId
               workId = selected.WorkId
               workGeneration = selected.WorkGeneration
               roleId = selected.RoleId
               roleVersion = selected.RoleVersion
               admissionOperationId = request.AdmissionOperationId.ToString("D")
               originalOperationId = selected.OriginalBrowserOperationId
               sourceRequestHash = selected.Source.RequestHash
               journalHostId = hostId |})

    let private verifyOriginalHostClaim (project: LedgerBinding)
                                        (journal: OriginalBrowserJournalStore)
                                        (selected: PlannerCandidate)
                                        (request: AccountAdmissionRequest) =
        match OriginalBrowserJournal.trustedHostId journal with
        | Error failure -> Error("WQ03 host continuity unavailable: " + failure)
        | Ok hostId ->
            let path = originalHostClaimPath request
            let expected = originalHostClaimBody hostId selected request
            match LedgerStore.fetch project with
            | Error failure -> Error("Cannot verify canonical original-send claim head: " + explain failure)
            | Ok head ->
                match LedgerStore.readAt project head path with
                | Error failure -> Error("Cannot read canonical original-send claim: " + explain failure)
                | Ok(Some content, Some oid) when content = expected ->
                    match LedgerStore.getOperationAt project head
                            (Guid.Parse selected.OriginalBrowserOperationId) with
                    | Ok(Some operation) when
                        operation.Type = "PutRecord"
                        && operation.Path = path
                        && operation.BlobOid = oid
                        && operation.Paths = [| path |] ->
                        Ok ()
                    | _ -> Error "Canonical original-send claim is missing its immutable creating operation."
                | Ok(Some _, _) ->
                    Error "Canonical original-send claim belongs to a foreign journal host or launch identity."
                | _ -> Error "Canonical original-send claim is absent; original send is not authorized."

    let private claimOriginalHost (project: LedgerBinding)
                                  (journal: OriginalBrowserJournalStore)
                                  (selected: PlannerCandidate)
                                  (request: AccountAdmissionRequest) =
        match OriginalBrowserJournal.trustedHostId journal with
        | Error failure -> Error("WQ03 host continuity unavailable: " + failure)
        | Ok hostId ->
            let change: LedgerRecordChange =
                { OperationId = Guid.Parse selected.OriginalBrowserOperationId
                  Actor = request.Actor
                  Path = originalHostClaimPath request
                  ExpectedBlobOid = None
                  Content = originalHostClaimBody hostId selected request }
            match LedgerStore.putRecord project change with
            | Ok write when not write.AlreadyApplied ->
                // Confirm at an independently fetched head, not merely a local
                // optimistic commit. A lost acknowledgement never grants send.
                verifyOriginalHostClaim project journal selected request
            | Ok _ ->
                Error "Canonical original-send claim already existed; only a durable WQ03 original marker permits replay."
            | Error failure ->
                // The push may have committed despite an error. Even when it
                // matches this host, a second caller cannot infer it is first.
                match verifyOriginalHostClaim project journal selected request with
                | Ok () ->
                    Error "Canonical claim is already present or its acknowledgement was lost; never resend without WQ03 marker."
                | Error why ->
                    Error("Canonical original-send pre-send CAS rejected (" + explain failure + "): " + why)

    // A failed CAS acknowledgment can already have committed. An existing
    // original receipt is evidence only for this exact stable operation; the
    // caller additionally verifies the resulting launch phase.
    let private mutateFakeOnly project actor operationId mutation =
        match WorkQueueGitLedger.load project with
        | Error error -> Error("WQ02 head unavailable: " + explain error)
        | Ok (_, state) ->
            match WorkQueueGitLedger.executeInertFixture project actor operationId state.Version mutation with
            | Ok _ -> Ok ()
            | Error original ->
                match WorkQueueGitLedger.load project with
                | Ok (_, current) ->
                    // Presence of *some* receipt with this UUID is not enough:
                    // it may belong to a different command. Compare the exact
                    // semantic payload at the winning Git head before replay.
                    let observed = current.Receipts.TryFind(operationId.ToString("D"))
                    let originalMatches =
                        match mutation, observed with
                        | DispatchOnce(launch, outcome), Some(ProjectOperationPayload.OtherMutation value) ->
                            value = "dispatch|" + launch + "|" + string outcome
                        | InitializeAgent(launch, session), Some(ProjectOperationPayload.OtherMutation value) ->
                            value = "initialize|" + launch + "|" + session
                        | ClaimNext launch, Some(ProjectOperationPayload.OtherMutation value) ->
                            value = "claim-next|" + launch
                        | _ -> false
                    if originalMatches then Ok ()
                    else Error("WQ02 CAS unresolved or operation ID reused for " + operationId.ToString("D")
                               + ": " + explain original)
                | _ -> Error("WQ02 CAS unresolved for " + operationId.ToString("D") + ": " + explain original)

    // Replay is a read-only reconciliation, not permission to submit again.
    // Never report an accepted original as registered without rechecking WQ07
    // token owner, WQ04 role/admission/source, and exact WQ02 receipt UUIDs.
    let private replay (entry: OriginalBrowserJournalEntry)
                       (head: LedgerSnapshot) (projectState: Project)
                       (accountSnapshot: AccountSnapshot option) (project: LedgerBinding)
                       originOid (selected: PlannerCandidate) (request: AccountAdmissionRequest)
                       (dispatchId: Guid) (connectId: Guid) =
        let accountHead = accountSnapshot |> Option.map (fun s -> s.Head)
        let accountAuthority =
            AccountAdmissionAdapter.projectAuthority project
            |> AccountCapacityLedger.projectAuthoritySha256
        let token =
            accountSnapshot
            |> Option.bind (fun snapshot ->
                snapshot.State.Tokens
                |> Array.tryFind (fun candidate ->
                    candidate.LaunchId = selected.LaunchId
                    && candidate.ProjectId = project.ProjectId.ToString("D")
                    && candidate.ProjectLedgerId = project.LedgerId.ToString("D")
                    && candidate.ProjectLedgerEpoch = project.Epoch
                    && candidate.ProjectAuthoritySha256 = accountAuthority
                    && candidate.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
                    && candidate.RoleId = request.RoleId
                    && candidate.RoleConfigVersion = int64 request.RoleVersion))
        let launch =
            projectState.Launches.TryFind selected.LaunchId
            |> Option.filter (fun l ->
                l.ProjectId = selected.ProjectId
                && l.AccountTokenId = request.AdmissionOperationId.ToString("D")
                && l.RoleId = request.RoleId
                && l.RoleVersion = request.RoleVersion)
        let pinnedRole =
            match WorkQueueRoleAuthority.loadAt project head with
            | Ok view -> view.Project = projectState && view.Proof.Head = head.Commit
            | _ -> false
        let admittedAt =
            match WorkQueueGitLedger.readAdmissionAt project head selected.LaunchId with
            | Ok(Some(decision, _)) ->
                decision.Kind = "AdmissionWon"
                && decision.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
                && decision.AccountTokenId = request.AdmissionOperationId.ToString("D")
                && decision.LaunchId = selected.LaunchId
                && decision.ProjectId = selected.ProjectId
            | _ -> false
        let sourceAt =
            match WorkQueueGitLedger.readOriginAt project head selected.WorkId with
            | Ok(Some(origin, _)) ->
                origin.ProjectId = selected.ProjectId
                && origin.WorkId = selected.WorkId
                && origin.SourceOperationId = selected.Source.OperationId
                && origin.SourceRequestHash = selected.Source.RequestHash
                && origin.SourceHead = selected.Source.Head
                && optional origin.SourceRef = selected.Source.SourceRef
                && optional origin.SourceCommit = selected.Source.SourceCommit
            | _ -> false
        let originalAt =
            entry.Intent.LaunchId = selected.LaunchId
            && entry.Intent.ProjectId = selected.ProjectId
            && entry.Intent.WorkId = selected.WorkId
            && entry.Intent.WorkGeneration = selected.WorkGeneration
            && entry.Intent.RoleId = selected.RoleId
            && entry.Intent.RoleVersion = selected.RoleVersion
            && entry.Intent.OperationId = selected.OriginalBrowserOperationId
            && entry.Intent.RequestFingerprint = selected.Source.RequestHash
            && entry.Intent.Provenance.SourceRef = selected.Source.SourceRef
            && entry.Intent.Provenance.SourceCommit = selected.Source.SourceCommit
        let verified = token.IsSome && launch.IsSome && pinnedRole && admittedAt && sourceAt && originalAt
        let phase, detail, action =
            if not verified then
                FakeDispatchPhase.RepairNeeded,
                "Replayed original cannot be joined to the same verified token, launch, role, admission and source.",
                Some repairText
            else
                match token, launch, entry.Outcome with
                | Some t, _, _ when t.Status = "Released" ->
                    FakeDispatchPhase.NotReady,
                    "Original WQ07 account token was already released. This replay cannot dispatch again.", None
                | Some t, _, _ when t.Status <> "Held" ->
                    FakeDispatchPhase.RepairNeeded,
                    "Original WQ07 account token is not in an authorized held/released state.", Some repairText
                | Some _, Some l, Some(OriginalBrowserOutcome.Accepted receipt) ->
                    match l.State with
                    | LaunchState.Registered (_, sessionKey) ->
                        let dispatched =
                            projectState.Receipts.TryFind(dispatchId.ToString("D")) =
                                Some(ProjectOperationPayload.OtherMutation
                                    ("dispatch|" + l.Id + "|Submitted"))
                        let initialized =
                            projectState.Receipts.TryFind(connectId.ToString("D")) =
                                Some(ProjectOperationPayload.OtherMutation
                                    ("initialize|" + l.Id + "|" + sessionKey))
                        if dispatched && initialized then
                            FakeDispatchPhase.Registered,
                            "Accepted original and exact WQ02 dispatch/first-connect receipts independently verified: " + receipt,
                            None
                        else
                            FakeDispatchPhase.RepairNeeded,
                            "Registered state lacks the pinned original dispatch or first-connect operation receipt.",
                            Some repairText
                    | LaunchState.ReservedUnregistered
                    | LaunchState.SubmittedUnregistered _ ->
                        FakeDispatchPhase.AwaitFirstConnect,
                        "Original accepted (" + receipt + "); missing session/dispatch confirmation requires reconciliation, never resend.",
                        Some repairText
                    | _ ->
                        FakeDispatchPhase.RepairNeeded,
                        "Accepted original is neither awaiting connection nor a verified registered launch.",
                        Some repairText
                | _, _, Some(OriginalBrowserOutcome.DefinitiveNoEffect reason) ->
                    FakeDispatchPhase.RepairNeeded,
                    "Original provider definitively refused: " + reason,
                    Some "WQ02 has no pre-registration no-effect terminal receipt. Retain the held WQ07 token until an independently verified terminal is available."
                | _, _, Some(OriginalBrowserOutcome.Unknown reason) ->
                    FakeDispatchPhase.RepairNeeded,
                    "Original outcome unknown: " + reason, Some repairText
                | _, _, None ->
                    FakeDispatchPhase.RepairNeeded,
                    "Durable original marker exists without a settled outcome.", Some repairText
                | _ ->
                    FakeDispatchPhase.RepairNeeded,
                    "Missing WQ02 launch or account token during original reconciliation.", Some repairText
        report phase detail verified accountHead (Some head.Commit) originOid action
            selected request dispatchId connectId

    // Test gates are inert by default; two disposable clients can synchronize
    // at both authority boundaries to reproduce the cross-host race.
    let dispatchFakeOnlyWithGates (beforeAdmission: unit -> unit)
                                  (beforeOriginalClaim: unit -> unit)
                                  explicitEnabled (planner: PlannerReport)
                         (account: AccountBinding) (project: LedgerBinding)
                         journalRoot (selected: PlannerCandidate)
                         (request: AccountAdmissionRequest)
                         dispatchId firstConnectId
                         (fakeProvider: OriginalBrowserIntent -> FakeOriginalReply) =
        let output phase detail verified accountHead projectHead oid repair =
            report phase detail verified accountHead projectHead oid repair
                selected request dispatchId firstConnectId
        if not explicitEnabled then
            output FakeDispatchPhase.Disabled "No fake-only execution was authorized." false None None None None
        elif not (validOperations selected request dispatchId firstConnectId)
             || isNull (box fakeProvider) then
            output FakeDispatchPhase.NotReady "Malformed/duplicate original, admission, dispatch or connect identity." false None None None None
        else
            match OriginalBrowserJournal.openExisting journalRoot with
            | Error message ->
                output FakeDispatchPhase.RepairNeeded ("WQ03 host continuity unavailable: " + message)
                    false None None None (Some repairText)
            | Ok journal ->
                match WorkQueueGitLedger.load project with
                | Error failure ->
                    output FakeDispatchPhase.RepairNeeded ("WQ02 project head unavailable: " + explain failure)
                        false None None None (Some repairText)
                | Ok (head, current) ->
                    match OriginalBrowserJournal.findByLaunch journal selected.LaunchId with
                    | Error failure ->
                        output FakeDispatchPhase.RepairNeeded ("Original one-shot read unresolved: " + failure)
                            false None (Some head.Commit) None (Some repairText)
                    | Ok(Some entry) when
                        verifyOriginalHostClaim project journal selected request <> Ok () ->
                        output FakeDispatchPhase.RepairNeeded
                            "WQ03 replay lacks the matching immutable canonical original-send host claim."
                            false None (Some head.Commit) None (Some repairText)
                    | Ok(Some entry) ->
                        if entry.Intent.OperationId <> selected.OriginalBrowserOperationId
                           || entry.Intent.ProjectId <> selected.ProjectId
                           || entry.Intent.WorkId <> selected.WorkId
                           || entry.Intent.RoleId <> selected.RoleId
                           || entry.Intent.RoleVersion <> selected.RoleVersion
                           || entry.Intent.RequestFingerprint <> selected.Source.RequestHash then
                            output FakeDispatchPhase.RepairNeeded "Existing original journal belongs to a different identity."
                                false None (Some head.Commit) None (Some repairText)
                        else
                            let currentAccount = AccountCapacityLedger.fetch account |> Result.toOption
                            replay entry head current currentAccount project None
                                selected request dispatchId firstConnectId
                    | Ok None when current.Launches.ContainsKey selected.LaunchId ->
                        output FakeDispatchPhase.RepairNeeded
                            "WQ02 launch already admitted, but its WQ03 original marker is absent."
                            false None (Some head.Commit) None (Some repairText)
                    | Ok None ->
                        match verifyNewSelection planner account project selected with
                        | Error why ->
                            let full = why.Contains("capacity is full", StringComparison.OrdinalIgnoreCase)
                            output (if full then FakeDispatchPhase.AtCapacity else FakeDispatchPhase.NotReady)
                                why false planner.AccountHead (Some head.Commit) None None
                        | Ok (_, _, origin, originOid) ->
                            beforeAdmission()
                            let demand : PracticalAdmissionDemand =
                                { WorkId = selected.WorkId; OriginBlobOid = originOid }
                            match WorkQueuePracticalAdmission.admitExplicit true account project request demand with
                            | GlobalReservationRejected(AccountCapacityFull _) ->
                                output FakeDispatchPhase.AtCapacity "WQ07 global CAS denied the competing slot."
                                    true None None (Some originOid) None
                            | ProjectAdmitted _ ->
                                match AccountCapacityLedger.fetch account, WorkQueueGitLedger.load project with
                                | Ok reserved, Ok (projectHead, admitted) ->
                                    let launch = admitted.Launches.TryFind selected.LaunchId
                                    let held = reserved.State.Tokens |> Array.exists (fun token ->
                                        token.Status = "Held" && token.LaunchId = selected.LaunchId
                                        && token.AdmissionOperationId = request.AdmissionOperationId.ToString("D"))
                                    if not held || not (launch |> Option.exists (fun l ->
                                        l.Credit = Some selected.WorkId
                                        && l.State = LaunchState.ReservedUnregistered
                                        && l.AccountTokenId = request.AdmissionOperationId.ToString("D"))) then
                                        output FakeDispatchPhase.RepairNeeded
                                            "Admitted role/work/token could not be reverified after the winning CAS."
                                            true (Some reserved.Head) (Some projectHead.Commit)
                                            (Some originOid) (Some repairText)
                                    else
                                        beforeOriginalClaim()
                                        let intent: OriginalBrowserIntent =
                                            { LaunchId = selected.LaunchId
                                              ProjectId = selected.ProjectId
                                              WorkId = selected.WorkId
                                              WorkGeneration = selected.WorkGeneration
                                              RoleId = selected.RoleId
                                              RoleVersion = selected.RoleVersion
                                              RequestFingerprint = origin.SourceRequestHash
                                              OperationId = selected.OriginalBrowserOperationId
                                              Provenance =
                                                { AccountHead = reserved.Head
                                                  ProjectHead = projectHead.Commit
                                                  SourceRef = optional origin.SourceRef
                                                  SourceCommit = optional origin.SourceCommit } }
                                        let accountHead = Some reserved.Head
                                        let projectHeadText = Some projectHead.Commit
                                        let out phase detail repair =
                                            output phase detail true accountHead projectHeadText
                                                (Some originOid) repair
                                        // A canonical create-only CAS must win before any local
                                        // WQ03 marker or fake provider callback is authorized.
                                        let mark =
                                            match claimOriginalHost project journal selected request with
                                            | Ok () -> OriginalBrowserJournal.tryMarkOriginal journal intent
                                            | Error reason -> Error("WQ11 canonical original-send authority: " + reason)
                                        match mark with
                                        | Error error ->
                                            out FakeDispatchPhase.RepairNeeded
                                                ("WQ03 pre-send marker not confirmed: " + error) (Some repairText)
                                        | Ok(OriginalMarkResult.Existing entry) ->
                                            replay entry projectHead admitted (Some reserved) project (Some originOid)
                                                selected request dispatchId firstConnectId
                                        | Ok OriginalMarkResult.Marked ->
                                            // The callback is the ONLY external seam; it is injected
                                            // in tests, never bound to production browser transport.
                                            let raw =
                                                try fakeProvider intent
                                                with ex -> FakeOriginalReply.Unknown("Fake callback threw: " + ex.Message)
                                            let outcome, session =
                                                match raw with
                                                | FakeOriginalReply.Accepted accepted
                                                    when accepted.LaunchId = intent.LaunchId
                                                         && accepted.WorkId = intent.WorkId
                                                         && nonBlank accepted.Receipt
                                                         && nonBlank accepted.SessionKey ->
                                                    OriginalBrowserOutcome.Accepted accepted.Receipt, Some accepted.SessionKey
                                                | FakeOriginalReply.Accepted _ ->
                                                    OriginalBrowserOutcome.Unknown "Fake callback identity/receipt/session did not match original.", None
                                                | FakeOriginalReply.DefinitiveNoEffect reason when nonBlank reason ->
                                                    OriginalBrowserOutcome.DefinitiveNoEffect reason, None
                                                | FakeOriginalReply.DefinitiveNoEffect _ ->
                                                    OriginalBrowserOutcome.Unknown "Fake provider returned an empty refusal.", None
                                                | FakeOriginalReply.Unknown reason ->
                                                    OriginalBrowserOutcome.Unknown(if nonBlank reason then reason else "Fake provider returned no outcome."), None
                                            match OriginalBrowserJournal.recordOriginalOutcome journal intent outcome with
                                            | Error error ->
                                                out FakeDispatchPhase.RepairNeeded
                                                    ("Original response not durably recorded: " + error) (Some repairText)
                                            | Ok () ->
                                                match outcome, session with
                                                | OriginalBrowserOutcome.Accepted receipt, Some sessionKey ->
                                                    match mutateFakeOnly project request.Actor dispatchId
                                                            (DispatchOnce(selected.LaunchId, DispatchState.Submitted)) with
                                                    | Error error ->
                                                        out FakeDispatchPhase.RepairNeeded
                                                            ("Accepted original, WQ02 dispatch needs repair: " + error) (Some repairText)
                                                    | Ok () ->
                                                        match mutateFakeOnly project request.Actor firstConnectId
                                                                (InitializeAgent(selected.LaunchId, sessionKey)) with
                                                        | Error error ->
                                                            out FakeDispatchPhase.AwaitFirstConnect
                                                                ("Accepted original, first-connect CAS unresolved: " + error)
                                                                (Some repairText)
                                                        | Ok () ->
                                                            match WorkQueueGitLedger.load project with
                                                            | Ok (_, verified) ->
                                                                match verified.Launches.TryFind selected.LaunchId with
                                                                | Some l ->
                                                                    match l.State with
                                                                    | LaunchState.Registered _ ->
                                                                        out FakeDispatchPhase.Registered
                                                                            ("Fake original accepted; WQ02 first-connect receipt committed: " + receipt) None
                                                                    | _ ->
                                                                        out FakeDispatchPhase.RepairNeeded
                                                                            "WQ02 did not retain the registered launch." (Some repairText)
                                                                | None ->
                                                                    out FakeDispatchPhase.RepairNeeded
                                                                        "WQ02 lost admitted launch after first-connect receipt." (Some repairText)
                                                            | Error error ->
                                                                out FakeDispatchPhase.RepairNeeded
                                                                    ("WQ02 first-connect verification failed: " + explain error) (Some repairText)
                                                | OriginalBrowserOutcome.DefinitiveNoEffect reason, _ ->
                                                    out FakeDispatchPhase.RepairNeeded
                                                        ("Definitive no-effect (" + reason + ") recorded; no WQ02 pre-registration terminal proof exists.")
                                                        (Some "Do not release held WQ07 capacity. A verified pre-registration no-effect terminal mutation/receipt is required.")
                                                | OriginalBrowserOutcome.Unknown reason, _ ->
                                                    match mutateFakeOnly project request.Actor dispatchId
                                                            (DispatchOnce(selected.LaunchId, DispatchState.OutcomeUnknown)) with
                                                    | Ok () ->
                                                        out FakeDispatchPhase.RepairNeeded
                                                            ("WQ03/WQ02 original outcome unknown: " + reason) (Some repairText)
                                                    | Error error ->
                                                        out FakeDispatchPhase.RepairNeeded
                                                            ("Original unknown; WQ02 CAS also unresolved: " + error) (Some repairText)
                                                | _ ->
                                                    out FakeDispatchPhase.RepairNeeded "Invalid fake callback outcome." (Some repairText)
                                | _ ->
                                    output FakeDispatchPhase.RepairNeeded
                                        "Winning WQ04/WQ07 admission cannot be independently re-fetched."
                                        true None None (Some originOid) (Some repairText)
                            | GlobalReservationUncertain original ->
                                output FakeDispatchPhase.RepairNeeded
                                    ("WQ07 reserve acknowledgment unknown: " + original.ToString("D"))
                                    true None None (Some originOid) (Some repairText)
                            | TokenRetained reason ->
                                output FakeDispatchPhase.RepairNeeded ("WQ04/C01 admission needs reconciliation: " + reason)
                                    true None None (Some originOid) (Some repairText)
                            | UnboundAbortedAndReleased head ->
                                output FakeDispatchPhase.NotReady
                                    "WQ04 CAS rejected selected Work; verified C01 unbound abort/release."
                                    true (Some head) None (Some originOid) None
                            | GlobalReservationRejected failure ->
                                output FakeDispatchPhase.NotReady
                                    ("WQ07 rejected admission: " + explain failure)
                                    true None None (Some originOid) None

    // No timer-driven completion. The test fixture explicitly supplies stable
    // claim and terminal UUIDs; WQ02 authoritatively chooses the claim and
    // publishes the terminal witness before WQ07 can release the held token.
    let dispatchFakeOnly explicitEnabled planner account project journalRoot
                         selected request dispatchId firstConnectId fakeProvider =
        dispatchFakeOnlyWithGates ignore ignore explicitEnabled planner account
            project journalRoot selected request dispatchId firstConnectId fakeProvider

    let completeFakeOnly explicitEnabled (account: AccountBinding) (project: LedgerBinding)
                         journalRoot (selected: PlannerCandidate)
                         (request: AccountAdmissionRequest) claimId terminalId =
        let output phase detail repair =
            report phase detail true None None None repair
                selected request claimId terminalId
        if not explicitEnabled then
            output FakeDispatchPhase.Disabled "Fake completion is default-off." None
        elif claimId = Guid.Empty || terminalId = Guid.Empty || claimId = terminalId
             || claimId = request.AdmissionOperationId || terminalId = request.ReleaseOperationId then
            output FakeDispatchPhase.NotReady "Invalid stable claim/terminal identities." None
        else
            match OriginalBrowserJournal.openExisting journalRoot with
            | Error error -> output FakeDispatchPhase.RepairNeeded ("Original continuity missing: " + error) (Some repairText)
            | Ok journal ->
                match OriginalBrowserJournal.findByLaunch journal selected.LaunchId with
                | Ok(Some entry) when entry.Intent.OperationId = selected.OriginalBrowserOperationId
                                     && entry.Intent.LaunchId = request.LaunchId.ToString("D")
                                     && entry.Intent.ProjectId = project.ProjectId.ToString("D")
                                     && entry.Intent.ProjectId = selected.ProjectId
                                     && entry.Intent.WorkId = selected.WorkId
                                     && entry.Intent.WorkGeneration = selected.WorkGeneration
                                     && entry.Intent.RoleId = selected.RoleId
                                     && entry.Intent.RoleId = request.RoleId
                                     && entry.Intent.RoleVersion = selected.RoleVersion
                                     && entry.Intent.RoleVersion = request.RoleVersion
                                     && entry.Intent.RequestFingerprint = selected.Source.RequestHash
                                     && entry.Intent.Provenance.SourceRef = selected.Source.SourceRef
                                     && entry.Intent.Provenance.SourceCommit = selected.Source.SourceCommit
                                     && (entry.Outcome |> Option.exists (function
                                         | OriginalBrowserOutcome.Accepted _ -> true
                                         | _ -> false)) ->
                    let read () = WorkQueueGitLedger.load project |> Result.mapError explain
                    let release () =
                        match read () with
                        | Error why -> output FakeDispatchPhase.RepairNeeded ("Terminal head unavailable: " + why) (Some repairText)
                        | Ok (head, state) ->
                            match WorkQueueGitLedger.readTerminalAt project head selected.LaunchId with
                            | Ok(Some (terminal, _)) when
                                terminal.TerminalOperationId = terminalId.ToString("D")
                                && terminal.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
                                && (state.Works.TryFind selected.WorkId
                                    |> Option.exists (fun work -> work.State = WorkState.Completed)) ->
                                let locator: ProjectTerminalLocator =
                                    { ProjectId = project.ProjectId
                                      LaunchId = request.LaunchId
                                      AdmissionOperationId = request.AdmissionOperationId
                                      TerminalOperationId = terminalId }
                                match AccountAdmissionAdapter.releaseTerminal account project request.Actor
                                          request.ReleaseOperationId locator with
                                | Error failure ->
                                    output FakeDispatchPhase.RepairNeeded
                                        ("Verified terminal exists, but WQ07 release is unresolved: " + explain failure)
                                        (Some repairText)
                                | Ok _ ->
                                    match AccountCapacityLedger.fetch account with
                                    | Ok latest when latest.State.Tokens |> Array.exists (fun t ->
                                        t.LaunchId = selected.LaunchId && t.Status = "Released"
                                        && t.AdmissionOperationId = request.AdmissionOperationId.ToString("D")) ->
                                        { output FakeDispatchPhase.TerminalReleased
                                            "WQ02 terminal witness and WQ07 global release verified." None with
                                            AccountHead = Some latest.Head; ProjectHead = Some head.Commit }
                                    | _ -> output FakeDispatchPhase.RepairNeeded
                                               "WQ07 release receipt not independently visible." (Some repairText)
                            | _ ->
                                output FakeDispatchPhase.RepairNeeded
                                    "No matching immutable WQ02 terminal witness: global token must remain held."
                                    (Some repairText)
                    match read () with
                    | Error why -> output FakeDispatchPhase.RepairNeeded ("Project head unavailable: " + why) (Some repairText)
                    | Ok (_, state) ->
                        match state.Launches.TryFind selected.LaunchId with
                        | Some launch when launch.ProjectId <> selected.ProjectId
                                           || launch.AccountTokenId <> request.AdmissionOperationId.ToString("D")
                                           || launch.RoleId <> request.RoleId
                                           || launch.RoleVersion <> request.RoleVersion ->
                            output FakeDispatchPhase.RepairNeeded
                                "WQ02 launch and its original account/role ownership do not match." (Some repairText)
                        | Some launch when launch.State = LaunchState.TerminalPendingRelease("terminal:" + terminalId.ToString("D")) ->
                            release ()
                        | Some launch ->
                            match launch.State with
                            | LaunchState.Registered _ when
                                not (match AccountCapacityLedger.fetch account with
                                     | Ok snapshot ->
                                         let authority =
                                             AccountAdmissionAdapter.projectAuthority project
                                             |> AccountCapacityLedger.projectAuthoritySha256
                                         snapshot.State.Tokens |> Array.exists (fun t ->
                                             t.Status = "Held"
                                             && t.LaunchId = selected.LaunchId
                                             && t.AdmissionOperationId = request.AdmissionOperationId.ToString("D")
                                             && t.ProjectId = selected.ProjectId
                                             && t.ProjectLedgerId = project.LedgerId.ToString("D")
                                             && t.ProjectLedgerEpoch = project.Epoch
                                             && t.ProjectAuthoritySha256 = authority
                                             && t.RoleId = request.RoleId
                                             && t.RoleConfigVersion = int64 request.RoleVersion)
                                     | Error _ -> false) ->
                                output FakeDispatchPhase.RepairNeeded
                                    "Original global WQ07 token is not verifiably held; no claim may run." (Some repairText)
                            | LaunchState.Registered _ ->
                                let claim =
                                    if launch.PrimaryWorkId = Some selected.WorkId then Ok ()
                                    elif launch.EverClaimed then Error "A different Work was already claimed."
                                    else mutateFakeOnly project request.Actor claimId (ClaimNext selected.LaunchId)
                                match claim with
                                | Error why -> output FakeDispatchPhase.RepairNeeded
                                                    ("Claim-next CAS needs repair: " + why) (Some repairText)
                                | Ok () ->
                                    match read () with
                                    | Ok (_, afterClaim) when
                                        (afterClaim.Launches.TryFind selected.LaunchId
                                         |> Option.exists (fun l -> l.PrimaryWorkId = Some selected.WorkId))
                                        && (afterClaim.Works.TryFind selected.WorkId
                                            |> Option.exists (fun w -> w.State = WorkState.Claimed selected.LaunchId)) ->
                                        match mutateFakeOnly project request.Actor terminalId
                                                (CompleteOrdinary(selected.LaunchId, selected.WorkId)) with
                                        | Ok () -> release ()
                                        | Error why -> output FakeDispatchPhase.RepairNeeded
                                                            ("WQ02 completion CAS unresolved: " + why) (Some repairText)
                                    | _ -> output FakeDispatchPhase.RepairNeeded
                                               "Claim-next selected a different Work or was unverified." (Some repairText)
                            | _ ->
                                output FakeDispatchPhase.NotReady
                                    "No fake registered/terminal launch can claim or release this Work." None
                        | None -> output FakeDispatchPhase.NotReady "Canonical launch is absent." None
                | Ok _ -> output FakeDispatchPhase.RepairNeeded
                              "Matching WQ03 accepted original is missing; cannot claim or release." (Some repairText)
                | Error why -> output FakeDispatchPhase.RepairNeeded
                                   ("WQ03 original read unresolved: " + why) (Some repairText)
