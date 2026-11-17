module Client.WorkStatefulProjection

open System
open Client.WorkStatefulContracts

// Pure H0 view model. It cannot claim, approve, dispatch or mutate Work.
// "Available" describes a supplied snapshot, never a grant or live action.
type Query =
    { ProjectId: string option
      RoleId: string option
      QueueId: string option
      Search: string }

type NodeRow =
    { Key: string
      Path: string
      State: string
      Proposal: string
      Acceptance: string
      RequiredForParent: bool
      Details: string list }

type StageRow =
    { Key: string
      Path: string
      Queue: string
      Kind: string
      LeadPriority: string
      Blocking: string
      Readiness: string
      Claim: string
      Review: string
      Integration: string
      Details: string list }

type Dashboard =
    { Banner: string
      Nodes: NodeRow list
      Stages: StageRow list
      Capacity: string
      Backup: string
      Recovery: string
      Diagnostics: string list
      HistoricalAgentAudit: string
      ActionsEnabled: bool }

let private unknown reason = "UNKNOWN / NotReady: " + reason
let private nonblank (s: string) = not (String.IsNullOrWhiteSpace s)

let private evidence revision item =
    match item with
    | Evidence.Verified (atRevision, value) when atRevision = revision -> Ok value
    | Evidence.Verified (atRevision, _) ->
        Error(sprintf "Evidence revision %d differs from snapshot %d" atRevision revision)
    | Evidence.Unknown reason -> Error reason

let private unavailable reason =
    { Banner = unknown reason
      Nodes = []
      Stages = []
      Capacity = "UNKNOWN"
      Backup = "UNKNOWN"
      Recovery = "UNKNOWN / fenced until verified"
      Diagnostics = [ reason ]
      HistoricalAgentAudit = "Existing historical Agent audit remains a separate read-only view"
      ActionsEnabled = false }

