module internal Client.WorkStatefulTransport

open System
open Jarvis.WorkStatefulApi
open Client.WorkStatefulContracts

// A future authenticated host transport supplies this interface. A decoded
// payload or H0 fixture is not itself an authenticated channel. No network
// client, route, scheduler, or command writer is registered in H1a.
type IAuthenticatedWorkSnapshotReader =
    abstract ReadProjectSnapshot: WorkReadRequest -> Result<WorkReadSnapshot, string>

[<RequireQualifiedAccess>]
type ReadSourceProvenance =
    // SQLite provenance is reported, not Git/review/integration attestation.
    | ReportedUnverified of string
    | Unknown of string

type ReadProjection =
    { Snapshot: Snapshot
      StageSources: Map<string * string, ReadSourceProvenance>
      Diagnostics: string list }

[<RequireQualifiedAccess>]
type ReadTransportFailure =
    | Unavailable of string
    | InvalidResponse of string

let private nonblank value = not (String.IsNullOrWhiteSpace value)

let private validSourceCommit (sha: string) =
    not (String.IsNullOrWhiteSpace sha)
    && (sha.Length = 40 || sha.Length = 64)
    && (sha |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

let private nodeKind = function
    | Some "Project" -> Some NodeKind.Project
    | Some "System" -> Some NodeKind.System
    | Some "Feature" -> Some NodeKind.Feature
    | _ -> None

let private nodeState = function
    | Some "Draft" -> Some NodeState.Draft
    | Some "Proposed" -> Some NodeState.Proposed
    | Some "Open" -> Some NodeState.Open
    | Some "Active" -> Some NodeState.Active
    | Some "Completed" -> Some NodeState.Completed
    | Some "Closed" -> Some NodeState.Closed
    | Some "Cancelled" -> Some NodeState.Cancelled
    | _ -> None

let private proposalState (row: WorkReadNode) =
    match row.ProposalState, row.ApprovalReceipt with
    | Some "Draft", _ -> ProposalState.NotSubmitted
    | Some "Proposed", _ -> ProposalState.AwaitingLead
    // A bare receipt ID in a Work record is not a verified Lead decision.
    | Some "Open", _ -> ProposalState.Unknown "Open needs an independently verified Lead approval receipt"
    | _ -> ProposalState.Unknown "No independently proven proposal/approval receipt"

let private node graphVersion (row: WorkReadNode) =
    match nodeKind row.Kind, nodeState row.State, row.Title, row.RequiredForParent with
    | Some kind, Some state, Some title, Some required
        when nonblank row.ProjectId && nonblank row.Id && nonblank title
             && row.RowVersion > 0L && row.RowVersion <= int64 Int32.MaxValue
             && row.GraphVersion = graphVersion ->
        Some { Id = row.Id
               ProjectId = row.ProjectId
               ParentId = row.ParentId
               Kind = kind
               Title = title
               Version = int row.RowVersion
               State = state
               Proposal = proposalState row
               RequiredForParent = required
               // Source JSON may *refer* to a decision; it cannot itself
               // prove independently accepted parent/work outcome.
               Acceptance = AcceptanceState.Unknown "Independent acceptance not attested by this read projection" }
    | _ -> None

let private stageKind = function
    | Some "Planning" -> Some StageKind.Planning
    | Some "Approval" -> Some StageKind.Approval
    | Some "Implementation" -> Some StageKind.Implementation
    | Some "FrozenReview" -> Some StageKind.FrozenReview
    | Some "Integration" -> Some StageKind.Integration
    | Some "Recovery" -> Some StageKind.Recovery
    | Some "RoleMessage" -> Some StageKind.RoleMessage
    | _ -> None

let private stage graphVersion stopGeneration (row: WorkReadStage) =
    let compatible = row.StopGeneration = stopGeneration && row.GraphVersion = graphVersion
    match stageKind row.Kind, row.NodeId, row.QueueId with
    | Some kind, Some nodeId, Some queue
        when compatible && nonblank row.ProjectId && nonblank row.Id
             && nonblank nodeId && nonblank queue
             && row.RowVersion > 0L && row.StageGeneration > 0L
             && row.StageGeneration <= int64 Int32.MaxValue
             && (row.AvailableSequence |> Option.forall ((<=) 0L)) ->
        let state =
            match row.State with
            | Some "Pending" -> Some StageState.Pending
            | Some "Available" -> Some StageState.Available
            | Some "Blocked" -> Some(StageState.Blocked ["Blocked reasons not yet attested"])
            | Some "Claimed" when (row.ClaimLaunchId |> Option.exists nonblank)
                                  && row.ClaimGeneration > 0L
                                  && row.ClaimGeneration <= int64 Int32.MaxValue ->
                Some(StageState.Claimed
                    { LaunchId = row.ClaimLaunchId.Value
                      Generation = int row.ClaimGeneration
                      Execution = Evidence.Unknown "No verified live execution observation" })
            | Some "Completed" | Some "Finished" -> Some StageState.Finished
            | Some "Cancelled" -> Some StageState.Cancelled
            | _ -> None
        state |> Option.map (fun state ->
            { Id = row.Id
              ProjectId = row.ProjectId
              NodeId = nodeId
              QueueId = queue
              Kind = kind
              Generation = int row.StageGeneration
              State = state
              LeadPriority = Evidence.Unknown "No verified Lead priority decision projected"
              IsBlocking = Evidence.Unknown "No authoritative blocker derivation projected"
              Eligible = Evidence.Unknown "E1 scheduler/graph/STOP authorization not attested"
              AvailableSequence = row.AvailableSequence |> Option.defaultValue 0L
              UnmetHardDependencies = Evidence.Unknown "No canonical graph dependency witness"
              Review = ReviewStage.Unknown "Review SHA is not a verified verdict"
              Integration = IntegrationStage.Unknown "Integration SHA is not a verified outcome" })
    | _ -> None

let private validate requestedProject requestedRole authority (snapshot: WorkReadSnapshot) =
    if snapshot.ApiVersion <> WorkReadVersion.Current then
        Error "Unsupported server snapshot schema"
    elif snapshot.ProjectId <> requestedProject || snapshot.AuthorityId <> authority then
        Error "Server authority or canonical project mismatch"
    elif snapshot.Revision <= 0L || snapshot.ProjectRowVersion <= 0L then
        Error "Missing committed Work revision/authority row"
    elif snapshot.VerifiedRoleId <> requestedRole then
        Error "Verified role differs from expected channel authority"
    elif snapshot.Nodes |> List.exists (fun r -> r.ProjectId <> requestedProject) ||
         snapshot.Stages |> List.exists (fun r -> r.ProjectId <> requestedProject) ||
         snapshot.Roles |> List.exists (fun r -> r.ProjectId <> requestedProject) then
        Error "Cross-project record in authenticated response"
    elif (snapshot.Nodes |> List.map (fun r -> r.Id) |> List.distinct |> List.length) <> snapshot.Nodes.Length ||
         (snapshot.Stages |> List.map (fun r -> r.Id) |> List.distinct |> List.length) <> snapshot.Stages.Length then
        Error "Duplicate canonical project-local record IDs"
    elif snapshot.Capacity |> Option.exists (fun c ->
        c.Limit < 0 || c.Occupied < 0 || c.Occupied > c.Limit || c.RowVersion <= 0L) then
        Error "Invalid global capacity witness"
    else Ok ()

/// Never accept user-provided raw bytes or an explicitly simulated H0 fixture
/// as authenticated authority. The caller must supply a channel authenticated
/// by the future host, whose identity is bound separately to this request.
let fetch (reader: IAuthenticatedWorkSnapshotReader)
          (expectedAuthorityId: string)
          (projectId: string)
          (verifiedRoleId: string option)
          : Result<ReadProjection, ReadTransportFailure> =
    if isNull (box reader) || not (nonblank expectedAuthorityId) ||
       not (nonblank projectId) then
        Error(ReadTransportFailure.InvalidResponse "Missing authenticated transport or project binding")
    else
        let request =
            { ApiVersion = WorkReadVersion.Current
              ProjectId = projectId
              RoleId = verifiedRoleId }
        match reader.ReadProjectSnapshot request with
        | Error why -> Error(ReadTransportFailure.Unavailable why)
        | Ok response ->
            match validate projectId verifiedRoleId expectedAuthorityId response with
            | Error why -> Error(ReadTransportFailure.InvalidResponse why)
            | Ok () ->
                let nodes = response.Nodes |> List.choose (node response.GraphVersion)
                let stages = response.Stages |> List.choose (stage response.GraphVersion response.StopGeneration)
                let sourceRows =
                    response.Stages
                    |> List.map (fun row -> (row.ProjectId, row.Id), row)
                    |> Map.ofList
                let stageSources =
                    stages
                    |> List.map (fun projected ->
                        let row = Map.find (projected.ProjectId, projected.Id) sourceRows
                        let source =
                            match row.SourceCommit with
                            | Some sha when validSourceCommit sha -> ReadSourceProvenance.ReportedUnverified sha
                            | Some _ -> ReadSourceProvenance.Unknown "Malformed recorded source SHA"
                            | None -> ReadSourceProvenance.Unknown "No recorded source SHA"
                        (projected.ProjectId, projected.Id), source)
                    |> Map.ofList
                let roles =
                    match verifiedRoleId with
                    | None -> Evidence.Unknown "No verified role selected by the authenticated channel"
                    | Some role ->
                        match response.Roles |> List.tryFind (fun row -> row.Id = role) with
                        | Some row when row.RoleVersion > 0L ->
                            match row.Queues with
                            | Some queues when queues |> List.forall nonblank ->
                                Evidence.Verified(response.Revision, Map.ofList [ role, Set.ofList queues ])
                            | _ -> Evidence.Unknown "Role subscription or active status unverified"
                        | _ -> Evidence.Unknown "No verified role subscription"
                let capacity =
                    match response.Capacity with
                    | Some c ->
                        Evidence.Verified(response.Revision,
                            { Limit = c.Limit; Occupied = c.Occupied })
                    | None -> Evidence.Unknown "No account-global capacity row in this read transaction"
                let diagnostics =
                    response.Diagnostics @
                    [ if nodes.Length <> response.Nodes.Length then
                          "Work graph rows omitted: node kind/state/required status is not verified."
                      if stages.Length <> response.Stages.Length then
                          "Stage rows omitted: canonical node link/state/STOP or generation is not verified."
                      if response.Stages |> List.exists (fun row ->
                          row.State = Some "Claimed"
                          && (not (row.ClaimLaunchId |> Option.exists nonblank)
                              || row.ClaimGeneration <= 0L
                              || row.ClaimGeneration > int64 Int32.MaxValue)) then
                          "Claimed stage omitted: missing or invalid claim launch/generation provenance."
                      if stageSources |> Map.exists (fun _ source ->
                          match source with
                          | ReadSourceProvenance.Unknown "Malformed recorded source SHA" -> true
                          | _ -> false) then
                          "Recorded source SHA malformed; no source provenance attested."
                      "This read-only snapshot does not grant approval, claim, or priority permissions." ]
                Ok
                    { Snapshot =
                        { Version = ApiVersion
                          Revision = response.Revision
                          CapturedAt = response.CapturedAt
                          Origin = SnapshotOrigin.AuthenticatedServer response.AuthorityId
                          Scope = Evidence.Verified(response.Revision,
                              { AllowedProjectIds = Set.singleton projectId
                                Authenticated = true
                                CanClaim = false })
                          RoleQueues = roles
                          Capacity = capacity
                          Backup = Evidence.Unknown response.BackupUnknownReason
                          Recovery = Evidence.Unknown response.RecoveryUnknownReason
                          Nodes = nodes
                          Stages = stages }
                      StageSources = stageSources
                      Diagnostics = diagnostics }
