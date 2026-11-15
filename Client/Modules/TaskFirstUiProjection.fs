module Client.TaskFirstUiProjection

open System
open Jarvis.WorkQueues

// WQ09 groundwork only. Pure projection of caller-verified WQ01/02 records.
// Deliberately not wired to ConsoleTui, command handlers, scheduler, or posting.
[<RequireQualifiedAccess>]
type Evidence<'a> =
    | Known of 'a
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type RowState =
    | Next
    | Active of launchId: string
    | StaleHeartbeat of launchId: string
    | StopRecovery of launchId: string
    | Blocked
    | RecoveryHold of reason: string
    | Uncertain of reason: string

type TaskRow =
    { WorkId: string
      ProjectId: string
      SubjectId: string
      SubjectTitle: string
      Kind: string
      StageGeneration: int
      State: RowState
      HardBlockers: string list
      AvailableAt: int64
      AvailableSequence: int64 }

type CycleRow =
    { Members: string list
      ExactStagePath: string list
      WitnessEdges: (string * string) list }

type MessageRow =
    { MessageId: string
      Body: string
      State: string
      ClaimLaunchId: string option
      HandlerGeneration: int
      WorkId: string option }

type ThreadRow =
    { Subject: Subject
      SubjectTitle: string
      MessagesByCanonicalId: MessageRow list }

type LaunchRow =
    { LaunchId: string
      RoleId: string
      Phase: string
      AgentUuid: string option
      RecoveryWarning: string option }

type CapacityRow =
    { Limit: int
      Occupied: int
      Free: int
      Overcommitted: bool
      Migration: MigrationEpoch }

type QueueHealth =
    { ProjectId: string
      ProjectVersion: int
      GraphVersion: int
      Unfinished: int
      Runnable: int
      Blocked: int
      Cycles: int
      ReadOnly: bool }

type Dashboard =
    { Next: Evidence<TaskRow list>
      ActiveAndStalled: Evidence<TaskRow list>
      Blocked: Evidence<TaskRow list>
      Cycles: Evidence<CycleRow list>
      Threads: Evidence<ThreadRow list>
      Launches: Evidence<LaunchRow list>
      Capacity: Evidence<CapacityRow>
      QueueHealth: Evidence<QueueHealth> }

type ComposerPreview =
    { Subject: Subject
      SubjectTitle: string
      DraftBody: string
      CanPost: bool
      DisabledReason: string }

let private nonBlank (s: string) = not (String.IsNullOrWhiteSpace s)

let private title titles subjectId =
    match Map.tryFind subjectId titles with
    | Some candidate when nonBlank candidate -> candidate
    | _ -> subjectId

let private workKind = function
    | WorkKind.Planning -> "Planning"
    | WorkKind.Implementation -> "Implementation"
    | WorkKind.FrozenReview -> "Frozen review"
    | WorkKind.Integration -> "Integration"
    | WorkKind.RoleMessage _ -> "Role message"

let private blocksOther (project: Project) (stage: WorkStage) =
    project.Works
    |> Map.exists (fun _ candidate ->
        Contracts.isUnfinished candidate
        && candidate.Dependencies.Contains stage.Id
        && not (candidate.WaivedDependencies.ContainsKey stage.Id)
        && not (Contracts.isSatisfied project stage.Id))

let private fifoKey project (stage: WorkStage) =
    (if blocksOther project stage then 0 else 1),
    stage.AvailableAt, stage.ProjectId, stage.AvailableSequence, stage.Id

let private unmet (project: Project) (stage: WorkStage) =
    stage.Dependencies
    |> Set.filter (fun dependency ->
        not (stage.WaivedDependencies.ContainsKey dependency)
        && not (Contracts.isSatisfied project dependency))
    |> Set.toList

let private row titles project (stage: WorkStage) state =
    { WorkId = stage.Id
      ProjectId = stage.ProjectId
      SubjectId = stage.SubjectId
      SubjectTitle = title titles stage.SubjectId
      Kind = workKind stage.Kind
      StageGeneration = stage.StageGeneration
      State = state
      HardBlockers = unmet project stage
      AvailableAt = stage.AvailableAt
      AvailableSequence = stage.AvailableSequence }

