module Client.WorkQueueToolFacade

open System
open System.Text.RegularExpressions
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.TaskFirstUiProjection

// Work is the durable identity. A launch is ephemeral evidence of a claim;
// neither the caller's legacy Agent label nor the authenticated operator is
// stored as a Work owner. This facade never creates another ledger.
type WorkDependencyReason =
    { WorkId: string
      StageGeneration: int option
      ClaimGeneration: int option
      State: string
      Satisfied: bool
      WaiverReceipt: string option
      Reason: string }

type WorkToolRow =
    { WorkId: string
      SubjectId: string
      QueueId: string
      Priority: int
      Kind: string
      State: string
      Category: string
      StageGeneration: int
      ClaimGeneration: int
      GraphVersion: int
      Gate: string
      Domain: string
      ClaimLaunchId: string option
      SessionUuid: string option
      Liveness: string
      Dependencies: WorkDependencyReason list
      Reasons: string list
      ClaimEnabled: bool }

type WorkToolSnapshot =
    { ProjectId: string
      VerifiedGitHead: string
      ProjectVersion: int
      GraphVersion: int
      Coverage: string
      RoleAuthority: string
      ClaimBinding: string
      Work: WorkToolRow list
      ReadOnly: bool }

type WorkPriorityTransition =
    { Status: string
      Applied: bool
      OperationId: string
      VerifiedGitHead: string
      ProjectVersion: int
      WorkId: string
      Priority: int
      Reason: string }

type WorkToolTransition =
    { Status: string
      Applied: bool
      OperationId: string
      VerifiedGitHead: string
      ProjectVersion: int
      LaunchId: string
      WorkId: string
      MissingBinding: string }