let private validOid (oid: string) =
    nonblank oid
    && (oid.Length = 40 || oid.Length = 64)
    && (oid |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

let private kind = function
    | NodeKind.Project -> "Project"
    | NodeKind.System -> "System"
    | NodeKind.Feature -> "Feature"

let private stageKind = function
    | StageKind.Planning -> "Planning"
    | StageKind.Approval -> "Lead approval"
    | StageKind.Implementation -> "Implementation"
    | StageKind.FrozenReview -> "Independent review"
    | StageKind.Integration -> "Integration"
    | StageKind.Recovery -> "Recovery"
    | StageKind.RoleMessage -> "Role message"

let private proposal = function
    | ProposalState.NotSubmitted -> "Not submitted"
    | ProposalState.AwaitingLead -> "Awaiting Lead decision"
    | ProposalState.Returned reason -> "Returned to Draft: " + reason
    | ProposalState.Approved decision when nonblank decision -> "Approved: " + decision
    | ProposalState.Approved _ -> unknown "Approval decision reference missing"

let private lifecycle (node: WorkNode) =
    match node.State, node.Proposal with
    | NodeState.Draft, ProposalState.Returned _ -> "Draft / Returned"
    | NodeState.Draft, _ -> "Draft (no execution)"
    | NodeState.Proposed, ProposalState.AwaitingLead -> "Proposed (Lead decision pending)"
    | NodeState.Proposed, _ -> unknown "Proposed without Lead demand evidence"
    | NodeState.Open, ProposalState.Approved id when nonblank id -> "Open"
    | NodeState.Open, _ -> unknown "Open without approved decision"
    | NodeState.Active, ProposalState.Approved id when nonblank id -> "Active"
    | NodeState.Active, _ -> unknown "Active without approved decision"
    | NodeState.Completed, _ -> "Completed (acceptance separate)"
    | NodeState.Closed, _ -> "Closed"
    | NodeState.Cancelled, _ -> "Cancelled"

let private accepted = function
    | AcceptanceState.NotRequired -> "Not applicable"
    | AcceptanceState.AwaitingIndependentAcceptance -> "Awaiting independent acceptance"
    | AcceptanceState.Accepted reference when nonblank reference -> "Accepted: " + reference
    | AcceptanceState.Accepted _ -> unknown "Acceptance evidence reference missing"

let private review = function
    | ReviewStage.NotRequired -> "Not required"
    | ReviewStage.AwaitingFrozenSource -> "Awaiting frozen source"
    | ReviewStage.Frozen sha when validOid sha -> "Frozen " + sha
    | ReviewStage.Accepted sha when validOid sha -> "Accepted frozen " + sha
    | ReviewStage.ChangesRequested sha when validOid sha -> "Changes requested at " + sha
    | ReviewStage.Unknown why -> unknown ("Review: " + why)
    | _ -> unknown "Invalid review SHA"

let private integration = function
    | IntegrationStage.NotRequired -> "Not required"
    | IntegrationStage.AwaitingAcceptedReview -> "Awaiting accepted review"
    | IntegrationStage.Submitted sha when validOid sha -> "Submitted exact " + sha
    | IntegrationStage.Integrated sha when validOid sha -> "Integrated exact " + sha
    | IntegrationStage.Unknown why -> unknown ("Integration: " + why)
    | _ -> unknown "Invalid integration SHA"

let private rowKey projectId localId = projectId + "/" + localId

let private pathFor (nodes: Map<string * string, WorkNode>) (node: WorkNode) =
    let rec visit (visited: Set<string * string>) (current: WorkNode) =
        let currentKey = current.ProjectId, current.Id
        if Set.contains currentKey visited then Error "Cycle in Project/System/Feature ancestry"
        else
            let currentLabel = kind current.Kind + ": " + current.Title
            match current.ParentId with
            | None when current.Kind = NodeKind.Project -> Ok [ currentLabel ]
            | None -> Error "Non-project Work unit has no parent"
            | Some parentId ->
                match Map.tryFind (current.ProjectId, parentId) nodes with
                | None -> Error ("Missing parent " + parentId)
                | Some parent when parent.ProjectId <> current.ProjectId ->
                    Error "Parent belongs to another project"
                | Some parent ->
                    visit (Set.add currentKey visited) parent
                    |> Result.map (fun result -> result @ [ currentLabel ])
    visit Set.empty node |> Result.map (String.concat " / ")

let private recoveryLine revision source =
    match evidence revision source with
    | Error reason -> unknown ("Recovery: " + reason)
    | Ok RecoveryMode.Normal -> "Normal (no Recovery fence at this revision)"
    | Ok(RecoveryMode.Fenced reason) -> "FENCED: " + reason
    | Ok(RecoveryMode.Investigating reason) -> "RECOVERY: " + reason

let private capacityLine revision source =
    match evidence revision source with
    | Error reason -> unknown ("Account capacity: " + reason)
    | Ok capacity when capacity.Limit < 0 || capacity.Occupied < 0
                       || capacity.Occupied > capacity.Limit ->
        unknown "Inconsistent account capacity"
    | Ok capacity -> sprintf "Global capacity %d/%d occupied" capacity.Occupied capacity.Limit

let private backupLine now revision source =
    match evidence revision source with
    | Error reason -> unknown ("Backup: " + reason)
    | Ok backup when not backup.VerifiedExport
                     || backup.ExportSchemaVersion <= 0
                     || backup.ExportedRevision < 0L
                     || backup.ExportedRevision > revision ->
        unknown "Backup export not verified or revision inconsistent"
    | Ok backup when backup.SnapshotAt > now
                     || (backup.PublishedAt |> Option.exists (fun published -> published > now || published < backup.SnapshotAt)) ->
        unknown "Backup time is inconsistent with observation clock"
    | Ok backup ->
        let lag = revision - backup.ExportedRevision
        let age = int64 (now - backup.SnapshotAt).TotalMinutes
        let publication =
            backup.PublishedAt
            |> Option.map (fun value -> "published " + value.ToString("u"))
            |> Option.defaultValue "NOT yet published to Git"
        sprintf "Backup revision %d / database revision %d (lag %d, age %dm; %s)"
            backup.ExportedRevision revision lag age publication

let private claimLine revision launchConflicts state =
    match state with
    | StageState.Claimed claim when not (nonblank claim.LaunchId) || claim.Generation <= 0 ->
        unknown "Claim identity/generation missing"
    | StageState.Claimed claim when Set.contains claim.LaunchId launchConflicts ->
        unknown ("Launch " + claim.LaunchId + " has multiple primary claims")
    | StageState.Claimed claim ->
        let execution =
            match evidence revision claim.Execution with
            | Ok ExecutionObservation.Running -> "observed running"
            | Ok ExecutionObservation.Stopped -> "stopped"
            | Ok(ExecutionObservation.Unknown reason)
            | Error reason -> "liveness UNKNOWN: " + reason
        sprintf "Launch %s / claim generation %d / %s (not a durable Agent owner)"
            claim.LaunchId claim.Generation execution
    | _ -> "Unclaimed"

let private evidenceText revision label item formatter =
    match evidence revision item with
    | Ok value -> formatter value
    | Error reason -> unknown (label + ": " + reason)

let private contains (term: string) (value: string) =
    value.Contains(term, StringComparison.OrdinalIgnoreCase)

let private projectStage revision origin scope roles capacity recovery nodes claimedTwice (stage: WorkStage) =
    let node = Map.tryFind (stage.ProjectId, stage.NodeId) nodes
    let path =
        node
        |> Option.map (fun n -> pathFor nodes n |> Result.defaultWith unknown)
        |> Option.defaultValue (unknown ("Missing Work unit " + stage.NodeId))
    let priority = evidenceText revision "Lead priority" stage.LeadPriority string
    let blocking = evidenceText revision "Blocking" stage.IsBlocking (fun b -> if b then "Blocking" else "Ordinary")
    let claimed = claimLine revision claimedTwice stage.State
    let queueStatus =
        match evidence revision roles with
        | Error reason -> Error("Role subscriptions: " + reason)
        | Ok subscriptions ->
            if subscriptions |> Map.exists (fun _ queues -> Set.contains stage.QueueId queues)
            then Ok()
            else Error "No verified role subscription for queue"
    let capacityStatus =
        match evidence revision capacity with
        | Error reason -> Error ("Global capacity: " + reason)
        | Ok c when c.Limit < 0 || c.Occupied < 0 || c.Occupied > c.Limit ->
            Error "Global capacity inconsistent"
        | Ok c when c.Occupied >= c.Limit -> Error "Global capacity exhausted"
        | Ok _ -> Ok()
    let recoveryStatus =
        match evidence revision recovery with
        | Ok RecoveryMode.Normal -> Ok()
        | Ok(RecoveryMode.Fenced reason) -> Error ("Recovery fence: " + reason)
        | Ok(RecoveryMode.Investigating reason) -> Error ("Recovery investigation: " + reason)
        | Error reason -> Error ("Recovery unknown: " + reason)
    let scopeStatus =
        match origin with
        | SnapshotOrigin.ExplicitFixture _ -> Error "Fixture has no live command authority"
        | SnapshotOrigin.AuthenticatedServer _ ->
            match evidence revision scope with
            | Ok scope when scope.Authenticated && scope.CanClaim -> Ok()
            | _ -> Error "Claim authorization is missing/stale/read-only"
    let gate =
        [ if stage.Generation <= 0 || stage.AvailableSequence < 0L then
              yield "Missing/invalid stage generation or availability sequence"
          match node with
          | None -> yield "Missing Work unit"
          | Some n when pathFor nodes n |> Result.isError -> yield "Graph ancestry UNKNOWN"
          | Some n when n.State <> NodeState.Open && n.State <> NodeState.Active ->
              yield "Work unit is not Open/Active"
          | Some n when (lifecycle n).StartsWith("UNKNOWN") ->
              yield "Work approval UNKNOWN"
          | _ -> ()
          match evidence revision stage.Eligible with
          | Ok true -> ()
          | Ok false -> yield "Graph eligibility is false"
          | Error reason -> yield "Graph eligibility UNKNOWN: " + reason
          match evidence revision stage.UnmetHardDependencies with
          | Ok [] -> ()
          | Ok pending -> yield "Unmet hard prerequisites: " + String.concat ", " pending
          | Error reason -> yield "Hard prerequisite evidence UNKNOWN: " + reason
          for check in [ queueStatus; capacityStatus; recoveryStatus; scopeStatus ] do
              match check with Error reason -> yield reason | Ok () -> ()
          match evidence revision stage.LeadPriority with
          | Error reason -> yield "Priority UNKNOWN: " + reason
          | Ok _ -> ()
          match evidence revision stage.IsBlocking with
          | Error reason -> yield "Blocking relevance UNKNOWN: " + reason
          | Ok _ -> () ]
    let readiness =
        match stage.State with
        | StageState.Pending -> "Pending (not yet published)"
        | StageState.Blocked reasons -> "Blocked: " + String.concat "; " reasons
        | StageState.Claimed _ -> claimed
        | StageState.Finished -> "Finished (review/integration evidence shown separately)"
        | StageState.Cancelled -> "Cancelled"
        | StageState.Available when gate.IsEmpty ->
            "Eligible in snapshot (read-only; no H0 claim endpoint)"
        | StageState.Available ->
            "NotReady: " + String.concat "; " gate
    { Key = rowKey stage.ProjectId stage.Id
      Path = path
      Queue = stage.QueueId
      Kind = stageKind stage.Kind
      LeadPriority = priority
      Blocking = blocking
      Readiness = readiness
      Claim = claimed
      Review = review stage.Review
      Integration = integration stage.Integration
      Details =
        [ sprintf "Work stage %s generation %d / availability sequence %d" stage.Id stage.Generation stage.AvailableSequence
          "Project: " + stage.ProjectId
          "Node: " + stage.NodeId
          evidenceText revision "Unmet hard prerequisites" stage.UnmetHardDependencies
              (fun deps -> if deps.IsEmpty then "No unmet hard prerequisites (at this revision)" else String.concat ", " deps)
          "Role compatibility is not operation authorization"
          "H0 has no live claim, approval, integration, or scheduler controls" ] }

let renderAt (now: DateTimeOffset) (query: Query) (snapshot: Snapshot) =
    if snapshot.Version <> ApiVersion then
        unavailable (sprintf "Unsupported Work API schema v%d (expected v%d)" snapshot.Version ApiVersion)
    elif snapshot.Revision <= 0L then unavailable "Missing authoritative snapshot revision"
    else
        let banner, allowed =
            match snapshot.Origin with
            | SnapshotOrigin.ExplicitFixture label when nonblank label ->
                Ok("FIXTURE PREVIEW - " + label + " (not live Work authority)"), None
            | SnapshotOrigin.ExplicitFixture _ ->
                Error "Unnamed fixture", None
            | SnapshotOrigin.AuthenticatedServer authority when not (nonblank authority) ->
                Error "Missing authenticated authority identity", None
            | SnapshotOrigin.AuthenticatedServer _ ->
                match evidence snapshot.Revision snapshot.Scope with
                | Ok scope when scope.Authenticated && not scope.AllowedProjectIds.IsEmpty ->
                    Ok(sprintf "Authenticated Work snapshot revision %d (read-only H0 projection)" snapshot.Revision),
                    Some scope.AllowedProjectIds
                | _ -> Error "Missing or stale authenticated project scope", None
        match banner with
        | Error reason -> unavailable reason
        | Ok banner ->
            let authorized =
                match query.ProjectId, allowed with
                | Some project, Some projects -> Set.contains project projects
                | _ -> true
            if not authorized then unavailable "Requested project is not in the authenticated scope"
            else
                let included (node: WorkNode) =
                    (query.ProjectId |> Option.forall ((=) node.ProjectId))
                    && (allowed |> Option.forall (fun projects -> Set.contains node.ProjectId projects))
                let selectedNodes = snapshot.Nodes |> List.filter included
                let duplicates =
                    selectedNodes
                    |> List.groupBy (fun node -> node.ProjectId, node.Id)
                    |> List.filter (fun (_, members) -> members.Length > 1)
                if selectedNodes |> List.exists (fun node -> not (nonblank node.ProjectId && nonblank node.Id)) then
                    unavailable "Missing canonical project/node identity in snapshot"
                elif not duplicates.IsEmpty then unavailable "Duplicate canonical Work node identities in snapshot"
                else
                    let nodes = selectedNodes |> List.map (fun node -> (node.ProjectId, node.Id), node) |> Map.ofList
                    let nodeRows =
                        selectedNodes
                        |> List.sortBy (fun node -> node.ProjectId, node.Id)
                        |> List.map (fun node ->
                            let path = pathFor nodes node
                            { Key = rowKey node.ProjectId node.Id
                              Path = path |> Result.defaultWith unknown
                              State = lifecycle node
                              Proposal = proposal node.Proposal
                              Acceptance = accepted node.Acceptance
                              RequiredForParent = node.RequiredForParent
                              Details =
                                [ sprintf "Node v%d / project %s" node.Version node.ProjectId
                                  "Required child: " + string node.RequiredForParent
                                  "Completion of required children does not automatically accept the parent" ]
                                @ (match path with Error reason -> [ unknown reason ] | Ok _ -> []) })
                    let roleFilter =
                        match query.RoleId with
                        | None -> Ok None
                        | Some role when not (nonblank role) -> Error "Blank role filter"
                        | Some role ->
                            match evidence snapshot.Revision snapshot.RoleQueues with
                            | Ok roles -> Ok(Some(roles |> Map.tryFind role |> Option.defaultValue Set.empty))
                            | Error reason -> Error ("Role filter cannot be verified: " + reason)
                    let projectStages =
                        snapshot.Stages
                        |> List.filter (fun stage -> Map.containsKey (stage.ProjectId, stage.NodeId) nodes)
                    let allStages =
                        projectStages
                        |> List.filter (fun stage -> query.QueueId |> Option.forall ((=) stage.QueueId))
                    let unresolvedFixtureLinks =
                        match snapshot.Origin, query.ProjectId with
                        | SnapshotOrigin.ExplicitFixture _, None ->
                            snapshot.Stages
                            |> List.filter (fun stage -> not (Map.containsKey (stage.ProjectId, stage.NodeId) nodes))
                            |> List.map (fun stage -> rowKey stage.ProjectId stage.Id)
                        | _ -> []
                    let malformedStageIds =
                        projectStages |> List.exists (fun stage -> not (nonblank stage.ProjectId && nonblank stage.Id && nonblank stage.NodeId))
                    let duplicateStageIds =
                        projectStages
                        |> List.groupBy (fun stage -> stage.ProjectId, stage.Id)
                        |> List.choose (fun ((projectId, id), members) ->
                            if members.Length > 1 then Some(rowKey projectId id) else None)
                    let claims =
                        projectStages
                        |> List.choose (fun stage ->
                            match stage.State with
                            | StageState.Claimed claim when nonblank claim.LaunchId -> Some claim.LaunchId
                            | _ -> None)
                        |> List.countBy id
                        |> List.choose (fun (id, count) -> if count > 1 then Some id else None)
                        |> Set.ofList
                    let stages =
                        match roleFilter with
                        | Error _ -> []
                        | Ok _ when malformedStageIds || not duplicateStageIds.IsEmpty -> []
                        | Ok queues ->
                            allStages
                            |> List.filter (fun stage ->
                                queues |> Option.forall (Set.contains stage.QueueId))
                            |> List.sortBy (fun stage ->
                                let ready = stage.State = StageState.Available
                                            && evidence snapshot.Revision stage.Eligible = Ok true
                                let blocker = evidence snapshot.Revision stage.IsBlocking = Ok true
                                let priority = evidence snapshot.Revision stage.LeadPriority |> Result.defaultValue Int32.MinValue
                                (if ready then 0 else 1), (if blocker then 0 else 1),
                                -int64 priority, stage.AvailableSequence, stage.ProjectId, stage.Id)
                            |> List.map (projectStage snapshot.Revision snapshot.Origin snapshot.Scope
                                            snapshot.RoleQueues snapshot.Capacity snapshot.Recovery nodes claims)
                            |> List.filter (fun row ->
                                not (nonblank query.Search)
                                || [ row.Key; row.Path; row.Readiness; row.Queue; row.Kind ]
                                   |> List.exists (contains query.Search))
                    let diagnostics =
                        [ match roleFilter with Error reason -> yield unknown reason | Ok _ -> ()
                          if not unresolvedFixtureLinks.IsEmpty then
                              yield unknown ("Unresolved Work stage node links in fixture: " + String.concat ", " unresolvedFixtureLinks)
                          if malformedStageIds then
                              yield unknown "Missing canonical project/stage identity in snapshot"
                          if not duplicateStageIds.IsEmpty then
                              yield unknown ("Duplicate Work stage identities: " + String.concat ", " duplicateStageIds)
                          if not claims.IsEmpty then
                              yield unknown "Multiple primary claims share the same launch ID"
                          for nodeRow in nodeRows do
                              if nodeRow.Path.StartsWith("UNKNOWN") then yield nodeRow.Path
                          for stage in stages do
                              if stage.Path.StartsWith("UNKNOWN") then yield stage.Path ]
                    { Banner = banner
                      Nodes = nodeRows
                      Stages = stages
                      Capacity = capacityLine snapshot.Revision snapshot.Capacity
                      Backup = backupLine now snapshot.Revision snapshot.Backup
                      Recovery = recoveryLine snapshot.Revision snapshot.Recovery
                      Diagnostics = diagnostics
                      HistoricalAgentAudit = "Existing historical Agent audit remains a separate read-only view"
                      ActionsEnabled = false }