let private cyclePath (warning: CycleWarning) =
    // A real ordered closed walk through canonical hard edges, not a sorted
    // list of SCC members presented misleadingly as a path.
    match warning.Members with
    | [] -> None
    | root :: _ ->
        let rec walk visited current =
            warning.WitnessEdges
            |> List.choose (fun (source, target) ->
                if source = current then Some target else None)
            |> List.tryPick (fun next ->
                if next = root then Some [ current; root ]
                elif Set.contains next visited then None
                else walk (Set.add next visited) next |> Option.map (fun path -> current :: path))
        walk (Set.singleton root) root

let private messageRow (message: SubjectMessage) =
    let phase, claimed =
        match message.State with
        | MessageState.Pending -> "Pending", None
        | MessageState.ClaimedPrimary launch -> "Claimed (primary)", Some launch
        | MessageState.ClaimedAncillary launch -> "Claimed (ancillary)", Some launch
        | MessageState.Handled reply -> "Handled (reply " + reply + ")", None
    { MessageId = message.Id
      Body = message.Body
      State = phase
      ClaimLaunchId = claimed
      HandlerGeneration = message.HandlerGeneration
      WorkId = message.WorkId }

let private threadRows titles (project: Project) =
    let roleIds =
        [ yield "Lead"
          for KeyValue(role, _) in project.Subscriptions do yield role
          for KeyValue(_, message) in project.Messages do
              match message.Subject with
              | Subject.Role role -> yield role
              | _ -> () ]
        |> Set.ofList
    let taskIds =
        [ for KeyValue(_, stage) in project.Works do
              match stage.Kind with
              | WorkKind.RoleMessage _ -> ()
              | _ -> yield stage.SubjectId
          for KeyValue(_, message) in project.Messages do
              match message.Subject with
              | Subject.Task task -> yield task
              | _ -> () ]
        |> Set.ofList
    let subjects =
        [ for task in taskIds -> Subject.Task task
          for role in roleIds -> Subject.Role role ]
    subjects
    |> List.map (fun subject ->
        let label =
            match subject with
            | Subject.Task id -> "Task: " + title titles id
            | Subject.Role id -> "Role: " + id
        let messages =
            project.Messages
            |> Map.toList
            |> List.map snd
            |> List.filter (fun message -> message.Subject = subject)
            |> List.sortBy (fun message -> message.Id)
            |> List.map messageRow
        { Subject = subject; SubjectTitle = label; MessagesByCanonicalId = messages })

let private launchPhase now staleAfter (launch: Launch) =
    let registeredDetails uuid =
        let warning =
            match launch.Lease, now with
            | None, _ -> Some "Registered lease missing; recovery status unknown"
            | Some lease, _ when lease.StopRequested -> Some "STOP requested; terminal recovery unproven"
            | Some lease, Some current when staleAfter > 0L && current >= lease.LastSeenAt
                                               && current - lease.LastSeenAt >= staleAfter ->
                Some "Heartbeat stale; recovery unproven"
            | Some lease, Some current when current < lease.LastSeenAt ->
                Some "Clock precedes last heartbeat; liveness unknown"
            | _ -> None
        "Registered", (if nonBlank uuid then Some uuid else None),
        (if not (nonBlank uuid) then Some "Registered UUID missing" else warning)
    match launch.State with
    | LaunchState.ReservedUnregistered -> "Reserved; unregistered", None, None
    | LaunchState.SubmittedUnregistered DispatchState.NotSubmitted ->
        "Unregistered; not submitted", None, None
    | LaunchState.SubmittedUnregistered DispatchState.Submitted ->
        "Submitted; registration pending", None, None
    | LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown ->
        "Dispatch outcome unknown; registration unproven", None,
        Some "Uncertain browser dispatch receipt"
    | LaunchState.Registered (uuid, _) -> registeredDetails uuid
    | LaunchState.DemandInvalidatedPendingTerminal previous ->
        let uuid =
            match previous with
            | LaunchState.Registered (id, _) when nonBlank id -> Some id
            | _ -> None
        "Demand invalidated; terminal proof pending", uuid,
        Some "Previous launch state retained; no terminal/release success"
    | LaunchState.TerminalPendingRelease _ ->
        "Terminal; global release pending", None, Some "Capacity still reserved until release proof"