// WQ02's ClaimNext is a real project CAS in Frozen fixture tests, but the
// accepted production path explicitly rejects it until the WQ04 same-CAS
// selected-work/role/credit proof and WQ03 ephemeral launch/session journal
// are wired. Never fall back to legacy TaskWrite/Agent assignment here.
module WorkQueueToolFacade =
    [<Literal>]
    let MissingClaimBinding =
        "WQ04 same-CAS selected Work, pinned role, account credit and WQ03 verified ephemeral launch/session binding is not installed; Scheduler dispatch is default-off."

    [<Literal>]
    let MissingTerminalBinding =
        "WQ03 verified session/terminal and source-effect journal is not joined to WQ02 claim generation; checkpoint/complete is default-off, not Accepted or Integrated."

    let private status (stage: WorkStage) = sprintf "%A" stage.State

    let private dependency (project: Project) (stage: WorkStage) id =
        let waiver = stage.WaivedDependencies.TryFind id
        match project.Works.TryFind id with
        | None ->
            { WorkId = id; StageGeneration = None; ClaimGeneration = None
              State = "Unknown"; Satisfied = false; WaiverReceipt = waiver
              Reason = sprintf "UNKNOWN prerequisite %s at graph v%d; no canonical stage" id project.GraphVersion }
        | Some prerequisite ->
            let met = Contracts.isSatisfied project id
            let reason =
                if waiver.IsSome then
                    sprintf "Waived at graph v%d by recorded receipt %s" project.GraphVersion waiver.Value
                elif met then
                    sprintf "Satisfied by completed stage generation %d at graph v%d" prerequisite.StageGeneration project.GraphVersion
                else
                    sprintf "Unmet prerequisite stage generation %d, claim generation %d, state %A, graph v%d"
                        prerequisite.StageGeneration prerequisite.ClaimGeneration prerequisite.State project.GraphVersion
            { WorkId = id; StageGeneration = Some prerequisite.StageGeneration
              ClaimGeneration = Some prerequisite.ClaimGeneration
              State = status prerequisite; Satisfied = met || waiver.IsSome
              WaiverReceipt = waiver; Reason = reason }

    let private classification (project: Project) (next: Set<string>)
                               (blocked: Set<string>) (claimed: Map<string, RowState>)
                               coverage (stage: WorkStage) =
        let fallback = if coverage = "Verified" then [] else [coverage]
        match stage.State with
        | WorkState.Completed -> "Terminal", fallback
        | WorkState.Cancelled -> "Terminal", fallback
        | _ when coverage <> "Verified" -> "Unknown", fallback
        | WorkState.Claimed launch ->
            match project.Launches.TryFind launch with
            | None -> "Unknown", fallback @ ["Claimed launch missing from pinned project snapshot"]
            | Some owner when owner.PrimaryWorkId <> Some stage.Id ->
                "Unknown", fallback @ ["Launch primary Work ID does not match this claim"]
            // WQ09 includes recovery-held claims in both ActiveAndStalled and
            // Blocked; preserve inconsistent links as Unknown, but a valid
            // claim under a recovery gate is Blocked, never Active.
            | Some _ when blocked.Contains stage.Id -> "Blocked", []
            | Some _ ->
                // A primary-Work link proves a claim, not a live session.
                // WQ09's row state requires reliable heartbeat evidence before
                // calling a claim Active; the current read supplies no clock.
                match claimed.TryFind stage.Id with
                | Some (RowState.Active id) when id = launch -> "Active", []
                | Some (RowState.StopRecovery _) ->
                    "Blocked", ["Claimed launch has a stop request pending recovery"]
                | Some (RowState.RecoveryHold reason) -> "Blocked", [reason]
                | Some (RowState.StaleHeartbeat _) ->
                    "Unknown", ["Claimed launch heartbeat is stale; recovery required"]
                | Some (RowState.Uncertain reason) -> "Unknown", [reason]
                | _ -> "Unknown", ["Claimed launch has no verified live WQ09 row"]
        | _ when next.Contains stage.Id -> "Available", []
        | _ when blocked.Contains stage.Id -> "Blocked", []
        | _ -> "Unknown", ["Unclassified by verified WQ09 projection at graph v" + string project.GraphVersion]

    /// Pure verified-snapshot projection for deterministic fixtures. 'Available'
    /// is a *queue observation*, never a dispatch or account capacity permit.
    let inspectAt projectId head (project: Project) (roleAuthority: Result<PinnedRoleView, string>) =
        let dashboard =
            projectDashboard projectId Map.empty (Ok project)
                (Error "Account/project cross-ledger admission proof is not joined")
                None 0L
        let next, blocked, claimed, coverage =
            match dashboard.Next, dashboard.Blocked, dashboard.ActiveAndStalled, dashboard.QueueHealth with
            | Evidence.Known ready, Evidence.Known held, Evidence.Known claimedRows, Evidence.Known _ ->
                Set.ofList (ready |> List.map (fun row -> row.WorkId)),
                Set.ofList (held |> List.map (fun row -> row.WorkId)),
                (claimedRows |> List.map (fun row -> row.WorkId, row.State) |> Map.ofList), "Verified"
            | Evidence.Unknown reason, _, _, _
            | _, Evidence.Unknown reason, _, _
            | _, _, Evidence.Unknown reason, _
            | _, _, _, Evidence.Unknown reason ->
                Set.empty, Set.empty, Map.empty, "UNKNOWN: " + reason
        // WQ09's Next retains blocker-first, priority-descending, FIFO-tied ordering from WQ02
        // contract; the tool must not reorder it by raw availability timestamps.
        let nextPositions =
            match dashboard.Next with
            | Evidence.Known ready ->
                ready |> List.mapi (fun index row -> row.WorkId, index) |> Map.ofList
            | Evidence.Unknown _ -> Map.empty
        let rank (stage: WorkStage) =
            match nextPositions.TryFind stage.Id with
            | Some index -> (0, index, 0L, 0L, stage.Id)
            | None -> (1, 0, stage.AvailableAt, stage.AvailableSequence, stage.Id)
        let role =
            match roleAuthority with
            | Ok pinned when pinned.Proof.Head = head
                              && pinned.Proof.ProjectId = projectId
                              && pinned.Project = project ->
                sprintf "Pinned WQ04 catalog revision %d at %s (observation only)"
                    pinned.Catalog.Revision head
            | Ok _ -> "UNKNOWN: WQ04 project/role head or content mismatch"
            | Error reason -> "UNKNOWN: WQ04 role provenance unavailable: " + reason
        let priorities = Contracts.priorityDecisions project
        let rows =
            project.Works
            |> Map.toList
            |> List.map snd
            |> List.sortBy rank
            |> List.map (fun stage ->
                let category, notes = classification project next blocked claimed coverage stage
                let launchId =
                    match stage.State with
                    | WorkState.Claimed id -> Some id
                    | _ -> None
                let session, liveness =
                    match launchId |> Option.bind project.Launches.TryFind with
                    | Some launch ->
                        match launch.State with
                        | LaunchState.Registered (uuid, _) when not (String.IsNullOrWhiteSpace uuid) ->
                            Some uuid, "UNKNOWN: live session/terminal journal not bound"
                        | _ -> None, "UNKNOWN: launch not registered with a verified session UUID"
                    | None when launchId.IsSome -> None, "UNKNOWN: claimed launch missing from pinned project snapshot"
                    | None -> None, "Not claimed"
                let dependencies =
                    stage.Dependencies |> Set.toList |> List.map (dependency project stage)
                let unresolved = dependencies |> List.filter (fun item -> not item.Satisfied)
                let reasons =
                    notes
                    @ (if stage.Gate = GateState.Clear then [] else [sprintf "Gate: %A at graph v%d" stage.Gate project.GraphVersion])
                    @ (unresolved |> List.map (fun item -> item.Reason))
                    @ (if category = "Blocked" && unresolved.IsEmpty && stage.Gate = GateState.Clear
                       then [sprintf "Canonical %A state blocks work at graph v%d" stage.State project.GraphVersion]
                       else [])
                { WorkId = stage.Id; SubjectId = stage.SubjectId
                  QueueId = stage.QueueId
                  Priority = priorities.TryFind stage.Id
                             |> Option.map (fun decision -> decision.Priority)
                             |> Option.defaultValue 0
                  Kind = sprintf "%A" stage.Kind
                  State = status stage; Category = category
                  StageGeneration = stage.StageGeneration
                  ClaimGeneration = stage.ClaimGeneration
                  GraphVersion = project.GraphVersion
                  Gate = sprintf "%A" stage.Gate; Domain = sprintf "%A" stage.Domain
                  ClaimLaunchId = launchId; SessionUuid = session
                  Liveness = liveness; Dependencies = dependencies
                  Reasons = reasons; ClaimEnabled = false })
        { ProjectId = projectId; VerifiedGitHead = head
          ProjectVersion = project.Version; GraphVersion = project.GraphVersion
          Coverage = coverage; RoleAuthority = role
          ClaimBinding = MissingClaimBinding; Work = rows; ReadOnly = true }

    let readWithBinding (binding: LedgerBinding) action category workId =
        let validCategory =
            Set.ofList ["All"; "Available"; "Blocked"; "Active"; "Terminal"; "Unknown"]
        if action <> "List" && action <> "Get" then
            Error(InvalidBinding "Unsupported Work read action.")
        elif action = "List" && not (validCategory.Contains category) then
            Error(InvalidBinding "Work category must be All, Available, Blocked, Active, Terminal or Unknown.")
        elif action = "Get" && String.IsNullOrWhiteSpace workId then
            Error(InvalidBinding "A Work ID is required for Get.")
        else
            WorkQueueGitLedger.load binding
            |> Result.bind (fun (head, project) ->
                let role =
                    WorkQueueRoleAuthority.loadAt binding head
                    |> Result.mapError (sprintf "%A")
                let view = inspectAt (binding.ProjectId.ToString("D")) head.Commit project role
                let selected =
                    if action = "Get" then
                        view.Work |> List.filter (fun row -> row.WorkId = workId)
                    elif category = "All" then view.Work
                    else view.Work |> List.filter (fun row -> row.Category = category)
                if action = "Get" && List.isEmpty selected then
                    Error(InvalidBinding ("Unknown canonical Work ID " + workId))
                else Ok { view with Work = selected })

    let private authenticatedActor (actor: string) =
        not (isNull actor) && actor.Length = 73
        && actor.StartsWith("operator:", StringComparison.Ordinal)
        && (actor.Substring(9) |> Seq.forall (fun c ->
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let setPriorityWithBinding (binding: LedgerBinding) (actor: string)
                               (operationId: string) (workId: string)
                               expectedProjectVersion priority (reason: string) =
        if not (authenticatedActor actor) || String.IsNullOrWhiteSpace workId
           || expectedProjectVersion < 0 || String.IsNullOrWhiteSpace reason
           || reason.Length > 1024 || reason <> reason.Trim() then
            Error(InvalidBinding "Priority edit requires authenticated operator, Work ID, nonnegative expected version and explicit bounded reason.")
        else
            match Guid.TryParse operationId with
            | true, operation when operation <> Guid.Empty ->
                WorkQueueGitLedger.setPriority binding actor operation
                    expectedProjectVersion workId priority reason
                |> Result.bind (fun written ->
                    WorkQueueGitLedger.load binding
                    |> Result.map (fun (snapshot, state) ->
                        { Status = if written.AlreadyApplied then "IdempotentReplay" else "Applied"
                          Applied = true; OperationId = operation.ToString("D")
                          VerifiedGitHead = snapshot.Commit; ProjectVersion = state.Version
                          WorkId = workId; Priority = Contracts.priorityOf state workId
                          Reason = reason }))
            | _ -> Error(InvalidBinding "Priority operation UUID must be nonempty.")

    // Deliberately returns an explicit non-applied result. No mutation call,
    // synthetic claim owner, new UUID, source write or Task assignment occurs.
    let transitionWithBinding (binding: LedgerBinding) (action: string) (actor: string) (operationId: string)
                              (launchId: string) (workId: string) expectedProjectVersion claimGeneration evidenceSha =
        let validId = match Guid.TryParse operationId with | true, id -> id <> Guid.Empty | _ -> false
        let validEvidence =
            Regex.IsMatch((if isNull evidenceSha then "" else evidenceSha), "^[a-f0-9]{64}$")
        if not (authenticatedActor actor) then
            Error(InvalidBinding "Work transition requires an OAuth-authenticated operator actor.")
        elif not validId || String.IsNullOrWhiteSpace launchId || expectedProjectVersion < 0
             || claimGeneration < 0 then
            Error(InvalidBinding "Stable operation UUID, launch ID and nonnegative CAS versions are required.")
        elif action <> "ClaimNext" && action <> "Checkpoint" && action <> "Complete" then
            Error(InvalidBinding "Unsupported Work transition action.")
        elif action <> "ClaimNext" && (String.IsNullOrWhiteSpace workId || not validEvidence) then
            Error(InvalidBinding "Checkpoint/complete requires canonical Work ID and evidence SHA-256.")
        else
            WorkQueueGitLedger.load binding
            |> Result.bind (fun (head, project) ->
                if project.Version <> expectedProjectVersion then
                    Error(StaleEntityVersion
                        (sprintf "Work CAS project version changed: expected %d, observed %d at %s"
                            expectedProjectVersion project.Version head.Commit))
                else
                    Ok { Status = "RejectedMissingBinding"; Applied = false
                         OperationId = operationId; VerifiedGitHead = head.Commit
                         ProjectVersion = project.Version; LaunchId = launchId; WorkId = workId
                         MissingBinding =
                           if action = "ClaimNext" then MissingClaimBinding else MissingTerminalBinding })

    // Model-visible Work bearer is an explicit ordinary tool argument. The
    // public launchId is only an index into the original Git verifier, never
    // authentication. A valid bearer still cannot enable default-off live
    // claim/registration: WQ03/WQ04 session/source CAS remains a separate gate.
    let transitionWithExplicitCredential (binding: LedgerBinding) (action: string)
            (actor: string) (operationId: string) (launchId: string)
            (credential: string) (workId: string) expectedProjectVersion
            claimGeneration evidenceSha =
        let operation =
            match Guid.TryParse operationId with
            | true, id when id <> Guid.Empty -> Some id
            | _ -> None
        let credentialAction =
            match action with
            | "FirstConnect" -> Some CredentialWorkAction.FirstConnect
            | "ClaimNext" -> Some CredentialWorkAction.ClaimPrimary
            | "Checkpoint" ->
                Some(CredentialWorkAction.Checkpoint(workId, claimGeneration, evidenceSha))
            | "Complete" ->
                Some(CredentialWorkAction.CompleteReadOnly(workId, claimGeneration, evidenceSha))
            | _ -> None
        let evidenceValid =
            Regex.IsMatch((if isNull evidenceSha then "" else evidenceSha), "^[a-f0-9]{64}$")
        if not (authenticatedActor actor) then
            Error(InvalidBinding "Work transition requires an OAuth-authenticated operator actor.")
        elif operation.IsNone || String.IsNullOrWhiteSpace launchId
             || expectedProjectVersion < 0 || claimGeneration < 0 then
            Error(InvalidBinding "Stable operation UUID, launch ID and nonnegative CAS versions are required.")
        elif credentialAction.IsNone then
            Error(InvalidBinding "Unsupported explicit-credential Work action.")
        elif action = "Checkpoint" || action = "Complete" then
            if String.IsNullOrWhiteSpace workId || not evidenceValid then
                Error(InvalidBinding "Checkpoint/complete requires canonical Work ID and evidence SHA-256.")
            else
                WorkQueueGitLedger.load binding
                |> Result.bind (fun (head, project) ->
                    if project.Version <> expectedProjectVersion then
                        Error(StaleEntityVersion "Work CAS project version changed.")
                    else
                        WorkQueueGitLedger.verifyBearerAt binding head project launchId
                            credential operation.Value credentialAction.Value
                        |> Result.map (fun () ->
                            { Status = "RejectedMissingBinding"; Applied = false
                              OperationId = operationId; VerifiedGitHead = head.Commit
                              ProjectVersion = project.Version; LaunchId = launchId; WorkId = workId
                              MissingBinding = MissingTerminalBinding }))
        else
            WorkQueueGitLedger.load binding
            |> Result.bind (fun (head, project) ->
                if project.Version <> expectedProjectVersion then
                    Error(StaleEntityVersion "Work CAS project version changed.")
                else
                    WorkQueueGitLedger.verifyBearerAt binding head project launchId
                        credential operation.Value credentialAction.Value
                    |> Result.map (fun () ->
                        { Status = "RejectedMissingBinding"; Applied = false
                          OperationId = operationId; VerifiedGitHead = head.Commit
                          ProjectVersion = project.Version; LaunchId = launchId; WorkId = workId
                          MissingBinding =
                              if action = "FirstConnect" then
                                  "WQ03 original provider dispatch and execution continuity is not bound to the live Work relay; registration remains default-off."
                              else MissingClaimBinding }))