let private launchRows now staleAfter (project: Project) =
    project.Launches
    |> Map.toList
    |> List.map (fun (_, launch) ->
        let phase, uuid, warning = launchPhase now staleAfter launch
        { LaunchId = launch.Id
          RoleId = launch.RoleId
          Phase = phase
          AgentUuid = uuid
          RecoveryWarning = warning })

let private claimedState now staleAfter (project: Project) launchId =
    match project.Launches.TryFind launchId with
    | None -> RowState.Uncertain "Claimed launch absent from verified snapshot"
    | Some launch ->
        match launch.State with
        | LaunchState.Registered (uuid, _) when nonBlank uuid ->
            match launch.Lease, now with
            | None, _ -> RowState.Uncertain "Registered lease absent"
            | Some lease, _ when lease.StopRequested -> RowState.StopRecovery launchId
            | Some lease, Some current when staleAfter > 0L && current >= lease.LastSeenAt
                                               && current - lease.LastSeenAt >= staleAfter ->
                RowState.StaleHeartbeat launchId
            | Some lease, Some current when current < lease.LastSeenAt ->
                RowState.Uncertain "Clock predates verified heartbeat"
            | _ -> RowState.Active launchId
        | _ -> RowState.Uncertain "Claimed launch not verified registered"

let private blockedState (stage: WorkStage) =
    match stage.Gate with
    | GateState.IntegrationGateHold _ -> RowState.RecoveryHold "IntegrationGateHold; original push must be reconciled"
    | GateState.ExternalEffectObservedPendingGate _ ->
        RowState.RecoveryHold "External effect observed; dependency gate pending"
    | GateState.SourceRecoveryBlocked reason ->
        RowState.RecoveryHold ("SourceRecoveryBlocked: " + reason)
    | GateState.DependencyGatePending _ -> RowState.Blocked
    | GateState.Clear ->
        match stage.State with
        | WorkState.GateException -> RowState.RecoveryHold "GateException; no implied authorization"
        | _ -> RowState.Blocked

let private capacityView (account: Result<Account, string>) =
    match account with
    | Error reason -> Evidence.Unknown ("Account capacity unverified: " + reason)
    | Ok state when state.MaxConcurrentAgents < 0 ->
        Evidence.Unknown "Negative account-global capacity limit"
    | Ok state ->
        let occupied = state.Tokens |> Map.filter (fun _ released -> not released) |> Map.count
        Evidence.Known
            { Limit = state.MaxConcurrentAgents
              Occupied = occupied
              Free = max 0 (state.MaxConcurrentAgents - occupied)
              Overcommitted = occupied > state.MaxConcurrentAgents
              Migration = state.Migration }

let private unknownDashboard reason capacity =
    { Next = Evidence.Unknown reason
      ActiveAndStalled = Evidence.Unknown reason
      Blocked = Evidence.Unknown reason
      Cycles = Evidence.Unknown reason
      Threads = Evidence.Unknown reason
      Launches = Evidence.Unknown reason
      Capacity = capacity
      QueueHealth = Evidence.Unknown reason }

/// Read-only snapshot projection. An Ok(Project) is a caller-provided snapshot,
/// never an authority grant. All queue runnable views require WQ01/02 coverage.
let projectDashboard projectId titles (projectResult: Result<Project, string>)
    (account: Result<Account, string>) now staleAfter : Dashboard =
    let capacity = capacityView account
    match projectResult with
    | Error reason -> unknownDashboard ("Project snapshot unverified: " + reason) capacity
    | Ok project ->
        let invalidIdentity =
            project.Works
            |> Map.exists (fun key stage ->
                not (nonBlank key) || not (nonBlank stage.Id)
                || key <> stage.Id || stage.ProjectId <> projectId)
            || (project.Launches |> Map.exists (fun key launch ->
                not (nonBlank key) || key <> launch.Id || launch.ProjectId <> projectId))
            || not (nonBlank projectId)
        let threads =
            if project.MessageCovered && not invalidIdentity then Evidence.Known(threadRows titles project)
            else Evidence.Unknown "Message index/identity incomplete; not an empty conversation"
        let launches =
            if project.QueueCovered && not invalidIdentity then
                Evidence.Known(launchRows now staleAfter project)
            else Evidence.Unknown "Launch/queue index or identity incomplete"
        let graph =
            WorkQueueDependencyDiagnostics.analyze project.GraphVersion project.GraphCovered project.Works
        let cycleEvidence =
            match graph with
            | Error reason -> Evidence.Unknown reason
            | Ok diagnostics ->
                let cycleRows =
                    diagnostics.CycleWarnings
                    |> List.map (fun warning ->
                        cyclePath warning
                        |> Option.map (fun path ->
                            { Members = warning.Members
                              ExactStagePath = path
                              WitnessEdges = warning.WitnessEdges }))
                if List.exists Option.isNone cycleRows then
                    Evidence.Unknown "Verified SCC has no reconstructible hard-edge cycle path"
                else Evidence.Known(List.choose id cycleRows)
        let queueValidation =
            if invalidIdentity then Error "Project/stage/launch identity mismatch"
            else
                match graph, Contracts.orderAvailable project with
                | Error error, _ -> Error error
                | _, Error error -> Error(sprintf "Work queue coverage/reconciliation unknown: %A" error)
                | Ok diagnostics, Ok available ->
                    match cycleEvidence with
                    | Evidence.Unknown reason -> Error reason
                    | Evidence.Known cycles -> Ok(diagnostics, available, cycles)
        match queueValidation with
        | Error reason ->
            { Next = Evidence.Unknown reason
              ActiveAndStalled = Evidence.Unknown reason
              Blocked = Evidence.Unknown reason
              Cycles = cycleEvidence
              Threads = threads
              Launches = launches
              Capacity = capacity
              QueueHealth = Evidence.Unknown reason }
        | Ok (_, available, cycles) ->
            let cyclic = cycles |> List.collect (fun cycle -> cycle.Members) |> Set.ofList
            // Even an available stage with a waiver remains visibly held if
            // the exact hard graph contains a cycle. Preview never claims it.
            let next = available |> List.filter (fun stage -> not (cyclic.Contains stage.Id))
            let nextIds = next |> List.map (fun stage -> stage.Id) |> Set.ofList
            let stages = project.Works |> Map.toList |> List.map snd
            let active =
                stages
                |> List.choose (fun stage ->
                    match stage.State with
                    | WorkState.Claimed launch ->
                        let state =
                            match stage.Gate with
                            | GateState.SourceRecoveryBlocked reason ->
                                RowState.RecoveryHold ("SourceRecoveryBlocked: " + reason)
                            | GateState.IntegrationGateHold _ ->
                                RowState.RecoveryHold "IntegrationGateHold; original push uncertain"
                            | GateState.ExternalEffectObservedPendingGate _ ->
                                RowState.RecoveryHold "External push observed; gate unresolved"
                            | _ -> claimedState now staleAfter project launch
                        Some(row titles project stage state)
                    | _ -> None)
                |> List.sortBy (fun row -> fifoKey project project.Works.[row.WorkId])
            let blocked =
                stages
                |> List.filter (fun stage ->
                    Contracts.isUnfinished stage
                    && not (nextIds.Contains stage.Id)
                    && (match stage.State with
                        | WorkState.Available | WorkState.Blocked | WorkState.GateException -> true
                        | _ -> false))
                |> List.sortBy (fifoKey project)
                |> List.map (fun stage -> row titles project stage (blockedState stage))
            { Next = Evidence.Known(next |> List.map (fun stage -> row titles project stage RowState.Next))
              ActiveAndStalled = Evidence.Known active
              Blocked = Evidence.Known blocked
              Cycles = Evidence.Known cycles
              Threads = threads
              Launches = launches
              Capacity = capacity
              QueueHealth =
                Evidence.Known
                    { ProjectId = projectId
                      ProjectVersion = project.Version
                      GraphVersion = project.GraphVersion
                      Unfinished = stages |> List.filter Contracts.isUnfinished |> List.length
                      Runnable = next.Length
                      Blocked = blocked.Length
                      Cycles = cycles.Length
                      ReadOnly = true } }

/// No post command, UUID allocation or task/role mutation exists in this module.
let previewComposer subject titles body =
    let subjectTitle =
        match subject with
        | Subject.Task id -> "Task: " + title titles id
        | Subject.Role id -> "Role: " + id
    { Subject = subject
      SubjectTitle = subjectTitle
      DraftBody = body
      CanPost = false
      DisabledReason = "Read-only WQ09 groundwork; Task/Role transport and dependency review not integrated" }

let private statusText = function
    | RowState.Next -> "NEXT (preview only)"
    | RowState.Active launch -> "ACTIVE claimed by " + launch
    | RowState.StaleHeartbeat launch -> "STALE HEARTBEAT; recovery unproven: " + launch
    | RowState.StopRecovery launch -> "STOP requested; recovery unproven: " + launch
    | RowState.Blocked -> "BLOCKED"
    | RowState.RecoveryHold reason -> "HELD: " + reason
    | RowState.Uncertain reason -> "UNKNOWN: " + reason

let private section heading render evidence =
    match evidence with
    | Evidence.Unknown reason -> [ heading; "  UNKNOWN - " + reason ]
    | Evidence.Known items ->
        [ heading ] @
        (match render items with
         | [] -> [ "  (none in verified snapshot)" ]
         | lines -> lines)

/// Terminal-ready lines, with no interactive controls or live route activation.
let renderPreview (view: Dashboard) =
    let taskLine (task: TaskRow) =
        let blockers =
            if List.isEmpty task.HardBlockers then ""
            else " | prerequisites: " + String.concat ", " task.HardBlockers
        sprintf "  %s [%s] %s (%s/g%d) - %s%s"
            task.SubjectTitle task.ProjectId task.WorkId task.Kind task.StageGeneration
            (statusText task.State) blockers
    let tasks heading evidence = section heading (List.map taskLine) evidence
    let cycles =
        section "Hard dependency cycles" (List.map (fun cycle ->
            "  " + String.concat " -> " cycle.ExactStagePath)) view.Cycles
    let threads =
        section "Task and Role conversations (canonical ID order, not chronology)" (fun items ->
            items
            |> List.collect (fun (thread: ThreadRow) ->
                ("  " + thread.SubjectTitle) ::
                (if List.isEmpty thread.MessagesByCanonicalId then
                    [ "    (no messages in verified snapshot)" ]
                 else thread.MessagesByCanonicalId |> List.map (fun message ->
                    sprintf "    %s [%s] %s" message.MessageId message.State message.Body)))) view.Threads
    let launches =
        section "Launch and recovery state" (List.map (fun launch ->
            let uuid = launch.AgentUuid |> Option.map (fun id -> " | UUID: " + id) |> Option.defaultValue ""
            let warning = launch.RecoveryWarning |> Option.map (fun reason -> " | " + reason) |> Option.defaultValue ""
            sprintf "  %s Role(%s): %s%s%s" launch.LaunchId launch.RoleId launch.Phase uuid warning)) view.Launches
    let capacity =
        match view.Capacity with
        | Evidence.Unknown reason -> [ "Account-global capacity"; "  UNKNOWN - " + reason ]
        | Evidence.Known c ->
            [ "Account-global capacity"
              sprintf "  %d/%d occupied; %d free%s (migration: %A)"
                  c.Occupied c.Limit c.Free (if c.Overcommitted then " | OVERCOMMITTED" else "") c.Migration ]
    let health =
        match view.QueueHealth with
        | Evidence.Unknown reason -> [ "Queue health"; "  UNKNOWN - " + reason ]
        | Evidence.Known q ->
            [ "Queue health"
              sprintf "  %s v%d graph v%d: %d unfinished, %d projected next, %d blocked, %d cycles"
                  q.ProjectId q.ProjectVersion q.GraphVersion q.Unfinished q.Runnable q.Blocked q.Cycles ]
    [ "WQ09 TASK-FIRST PREVIEW - READ ONLY; NO CLAIM, POST, DISPATCH OR ACTIVATION" ]
    @ health @ tasks "Next (blocking-first FIFO)" view.Next
    @ tasks "Active and stalled" view.ActiveAndStalled
    @ tasks "Blocked / recovery holds" view.Blocked
    @ cycles @ threads @ launches @ capacity
