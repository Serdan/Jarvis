namespace Jarvis.WorkQueues

open System
open System.Collections.Generic

// Phase A contracts only. No source, browser, account, or production ledger I/O.
[<RequireQualifiedAccess>]
type WorkKind =
    | Planning
    | Implementation
    | FrozenReview
    | Integration
    | RoleMessage of messageId: string

[<RequireQualifiedAccess>]
type WorkState =
    | Available
    | Claimed of launchId: string
    | Blocked
    | GateException
    | Completed
    | Cancelled

[<RequireQualifiedAccess>]
type DomainState =
    | Undecided
    | FinalizedPendingTerminal of receipt: string
    | Integrated of receipt: string

[<RequireQualifiedAccess>]
type GateState =
    | Clear
    | DependencyGatePending of graphVersion: int
    | IntegrationGateHold of graphVersion: int * pushOperationId: string
    | ExternalEffectObservedPendingGate of graphVersion: int * pushOperationId: string * remoteCommit: string

[<RequireQualifiedAccess>]
type PushOutcome =
    | Unknown
    | DefinitiveNoEffect
    | Observed of remoteCommit: string

type PushJournal =
    { OperationId: string
      AcceptedSourceSha: string
      TargetRef: string
      Outcome: PushOutcome }

type VerifiedRemoteEffect =
    { OriginalPushOperationId: string
      AcceptedSourceSha: string
      TargetRef: string
      RemoteCommitSha: string
      AncestryVerified: bool
      JobsReconciled: bool }

type DefinitiveNoEffectProof =
    { OriginalPushOperationId: string
      DispatchedOperationTerminal: bool
      JobsReconciled: bool }

// An already verified remote observation, supplied to the pure contract boundary.
// Production must verify the repository, ref, SHA, journal and job proofs canonically.
type RemoteSourceSnapshot =
    { RepositoryId: string
      TaskId: string
      LineageId: string
      StageId: string
      StageGeneration: int
      ClaimGeneration: int
      LineageGeneration: int
      ExpectedRef: string
      ExpectedSha: string
      ContinuationRef: string
      ContinuationSha: string
      ReachabilityVerified: bool
      JobsReconciled: bool }

[<RequireQualifiedAccess>]
type SourceHandoffProof =
    | ReadOnly of verifiedReceipt: string
    | RemoteSnapshot of RemoteSourceSnapshot
    | Unsafe of reason: string

// The receipt is an assertion of a canonical, versioned account authorization,
// NOT a bare string or a way for an agent to grant itself waiver authority.
type VerifiedWaiverReceipt =
    { ReceiptId: string
      WorkId: string
      Dependencies: Set<string>
      AccountApprovalVersion: int
      ApprovedByRole: string
      CanonicallyVerified: bool }

type InvalidatedTerminalEvidence =
    { DispatchJournalSettled: bool
      SessionTerminalVerified: bool }

type SourceLineage =
    { RepositoryId: string
      TaskId: string
      TargetRef: string
      LineageId: string
      PinnedBaseCommit: string
      ReachableRef: string option
      ContinuationSha: string option
      Generation: int }

type WorkStage =
    { Id: string
      ProjectId: string
      SubjectId: string
      Kind: WorkKind
      QueueId: string
      StageGeneration: int
      State: WorkState
      Domain: DomainState
      Gate: GateState
      ClaimGeneration: int
      Dependencies: Set<string>
      WaivedDependencies: Map<string, string>
      AvailableAt: int64
      AvailableSequence: int64
      SourceLineage: SourceLineage option
      Push: PushJournal option
      IntegrationAuthorized: bool }

[<RequireQualifiedAccess>]
type Subject = Task of taskId: string | Role of roleId: string

[<RequireQualifiedAccess>]
type MessageState =
    | Pending
    | ClaimedPrimary of launchId: string
    | ClaimedAncillary of launchId: string
    | Handled of replyId: string

type SubjectMessage =
    { Id: string
      Subject: Subject
      Body: string
      State: MessageState
      HandlerGeneration: int
      WorkId: string option }

[<RequireQualifiedAccess>]
type DispatchState = NotSubmitted | Submitted | OutcomeUnknown

[<RequireQualifiedAccess>]
type LaunchState =
    | ReservedUnregistered
    | SubmittedUnregistered of DispatchState
    | Registered of agentUuid: string * sessionKey: string
    // Once eligibility is lost, no first connect, dispatch, heartbeat or claim is legal.
    // Preserve the pre-fence phase for phase-specific terminal reconciliation.
    | DemandInvalidatedPendingTerminal of previous: LaunchState
    | TerminalPendingRelease of projectReceipt: string

type RegisteredLease =
    { LastSeenAt: int64
      Generation: int
      StopRequested: bool }

type Launch =
    { Id: string
      ProjectId: string
      RoleId: string
      RoleVersion: int
      AccountTokenId: string
      State: LaunchState
      Lease: RegisteredLease option
      Version: int
      Credit: string option
      PrimaryWorkId: string option
      EverClaimed: bool
      AncillaryMessages: Set<string>
      DispatchCount: int }

[<RequireQualifiedAccess>]
type MigrationEpoch = Frozen | Preparing | Validated | Activated

// Semantic equality of complete typed command input; the future Git-ledger
// writer must encode these cases with a versioned canonical binary/JSON format.
[<RequireQualifiedAccess>]
type ProjectOperationPayload =
    | PublishStage of WorkStage
    | PostMessage of SubjectMessage * WorkStage option
    | AdmitProject of Launch
    | OtherMutation of string

type Project =
    { Version: int
      GraphVersion: int
      QueueCovered: bool
      GraphCovered: bool
      MessageCovered: bool
      SourceCovered: bool
      Subscriptions: Map<string, Set<string>>
      Works: Map<string, WorkStage>
      Launches: Map<string, Launch>
      Messages: Map<string, SubjectMessage>
      Receipts: Map<string, ProjectOperationPayload>
      NextAvailability: int64
      ProducerCount: int
      Migration: MigrationEpoch }

type Account =
    { Version: int
      MaxConcurrentAgents: int
      Tokens: Map<string, bool>
      Receipts: Map<string, string>
      Migration: MigrationEpoch }

[<RequireQualifiedAccess>]
type Rejection =
    | CasConflict
    | OperationIdReused
    | IncompleteCoverage of string
    | NoCompatibleMatching
    | NoCapacity
    | InvalidState of string
    | AlreadyRegistered
    | Terminal
    | DomainAlreadyFinalized
    | StageAlreadyTerminal
    | DependencyGatePending
    | UnsafeExternalEffect
    | MissingProof
    | NoEligibleWork

type CycleWarning = { Members: string list; WitnessEdges: (string * string) list }

module Contracts =
    let emptyProject subscriptions =
        { Version = 0
          GraphVersion = 0
          QueueCovered = true
          GraphCovered = true
          MessageCovered = true
          SourceCovered = true
          Subscriptions = subscriptions
          Works = Map.empty
          Launches = Map.empty
          Messages = Map.empty
          Receipts = Map.empty
          NextAvailability = 0L
          ProducerCount = 0
          Migration = MigrationEpoch.Activated }

    let emptyAccount limit =
        { Version = 0
          MaxConcurrentAgents = limit
          Tokens = Map.empty
          Receipts = Map.empty
          Migration = MigrationEpoch.Activated }

    // A two-client fixture reads a versioned immutable Git head and attempts one CAS.
    // Lost responses are recovered by the original operation/payload receipt.
    let private projectCasTyped expectedVersion operationId (payload: ProjectOperationPayload) change (project: Project) =
        match project.Receipts.TryFind operationId with
        | Some original when original = payload -> Ok project
        | Some _ -> Error Rejection.OperationIdReused
        | None when project.Version <> expectedVersion -> Error Rejection.CasConflict
        | None ->
            change project
            |> Result.map (fun changed ->
                { changed with
                    Version = project.Version + 1
                    Receipts = changed.Receipts.Add(operationId, payload) })

    let private projectCas expectedVersion operationId payload change project =
        projectCasTyped expectedVersion operationId
            (ProjectOperationPayload.OtherMutation payload) change project

    let private publishCas expectedVersion operationId stage change project =
        projectCasTyped expectedVersion operationId
            (ProjectOperationPayload.PublishStage stage) change project

    let private messageCas expectedVersion operationId message roleWork change project =
        projectCasTyped expectedVersion operationId
            (ProjectOperationPayload.PostMessage(message, roleWork)) change project

    let private admitCas expectedVersion operationId launch change project =
        projectCasTyped expectedVersion operationId
            (ProjectOperationPayload.AdmitProject launch) change project

    let private accountCas expectedVersion operationId payload change (account: Account) =
        match account.Receipts.TryFind operationId with
        | Some original when original = payload -> Ok account
        | Some _ -> Error Rejection.OperationIdReused
        | None when account.Version <> expectedVersion -> Error Rejection.CasConflict
        | None ->
            change account
            |> Result.map (fun changed ->
                { changed with
                    Version = account.Version + 1
                    Receipts = changed.Receipts.Add(operationId, payload) })

    let private completeCoverage (project: Project) =
        if not project.QueueCovered then Error(Rejection.IncompleteCoverage "queue high-water/index")
        elif not project.GraphCovered then Error(Rejection.IncompleteCoverage "dependency graph")
        elif not project.MessageCovered then Error(Rejection.IncompleteCoverage "message high-water/handler")
        elif not project.SourceCovered then Error(Rejection.IncompleteCoverage "source/lineage")
        elif project.Works |> Map.exists (fun _ work ->
            work.Dependencies |> Set.exists (fun id -> not (project.Works.ContainsKey id))) then
            Error(Rejection.IncompleteCoverage "missing prerequisite stage generation")
        else Ok ()

    let isSatisfied (project: Project) workId =
        match project.Works.TryFind workId with
        | Some stage when stage.State = WorkState.Completed ->
            stage.Kind <> WorkKind.Integration || stage.IntegrationAuthorized
        | _ -> false

    // One canonical predicate fences every completion, ancillary handoff,
    // and FIFO wake inside its graph/project CAS snapshot.
    let private hardDependenciesSatisfied (project: Project) (stage: WorkStage) =
        stage.Dependencies
        |> Set.forall (fun id ->
            stage.WaivedDependencies.ContainsKey id || isSatisfied project id)

    let isUnfinished (stage: WorkStage) =
        stage.State <> WorkState.Completed && stage.State <> WorkState.Cancelled

    let private isBlockingOther (project: Project) (stage: WorkStage) =
        project.Works
        |> Map.exists (fun _ candidate ->
            isUnfinished candidate
            && candidate.Dependencies.Contains stage.Id
            && not (candidate.WaivedDependencies.ContainsKey stage.Id)
            && not (isSatisfied project stage.Id))

    let private ready (project: Project) (stage: WorkStage) =
        stage.State = WorkState.Available
        && stage.Gate = GateState.Clear
        && hardDependenciesSatisfied project stage

    let orderAvailable (project: Project) =
        completeCoverage project
        |> Result.map (fun () ->
            project.Works
            |> Map.toList
            |> List.map snd
            |> List.filter (ready project)
            |> List.sortBy (fun stage ->
                (if isBlockingOther project stage then 0 else 1),
                stage.AvailableAt,
                stage.ProjectId,
                stage.AvailableSequence,
                stage.Id))

    // Tarjan on verified hard stage-generation edges, not task IDs or hint relationships.
    let circularHardDependencies (project: Project) =
        match completeCoverage project with
        | Error e -> Error e
        | Ok () ->
            let indices = Dictionary<string, int>()
            let lowlinks = Dictionary<string, int>()
            let openNodes = HashSet<string>()
            let stack = Stack<string>()
            let components = ResizeArray<string list>()
            let mutable nextIndex = 0
            let rec visit node =
                indices.[node] <- nextIndex
                lowlinks.[node] <- nextIndex
                nextIndex <- nextIndex + 1
                stack.Push node
                openNodes.Add node |> ignore
                for next in project.Works.[node].Dependencies do
                    if not (indices.ContainsKey next) then
                        visit next
                        lowlinks.[node] <- min lowlinks.[node] lowlinks.[next]
                    elif openNodes.Contains next then
                        lowlinks.[node] <- min lowlinks.[node] indices.[next]
                if lowlinks.[node] = indices.[node] then
                    let mutable membersFound = []
                    let mutable finished = false
                    while not finished do
                        let popped = stack.Pop()
                        openNodes.Remove popped |> ignore
                        membersFound <- popped :: membersFound
                        finished <- popped = node
                    components.Add(List.sort membersFound)
            for KeyValue(node, _) in project.Works do
                if not (indices.ContainsKey node) then visit node
            components
            |> Seq.choose (fun members ->
                let memberSet = Set.ofList members
                let edges =
                    members
                    |> List.collect (fun node ->
                        project.Works.[node].Dependencies
                        |> Set.toList
                        |> List.filter (fun target -> memberSet.Contains target)
                        |> List.map (fun target -> node, target))
                    |> List.sort
                if members.Length > 1 || (edges |> List.exists (fun (a, b) -> a = b)) then
                    Some { Members = members; WitnessEdges = edges }
                else None)
            |> Seq.sortBy (fun warning -> warning.Members)
            |> Seq.toList
            |> Ok

    let private compatible (project: Project) (launch: Launch) (work: WorkStage) =
        let queueAllowed =
            match project.Subscriptions.TryFind launch.RoleId with
            | Some queues -> queues.Contains work.QueueId
            | None -> false
        queueAllowed
        && (match work.Kind with
            | WorkKind.RoleMessage messageId ->
                match project.Messages.TryFind messageId with
                | Some message ->
                    message.Subject = Subject.Role launch.RoleId
                    && message.WorkId = Some work.Id
                    && message.State = MessageState.Pending
                    && work.QueueId = "role:" + launch.RoleId
                    && work.SubjectId = launch.RoleId
                | None -> false
            | _ -> true)

    let private liveUnclaimed project =
        project.Launches
        |> Map.toList
        |> List.map snd
        |> List.filter (fun launch ->
            not launch.EverClaimed
            && (match launch.State with
                | LaunchState.TerminalPendingRelease _
                | LaunchState.DemandInvalidatedPendingTerminal _ -> false
                | _ -> true))

    // Deterministic maximum-cardinality injective matching; credits are not assignments.
    // A failed augmentation leaves earlier matched launches intact, and later launches
    // can still rematch them. The partial result identifies launches requiring a fence.
    let private matchCreditsPartial project (launches: Launch list) (works: WorkStage list) =
        let candidates launch =
            works
            |> List.filter (compatible project launch)
            |> List.map (fun stage -> stage.Id)
        let lookup = launches |> List.map (fun l -> l.Id, l) |> Map.ofList
        let rec augment (launch: Launch) (seen: Set<string>) (owners: Map<string, string>) =
            candidates launch
            |> List.tryPick (fun workId ->
                if seen.Contains workId then None
                else
                    let visited = seen.Add workId
                    match owners.TryFind workId with
                    | None -> Some(owners.Add(workId, launch.Id))
                    | Some otherLaunch ->
                        match augment lookup.[otherLaunch] visited owners with
                        | None -> None
                        | Some rematched -> Some(rematched.Add(workId, launch.Id)))
        let mutable owners = Map.empty
        for launch in launches |> List.sortBy (fun l -> l.Id) do
            match augment launch Set.empty owners with
            | Some matched -> owners <- matched
            | None -> ()
        owners
        |> Map.toList
        |> List.map (fun (workId, launchId) -> launchId, workId)
        |> Map.ofList

    let private matchCredits project (launches: Launch list) (works: WorkStage list) =
        let matched = matchCreditsPartial project launches works
        if matched.Count = launches.Length then Some matched else None

    let private applyCredits (project: Project) (matching: Map<string, string>) =
        { project with
            Launches =
                project.Launches
                |> Map.map (fun id launch ->
                    if launch.EverClaimed then launch
                    else
                        match launch.State with
                        | LaunchState.TerminalPendingRelease _
                        | LaunchState.DemandInvalidatedPendingTerminal _ -> launch
                        | _ -> { launch with Credit = matching.TryFind id }) }

    // Recheck *all* unclaimed launch credits inside the eligibility mutation CAS.
    // Invalidated launches are fenced until their original external effects are
    // reconciled and a separate terminal project receipt permits account release.
    let private reconcileCredits (project: Project) =
        orderAvailable project
        |> Result.map (fun available ->
            let matching = matchCreditsPartial project (liveUnclaimed project) available
            { project with
                Launches =
                    project.Launches
                    |> Map.map (fun id launch ->
                        if launch.EverClaimed then launch
                        else
                            match launch.State with
                            | LaunchState.TerminalPendingRelease _
                            | LaunchState.DemandInvalidatedPendingTerminal _ -> launch
                            | _ ->
                                match matching.TryFind id with
                                | Some workId -> { launch with Credit = Some workId }
                                | None ->
                                    { launch with
                                        State = LaunchState.DemandInvalidatedPendingTerminal launch.State
                                        Credit = None
                                        Version = launch.Version + 1 }) })

    // Phase A Blocked means unmet hard dependencies. The winning Git-CAS
    // projects an independently witnessed time and a fresh FIFO sequence.
    // Replayed operations reuse both via their original receipt.
    let private wakeBlockedStages now (project: Project) =
        completeCoverage project
        |> Result.map (fun () ->
            (project, project.Works |> Map.toList |> List.map snd)
            ||> List.fold (fun current stage ->
                let allSatisfied = hardDependenciesSatisfied current stage
                if stage.State = WorkState.Blocked
                   && stage.Gate = GateState.Clear
                   && not stage.Dependencies.IsEmpty
                   && allSatisfied then
                    let sequence = current.NextAvailability + 1L
                    let readyStage =
                        { stage with State = WorkState.Available
                                     AvailableSequence = sequence
                                     AvailableAt = now }
                    { current with Works = current.Works.Add(stage.Id, readyStage)
                                   NextAvailability = sequence }
                else current))

    let private projectDependenciesChanged now project =
        wakeBlockedStages now project |> Result.bind reconcileCredits

    // Only auxiliary Phase A transitions use this text key. Publish/Post/Admit
    // use a typed discriminated union with structural equality of every field.
    let private fullInput tag value = sprintf "%s|%A" tag value

    // No untyped independent Blocked reason is admitted. Operator/recovery
    // holds require a separate authority/gate model in a later phase.
    let private normalizePublishedStage (project: Project) (stage: WorkStage) =
        let unmet =
            stage.Dependencies |> Set.exists (fun id ->
                not (stage.WaivedDependencies.ContainsKey id || isSatisfied project id))
        match stage.State with
        | WorkState.Available when unmet -> Ok { stage with State = WorkState.Blocked }
        | WorkState.Blocked when stage.Gate = GateState.Clear && not unmet ->
            Error(Rejection.InvalidState "Blocked requires an unmet hard prerequisite")
        | _ -> Ok stage

    let publishStage expected op (stage: WorkStage) project =
        publishCas expected op stage (fun current ->
            if current.Works.ContainsKey stage.Id then Error(Rejection.InvalidState "duplicate immutable work ID")
            else
                // One actionable mutable stage per explicit source-writer lineage. Reviews are immutable.
                let writerCollision =
                    match stage.Kind, stage.SourceLineage with
                    | WorkKind.Implementation, Some lineage when isUnfinished stage ->
                        current.Works
                        |> Map.exists (fun _ other ->
                            other.Kind = WorkKind.Implementation
                            && isUnfinished other
                            && (match other.SourceLineage with
                                | Some existing ->
                                    existing.RepositoryId = lineage.RepositoryId
                                    && existing.TaskId = lineage.TaskId
                                    && existing.TargetRef = lineage.TargetRef
                                    && existing.LineageId = lineage.LineageId
                                | None -> false))
                    | _ -> false
                if writerCollision then
                    Error(Rejection.InvalidState "concurrent source writer lineage")
                else
                    normalizePublishedStage current stage
                    |> Result.map (fun normalized ->
                        { current with Works = current.Works.Add(stage.Id, normalized)
                                       NextAvailability = max current.NextAvailability stage.AvailableSequence })) project

    let postMessage expected op (message: SubjectMessage) roleWork project =
        messageCas expected op message roleWork (fun current ->
            if current.Messages.ContainsKey message.Id then Error(Rejection.InvalidState "duplicate message ID")
            // The input is an unclaimed conversation event, never a handler or
            // work-stage capability. Role work is generated only by this CAS.
            elif message.State <> MessageState.Pending
                 || message.HandlerGeneration <> 0
                 || message.WorkId.IsSome then
                Error(Rejection.InvalidState "message must be new passive attention")
            else
                match message.Subject, roleWork with
                | Subject.Task _, None ->
                    Ok { current with Messages = current.Messages.Add(message.Id, message) }
                | Subject.Role roleId, Some work when work.Kind = WorkKind.RoleMessage message.Id
                                                     && work.QueueId = "role:" + roleId
                                                     && work.SubjectId = roleId
                                                     && work.StageGeneration > 0
                                                     && work.ClaimGeneration = 0
                                                     && (work.State = WorkState.Available
                                                         || work.State = WorkState.Blocked)
                                                     && work.Gate = GateState.Clear
                                                     && work.Domain = DomainState.Undecided
                                                     && work.SourceLineage.IsNone
                                                     && work.Push.IsNone
                                                     && not work.IntegrationAuthorized
                                                     && not (current.Works.ContainsKey work.Id) ->
                    normalizePublishedStage current work
                    |> Result.map (fun normalized ->
                        { current with
                            Messages = current.Messages.Add(message.Id, { message with WorkId = Some work.Id })
                            Works = current.Works.Add(work.Id, normalized)
                            NextAvailability = max current.NextAvailability work.AvailableSequence })
                | _ -> Error(Rejection.InvalidState "Role message requires exactly one dedicated work generation")) project

    let reserveGlobal expected op launchId account =
        accountCas expected op ("reserve|" + launchId) (fun current ->
            if current.Tokens.ContainsKey launchId then Error(Rejection.InvalidState "launch token already exists")
            elif current.Tokens |> Map.filter (fun _ released -> not released) |> Map.count >= current.MaxConcurrentAgents then
                Error Rejection.NoCapacity
            else Ok { current with Tokens = current.Tokens.Add(launchId, false) }) account

    let resizeGlobal expected op newLimit account =
        accountCas expected op ("resize|" + string newLimit) (fun current ->
            let occupied = current.Tokens |> Map.filter (fun _ released -> not released) |> Map.count
            if newLimit < 0 || newLimit < occupied then Error Rejection.NoCapacity
            else Ok { current with MaxConcurrentAgents = newLimit }) account

    let admitProject expected op launch project =
        admitCas expected op launch (fun current ->
            match orderAvailable current with
            | Error e -> Error e
            | Ok candidates ->
                if current.Launches.ContainsKey launch.Id then Error(Rejection.InvalidState "launch ID reused")
                else
                    let next = { current with Launches = current.Launches.Add(launch.Id, launch) }
                    match matchCredits next (liveUnclaimed next) candidates with
                    | None -> Error Rejection.NoCompatibleMatching
                    | Some matching -> Ok(applyCredits next matching)) project

    // C01: original project demand reservation and abort-only tombstone conflict on launch-ID absence.
    let abortUnboundAdmission expected op launch project =
        projectCas expected op ("abort-unbound|" + launch.Id) (fun current ->
            if current.Launches.ContainsKey launch.Id then Error(Rejection.InvalidState "project admission exists")
            else
                let terminal = { launch with State = LaunchState.TerminalPendingRelease("abort:" + op) }
                Ok { current with Launches = current.Launches.Add(launch.Id, terminal) }) project

    let dispatchOnce expected op launchId outcome project =
        projectCas expected op ("dispatch|" + launchId + "|" + string outcome) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch when launch.State = LaunchState.ReservedUnregistered ->
                let updated =
                    { launch with
                        State = LaunchState.SubmittedUnregistered outcome
                        DispatchCount = launch.DispatchCount + 1
                        Version = launch.Version + 1 }
                Ok { current with Launches = current.Launches.Add(launchId, updated) }
            | _ -> Error(Rejection.InvalidState "dispatch is one-shot and requires reserved launch")) project

    // The caller supplies entropy for a deterministic fixture; the production server will allocate UUID.
    let initializeAgent expected op launchId sessionKey serverUuid now project =
        projectCas expected op ("initialize|" + launchId + "|" + sessionKey) (fun current ->
            match current.Launches.TryFind launchId with
            | None -> Error(Rejection.InvalidState "unknown launch")
            | Some launch ->
                match launch.State with
                | LaunchState.Registered(_, existingKey) when existingKey <> sessionKey ->
                    Error Rejection.AlreadyRegistered
                | LaunchState.Registered _ -> Ok current
                | LaunchState.TerminalPendingRelease _ -> Error Rejection.Terminal
                | LaunchState.SubmittedUnregistered DispatchState.Submitted
                | LaunchState.SubmittedUnregistered DispatchState.OutcomeUnknown ->
                    if String.IsNullOrWhiteSpace sessionKey || String.IsNullOrWhiteSpace serverUuid then
                        Error(Rejection.InvalidState "missing session key/UUID")
                    else
                        let updated =
                            { launch with
                                State = LaunchState.Registered(serverUuid, sessionKey)
                                Lease = Some { LastSeenAt = now; Generation = 1; StopRequested = false }
                                Version = launch.Version + 1 }
                        Ok { current with Launches = current.Launches.Add(launchId, updated) }
                | _ -> Error(Rejection.InvalidState "unsubmitted launch cannot register")) project

    let heartbeat expected op launchId expectedLeaseGeneration now project =
        projectCas expected op ("heartbeat|" + launchId + "|" + string expectedLeaseGeneration + "|" + string now) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch ->
                match launch.State, launch.Lease with
                | LaunchState.Registered _, Some lease
                    when lease.Generation = expectedLeaseGeneration
                         && not lease.StopRequested
                         && now >= lease.LastSeenAt ->
                    let updated =
                        { launch with
                            Lease = Some { lease with
                                            LastSeenAt = now
                                            Generation = lease.Generation + 1 }
                            Version = launch.Version + 1 }
                    Ok { current with Launches = current.Launches.Add(launchId, updated) }
                | _ -> Error(Rejection.InvalidState "heartbeat cannot restore stalled or terminal launch")
            | None -> Error(Rejection.InvalidState "unknown launch")) project

    let markStalled expected op launchId expectedLeaseGeneration now stalledAfter project =
        projectCas expected op ("stalled|" + launchId + "|" + string expectedLeaseGeneration) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch ->
                match launch.State, launch.Lease with
                | LaunchState.Registered _, Some lease
                    when lease.Generation = expectedLeaseGeneration
                         && not lease.StopRequested
                         && stalledAfter > 0L
                         && now - lease.LastSeenAt >= stalledAfter ->
                    let updated =
                        { launch with
                            Lease = Some { lease with StopRequested = true; Generation = lease.Generation + 1 }
                            Version = launch.Version + 1 }
                    Ok { current with Launches = current.Launches.Add(launchId, updated) }
                | _ -> Error(Rejection.InvalidState "lease is still live or already fenced")
            | None -> Error(Rejection.InvalidState "unknown launch")) project

    let recoverRegisteredNoClaim expected op launchId expectedLeaseGeneration now reclaimAfter journalSettled project =
        projectCas expected op ("recover-no-claim|" + launchId + "|" + string expectedLeaseGeneration) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch when not launch.EverClaimed && launch.PrimaryWorkId.IsNone
                               && launch.AncillaryMessages.IsEmpty && journalSettled ->
                match launch.State, launch.Lease with
                | LaunchState.Registered _, Some lease
                    when lease.StopRequested
                         && lease.Generation = expectedLeaseGeneration
                         && reclaimAfter > 0L
                         && now - lease.LastSeenAt >= reclaimAfter ->
                    let terminal =
                        { launch with
                            State = LaunchState.TerminalPendingRelease("stalled-no-claim:" + op)
                            Credit = None
                            Version = launch.Version + 1 }
                    Ok { current with Launches = current.Launches.Add(launchId, terminal) }
                | _ -> Error Rejection.UnsafeExternalEffect
            | _ -> Error Rejection.UnsafeExternalEffect) project

    let claimNext expected op launchId project =
        projectCas expected op ("claim-next|" + launchId) (fun current ->
            match current.Launches.TryFind launchId, orderAvailable current with
            | _, Error e -> Error e
            | None, _ -> Error(Rejection.InvalidState "unknown launch")
            | Some launch, Ok ordered ->
                match launch.State with
                | LaunchState.Registered _ when not launch.EverClaimed
                      && (match launch.Lease with Some lease -> not lease.StopRequested | None -> false) ->
                    let other = liveUnclaimed current |> List.filter (fun l -> l.Id <> launchId)
                    let feasible =
                        ordered
                        |> List.filter (compatible current launch)
                        |> List.tryPick (fun work ->
                            let remaining = ordered |> List.filter (fun candidate -> candidate.Id <> work.Id)
                            match matchCredits current other remaining with
                            | Some matches -> Some(work, matches)
                            | None -> None)
                    match feasible with
                    | None -> Error Rejection.NoEligibleWork
                    | Some(work, matches) ->
                        let nextWork =
                            { work with
                                State = WorkState.Claimed launchId
                                ClaimGeneration = work.ClaimGeneration + 1 }
                        let claimedLaunch =
                            { launch with
                                PrimaryWorkId = Some work.Id
                                EverClaimed = true
                                Credit = None
                                Version = launch.Version + 1 }
                        let next =
                            { current with
                                Works = current.Works.Add(work.Id, nextWork)
                                Launches = current.Launches.Add(launchId, claimedLaunch) }
                        let next = applyCredits next matches
                        match work.Kind with
                        | WorkKind.RoleMessage id ->
                            let message = next.Messages.[id]
                            if message.State <> MessageState.Pending then
                                Error(Rejection.InvalidState "message already handled/claimed")
                            else
                                Ok { next with
                                        Messages = next.Messages.Add(id,
                                            { message with State = MessageState.ClaimedPrimary launchId
                                                           HandlerGeneration = nextWork.ClaimGeneration }) }
                        | _ -> Ok next
                | _ -> Error(Rejection.InvalidState "requires registered launch with no prior primary claim")) project

    let terminalizeNoClaim expected op launchId project =
        projectCas expected op ("no-claim-terminal|" + launchId) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch when not launch.EverClaimed ->
                match launch.State with
                | LaunchState.Registered _ ->
                    let terminal =
                        { launch with
                            State = LaunchState.TerminalPendingRelease("terminal:" + op)
                            Credit = None
                            Version = launch.Version + 1 }
                    Ok { current with Launches = current.Launches.Add(launchId, terminal) }
                | _ -> Error(Rejection.InvalidState "must be registered")
            | _ -> Error(Rejection.InvalidState "primary claim already existed")) project

    // An ancillary message lease cannot survive its unrelated primary activation.
    // Release/requeue all unfinished handler responsibilities in the same terminal Git CAS.
    // An unresolved hard edge becomes an ordinary dependency-Blocked Role stage,
    // never Available with an unowned DependencyGatePending.
    let private releaseAncillaryOnTerminal now (launch: Launch) (current: Project) =
        let mutable state = current
        let mutable failure = None
        for messageId in launch.AncillaryMessages do
            if failure.IsNone then
                match state.Messages.TryFind messageId with
                | Some message when message.State = MessageState.ClaimedAncillary launch.Id ->
                    // Invalidate even a workless ancillary handler at terminal.
                    // Stage-backed claims advance the same canonical generation.
                    let pending = { message with State = MessageState.Pending
                                                 HandlerGeneration = message.HandlerGeneration + 1 }
                    state <- { state with Messages = state.Messages.Add(messageId, pending) }
                    match message.WorkId with
                    | None -> ()
                    | Some workId ->
                        match state.Works.TryFind workId with
                        | Some work when work.State = WorkState.Claimed launch.Id
                                         && work.Kind = WorkKind.RoleMessage messageId ->
                            match work.Gate with
                            | GateState.Clear
                            | GateState.DependencyGatePending _ when work.SourceLineage.IsNone
                                                                 && work.Push.IsNone
                                                                 && work.Domain = DomainState.Undecided ->
                                // This pure Role-message handler cannot have a
                                // source/effect to reconcile. Invalidate its old
                                // claim before publishing any new handler credit.
                                let satisfied = hardDependenciesSatisfied state work
                                let nextSequence =
                                    if satisfied then state.NextAvailability + 1L
                                    else state.NextAvailability
                                let released =
                                    { work with
                                        State = if satisfied then WorkState.Available else WorkState.Blocked
                                        Gate = GateState.Clear
                                        ClaimGeneration = work.ClaimGeneration + 1
                                        AvailableSequence = if satisfied then nextSequence else work.AvailableSequence
                                        AvailableAt = if satisfied then now else work.AvailableAt }
                                state <- { state with
                                            Works = state.Works.Add(workId, released)
                                            NextAvailability = nextSequence }
                            | _ -> failure <- Some Rejection.UnsafeExternalEffect
                        | _ -> failure <- Some(Rejection.InvalidState "unreconciled ancillary Role-message claim")
                | _ -> failure <- Some(Rejection.InvalidState "unreconciled ancillary message handler")
        match failure with
        | Some e -> Error e
        | None -> Ok state

    let private finishClaim op launchId workId now (finishStage: WorkStage -> Result<WorkStage, Rejection>) (current: Project) =
        match current.Launches.TryFind launchId, current.Works.TryFind workId with
        | Some launch, Some stage when launch.PrimaryWorkId = Some workId
                                     && stage.State = WorkState.Claimed launchId ->
            match finishStage stage with
            | Error e -> Error e
            | Ok finished ->
                releaseAncillaryOnTerminal now launch current
                |> Result.map (fun cleaned ->
                    let terminal =
                        { launch with
                            State = LaunchState.TerminalPendingRelease("terminal:" + op)
                            AncillaryMessages = Set.empty
                            Version = launch.Version + 1 }
                    { cleaned with
                        Works = cleaned.Works.Add(workId, finished)
                        Launches = cleaned.Launches.Add(launchId, terminal) })
        | _ -> Error(Rejection.InvalidState "stale work claim/launch generation")

    let completeOrdinary expected op launchId workId now project =
        projectCas expected op (fullInput "complete" (launchId, workId, now)) (fun current ->
            finishClaim op launchId workId now (fun stage ->
                match stage.Kind, stage.Gate, stage.Domain with
                | WorkKind.RoleMessage _, _, _ -> Error(Rejection.InvalidState "dedicated message needs atomic completion")
                | _, GateState.Clear, _ when not (hardDependenciesSatisfied current stage) ->
                    Error Rejection.DependencyGatePending
                | WorkKind.FrozenReview, GateState.Clear, DomainState.Undecided
                | WorkKind.Integration, GateState.Clear, DomainState.Undecided ->
                    Error(Rejection.InvalidState "review/integration requires immutable final domain receipt")
                | _, GateState.Clear, DomainState.Undecided -> Ok { stage with State = WorkState.Completed }
                | _, GateState.Clear, DomainState.FinalizedPendingTerminal _ ->
                    Ok { stage with State = WorkState.Completed }
                | _ -> Error Rejection.DependencyGatePending) current
            |> Result.bind (projectDependenciesChanged now)) project

    let completeMessageWork expected op launchId workId messageId expectedHandlerGeneration replyId now project =
        projectCas expected op
            (fullInput "complete-message" (launchId, workId, messageId, expectedHandlerGeneration, replyId, now))
            (fun current ->
            match current.Messages.TryFind messageId with
            | Some message when message.WorkId = Some workId
                                && message.State = MessageState.ClaimedPrimary launchId
                                && message.HandlerGeneration = expectedHandlerGeneration ->
                finishClaim op launchId workId now (fun stage ->
                    if stage.Kind = WorkKind.RoleMessage messageId
                       && stage.ClaimGeneration = expectedHandlerGeneration
                       && stage.Gate = GateState.Clear
                       && hardDependenciesSatisfied current stage then
                        Ok { stage with State = WorkState.Completed }
                    else Error(Rejection.InvalidState "not the one claimed primary message")) current
                |> Result.map (fun next ->
                    let handled = { message with State = MessageState.Handled replyId }
                    { next with Messages = next.Messages.Add(messageId, handled) })
                |> Result.bind (projectDependenciesChanged now)
            | _ -> Error(Rejection.InvalidState "message handler does not own primary work")) project

    // A Registered launch remains Registered after STOP; ancillary intake and
    // acknowledgement require its live, ungated primary activation under CAS.
    let private hasLivePrimaryActivation (current: Project) launchId (launch: Launch) =
        match launch.State, launch.Lease, launch.PrimaryWorkId with
        | LaunchState.Registered _, Some lease, Some primaryId
            when launch.EverClaimed && not lease.StopRequested ->
            match current.Works.TryFind primaryId with
            | Some primary ->
                primary.State = WorkState.Claimed launchId
                && primary.ClaimGeneration > 0
                // A dedicated Role-message activation owns one message only.
                // It cannot widen that primary claim by ancillary intake.
                && (match primary.Kind with WorkKind.RoleMessage _ -> false | _ -> true)
                && primary.Gate = GateState.Clear
                && primary.Domain = DomainState.Undecided
                && hardDependenciesSatisfied current primary
            | None -> false
        | _ -> false

    let claimAncillaryMessage expected op launchId messageId project =
        projectCas expected op ("ancillary-claim|" + launchId + "|" + messageId) (fun current ->
            match current.Launches.TryFind launchId, current.Messages.TryFind messageId with
            | Some launch, Some message when hasLivePrimaryActivation current launchId launch
                                             && message.State = MessageState.Pending ->
                let eligibleSubject =
                    match message.Subject with
                    | Subject.Role roleId -> roleId = launch.RoleId
                    | Subject.Task taskId ->
                        match launch.PrimaryWorkId with
                        | Some id ->
                            current.Works.TryFind id
                            |> Option.exists (fun primary -> primary.SubjectId = taskId)
                        | None -> false
                // WorkId is an untrusted field in a stored conversation event.
                // A Task message must stay passive; a Role message may claim
                // only its own typed stage in the launch's subscribed queue.
                let validMessageWork =
                    match message.Subject, message.WorkId with
                    | Subject.Task _, None -> true
                    | Subject.Role roleId, Some workId ->
                        match current.Works.TryFind workId with
                        | Some work ->
                            work.Kind = WorkKind.RoleMessage messageId
                            && work.QueueId = "role:" + roleId
                            && work.SubjectId = roleId
                            && compatible current launch work
                            && work.SourceLineage.IsNone
                            && work.Push.IsNone
                            && work.Domain = DomainState.Undecided
                            && not work.IntegrationAuthorized
                        | None -> false
                    | _ -> false
                if not eligibleSubject then Error(Rejection.InvalidState "ineligible task/role actor")
                elif not validMessageWork then
                    Error(Rejection.InvalidState "ancillary message does not match passive Task or its Role work generation")
                else
                    match message.WorkId with
                    | None ->
                        // Passive Task attention has no orderAvailable path, but
                        // must obey its verified message/queue/source high-water.
                        completeCoverage current
                        |> Result.map (fun () ->
                            let updatedLaunch = { launch with AncillaryMessages = launch.AncillaryMessages.Add messageId }
                            { current with
                                Launches = current.Launches.Add(launchId, updatedLaunch)
                                Messages = current.Messages.Add(messageId,
                                    { message with State = MessageState.ClaimedAncillary launchId
                                                   HandlerGeneration = message.HandlerGeneration + 1 }) })
                    | Some workId ->
                        match orderAvailable current with
                        | Error e -> Error e
                        | Ok ordered ->
                            match current.Works.TryFind workId with
                            | Some work when ready current work ->
                                let others = liveUnclaimed current
                                let remaining = ordered |> List.filter (fun w -> w.Id <> workId)
                                match matchCredits current others remaining with
                                | None -> Error Rejection.NoCompatibleMatching
                                | Some matching ->
                                    let claimed = { work with State = WorkState.Claimed launchId
                                                              ClaimGeneration = work.ClaimGeneration + 1 }
                                    let updatedLaunch = { launch with AncillaryMessages = launch.AncillaryMessages.Add messageId }
                                    let next =
                                        { current with
                                            Works = current.Works.Add(workId, claimed)
                                            Launches = current.Launches.Add(launchId, updatedLaunch)
                                            Messages = current.Messages.Add(messageId,
                                                { message with State = MessageState.ClaimedAncillary launchId
                                                               HandlerGeneration = claimed.ClaimGeneration }) }
                                    Ok(applyCredits next matching)
                            | _ -> Error(Rejection.InvalidState "role message work is not Available")
            | _ -> Error(Rejection.InvalidState "ancillary handler requires active unrelated primary claim")) project

    // Project.Version cannot fence a reply created before a same-launch reclaim.
    // The response and the idempotent receipt must pin its handler generation.
    let completeAncillaryMessage expected op launchId messageId expectedHandlerGeneration replyId now project =
        projectCas expected op
            (fullInput "ancillary-complete" (launchId, messageId, expectedHandlerGeneration, replyId, now))
            (fun current ->
            match current.Launches.TryFind launchId, current.Messages.TryFind messageId with
            | Some launch, Some message when launch.AncillaryMessages.Contains messageId
                                             && message.State = MessageState.ClaimedAncillary launchId
                                             && message.HandlerGeneration = expectedHandlerGeneration
                                             && hasLivePrimaryActivation current launchId launch ->
                let markHandled (next: Project) =
                    let updatedLaunch = { launch with AncillaryMessages = launch.AncillaryMessages.Remove messageId }
                    { next with
                        Launches = next.Launches.Add(launchId, updatedLaunch)
                        Messages = next.Messages.Add(messageId, { message with State = MessageState.Handled replyId }) }
                match message.WorkId with
                | None ->
                    // Intake coverage may have regressed while this passive
                    // handler held the message; fail closed before acknowledging.
                    completeCoverage current |> Result.map (fun () -> markHandled current)
                | Some workId ->
                    match current.Works.TryFind workId with
                    | Some work when work.State = WorkState.Claimed launchId
                                     && work.Kind = WorkKind.RoleMessage messageId ->
                        // The graph edit and completion compete at Project.Version.
                        // Once a new hard edge wins, both reply and downstream
                        // publication must remain fenced by its dependency gate.
                        if work.ClaimGeneration <> expectedHandlerGeneration then
                            Error(Rejection.InvalidState "stale ancillary handler generation")
                        elif work.Gate <> GateState.Clear || not (hardDependenciesSatisfied current work) then
                            Error Rejection.DependencyGatePending
                        elif work.SourceLineage.IsSome || work.Push.IsSome
                             || work.Domain <> DomainState.Undecided then
                            Error Rejection.UnsafeExternalEffect
                        else
                            { current with Works = current.Works.Add(workId, { work with State = WorkState.Completed }) }
                            |> markHandled
                            |> projectDependenciesChanged now
                    | _ -> Error(Rejection.InvalidState "ancillary work lost its handler claim")
            | Some launch, Some message when launch.AncillaryMessages.Contains messageId
                                             && message.State = MessageState.ClaimedAncillary launchId
                                             && message.HandlerGeneration <> expectedHandlerGeneration ->
                Error(Rejection.InvalidState "stale ancillary handler generation")
            | _ -> Error(Rejection.InvalidState "no live ancillary message handler")) project

    // A new hard prerequisite may gate an ancillary Role handler while its
    // launch still owns an unrelated primary task. Requeue that handler under
    // the same project CAS without terminalizing the primary or its token.
    // The stage claim generation is the handler generation for a Role message.
    let resolveAncillaryDependencyGate expected op launchId messageId workId
                                       expectedLaunchVersion expectedClaimGeneration expectedGateVersion now project =
        projectCas expected op
            (fullInput "resolve-ancillary-dependency"
                (launchId, messageId, workId, expectedLaunchVersion, expectedClaimGeneration, expectedGateVersion, now))
            (fun current ->
                match current.Launches.TryFind launchId,
                      current.Messages.TryFind messageId,
                      current.Works.TryFind workId with
                | Some launch, Some message, Some work ->
                    let activePrimary =
                        match launch.State, launch.PrimaryWorkId with
                        | LaunchState.Registered _, Some primaryId when primaryId <> workId ->
                            match current.Works.TryFind primaryId with
                            | Some primary -> primary.State = WorkState.Claimed launchId
                            | None -> false
                        | _ -> false
                    if not activePrimary || launch.Version <> expectedLaunchVersion
                       || not (launch.AncillaryMessages.Contains messageId)
                       || message.State <> MessageState.ClaimedAncillary launchId
                       || message.WorkId <> Some workId
                       || message.HandlerGeneration <> expectedClaimGeneration
                       || work.Kind <> WorkKind.RoleMessage messageId
                       || work.State <> WorkState.Claimed launchId
                       || work.ClaimGeneration <> expectedClaimGeneration
                       || work.Gate <> GateState.DependencyGatePending expectedGateVersion
                       || current.GraphVersion < expectedGateVersion then
                        Error(Rejection.InvalidState "stale ancillary claim/launch/gate generation")
                    elif work.SourceLineage.IsSome || work.Push.IsSome
                         || work.Domain <> DomainState.Undecided
                         || work.IntegrationAuthorized then
                        Error Rejection.UnsafeExternalEffect
                    else
                        let satisfied = hardDependenciesSatisfied current work
                        let sequence =
                            if satisfied then current.NextAvailability + 1L
                            else current.NextAvailability
                        let released =
                            { work with
                                State = if satisfied then WorkState.Available else WorkState.Blocked
                                Gate = GateState.Clear
                                ClaimGeneration = work.ClaimGeneration + 1
                                AvailableSequence = if satisfied then sequence else work.AvailableSequence
                                AvailableAt = if satisfied then now else work.AvailableAt }
                        let next =
                            { current with
                                Works = current.Works.Add(workId, released)
                                Messages = current.Messages.Add(messageId,
                                    { message with State = MessageState.Pending
                                                   HandlerGeneration = released.ClaimGeneration })
                                Launches = current.Launches.Add(launchId,
                                    { launch with
                                        AncillaryMessages = launch.AncillaryMessages.Remove messageId
                                        Version = launch.Version + 1 })
                                NextAvailability = sequence }
                        projectDependenciesChanged now next
                | _ -> Error(Rejection.InvalidState "no live ancillary gate handler")) project

    let startOriginalPush expected op launchId workId pushOperation acceptedSourceSha targetRef project =
        projectCas expected op ("push-intent|" + launchId + "|" + workId + "|" + pushOperation
                                + "|" + acceptedSourceSha + "|" + targetRef) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.Kind = WorkKind.Integration
                             && work.State = WorkState.Claimed launchId
                             && work.Gate = GateState.Clear && work.Push.IsNone
                             && not (String.IsNullOrWhiteSpace acceptedSourceSha)
                             && not (String.IsNullOrWhiteSpace targetRef) ->
                let journal =
                    { OperationId = pushOperation
                      AcceptedSourceSha = acceptedSourceSha
                      TargetRef = targetRef
                      Outcome = PushOutcome.Unknown }
                Ok { current with Works = current.Works.Add(workId, { work with Push = Some journal }) }
            | _ -> Error(Rejection.InvalidState "push already dispatched or integration not safe")) project

    let setHardPrerequisite expected op workId prerequisite now project =
        projectCas expected op (fullInput "hard-edge" (workId, prerequisite, now)) (fun current ->
            match current.Works.TryFind workId with
            | None -> Error(Rejection.InvalidState "unknown stage")
            | Some work when not (current.Works.ContainsKey prerequisite) ->
                Error(Rejection.IncompleteCoverage "prerequisite missing")
            | Some work when work.State = WorkState.Completed || work.State = WorkState.Cancelled ->
                Error Rejection.StageAlreadyTerminal
            | Some work when work.Domain <> DomainState.Undecided ->
                Error Rejection.DomainAlreadyFinalized
            | Some work ->
                let nextGraph = current.GraphVersion + 1
                let dependencies = work.Dependencies.Add prerequisite
                let unsatisfied = not (isSatisfied current prerequisite || work.WaivedDependencies.ContainsKey prerequisite)
                let gate =
                    match work.State, work.Kind, work.Push with
                    | WorkState.Claimed _, WorkKind.Integration, Some journal when unsatisfied ->
                        GateState.IntegrationGateHold(nextGraph, journal.OperationId)
                    | WorkState.Claimed _, _, _ when unsatisfied ->
                        GateState.DependencyGatePending nextGraph
                    | _ -> work.Gate
                let state =
                    if work.State = WorkState.Available && unsatisfied then WorkState.Blocked
                    else work.State
                let updated = { work with Dependencies = dependencies; Gate = gate; State = state }
                { current with GraphVersion = nextGraph
                               Works = current.Works.Add(workId, updated) }
                |> projectDependenciesChanged now) project

    let private waiverMatches (work: WorkStage) (required: Set<string>) (receipt: VerifiedWaiverReceipt) =
        receipt.CanonicallyVerified
        && receipt.AccountApprovalVersion > 0
        && not (String.IsNullOrWhiteSpace receipt.ReceiptId)
        && (receipt.ApprovedByRole = "Lead" || receipt.ApprovedByRole = "Operator")
        && receipt.WorkId = work.Id
        && Set.isSubset required receipt.Dependencies

    // Cancellation does not satisfy other stages' hard edges: a dependent
    // remains Blocked until a separately verified waiver is committed.
    let cancelUnclaimedStage expected op workId reason now project =
        projectCas expected op (fullInput "cancel-stage" (workId, reason, now)) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.State = WorkState.Available || work.State = WorkState.Blocked ->
                { current with Works = current.Works.Add(workId, { work with State = WorkState.Cancelled }) }
                |> projectDependenciesChanged now
            | _ -> Error(Rejection.InvalidState "only unclaimed nonterminal work may be cancelled")) project

    let waiveHardPrerequisite expected op workId prerequisite (receipt: VerifiedWaiverReceipt) now project =
        projectCas expected op (fullInput "waive-hard-edge" (workId, prerequisite, receipt, now)) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.Dependencies.Contains prerequisite
                             && work.Domain = DomainState.Undecided
                             && isUnfinished work ->
                if not (waiverMatches work (Set.singleton prerequisite) receipt) then
                    Error Rejection.MissingProof
                else
                    let updated =
                        { work with WaivedDependencies = work.WaivedDependencies.Add(prerequisite, receipt.ReceiptId) }
                    { current with Works = current.Works.Add(workId, updated) }
                    |> projectDependenciesChanged now
            | _ -> Error(Rejection.InvalidState "waiver needs an undecided live hard edge")) project

    let decideReview expected op workId decisionReceipt project =
        projectCas expected op ("review-verdict|" + workId + "|" + decisionReceipt) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.Kind = WorkKind.FrozenReview
                             && (match work.State with WorkState.Claimed _ -> true | _ -> false)
                             && work.Gate = GateState.Clear
                             && work.Domain = DomainState.Undecided
                             && (work.Dependencies |> Set.forall (fun id ->
                                 work.WaivedDependencies.ContainsKey id || isSatisfied current id)) ->
                Ok { current with
                        Works = current.Works.Add(workId,
                            { work with Domain = DomainState.FinalizedPendingTerminal decisionReceipt })
                        ProducerCount = current.ProducerCount + 1 }
            | _ -> Error Rejection.DependencyGatePending) project

    // A verified original external effect is a separate durable fact from domain authorization.
    // After an integration hold exists, only reconcileExternalEffect is legal, never this ordinary path.
    let recordUngatedPushEffect expected op workId (evidence: VerifiedRemoteEffect) project =
        projectCas expected op ("ungated-effect|" + workId + "|" + string evidence) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.Kind = WorkKind.Integration
                             && work.Gate = GateState.Clear
                             && (match work.State with WorkState.Claimed _ -> true | _ -> false)
                             && evidence.AncestryVerified
                             && evidence.JobsReconciled
                             && not (String.IsNullOrWhiteSpace evidence.RemoteCommitSha) ->
                match work.Push with
                | Some journal when journal.OperationId = evidence.OriginalPushOperationId
                                    && journal.AcceptedSourceSha = evidence.AcceptedSourceSha
                                    && journal.TargetRef = evidence.TargetRef
                                    && journal.Outcome = PushOutcome.Unknown ->
                    Ok { current with
                            Works = current.Works.Add(workId,
                                { work with Push = Some { journal with Outcome = PushOutcome.Observed evidence.RemoteCommitSha } }) }
                | _ -> Error Rejection.MissingProof
            | _ -> Error Rejection.DependencyGatePending) project

    let recordDefinitiveNoEffect expected op workId (proof: DefinitiveNoEffectProof) project =
        projectCas expected op ("no-external-effect|" + workId + "|" + string proof) (fun current ->
            match current.Works.TryFind workId with
            | Some work ->
                match work.Push with
                | Some journal when journal.OperationId = proof.OriginalPushOperationId
                                    && journal.Outcome = PushOutcome.Unknown
                                    && proof.DispatchedOperationTerminal && proof.JobsReconciled ->
                    Ok { current with
                            Works = current.Works.Add(workId,
                                { work with Push = Some { journal with Outcome = PushOutcome.DefinitiveNoEffect } }) }
                | _ -> Error Rejection.UnsafeExternalEffect
            | None -> Error(Rejection.InvalidState "unknown integration stage")) project

    let finalizeIntegration expected op workId resultReceipt project =
        projectCas expected op ("integration-final|" + workId + "|" + resultReceipt) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.Kind = WorkKind.Integration
                             && (match work.State with WorkState.Claimed _ -> true | _ -> false)
                             && work.Gate = GateState.Clear
                             && work.Domain = DomainState.Undecided
                             && (work.Dependencies |> Set.forall (fun id ->
                                 work.WaivedDependencies.ContainsKey id || isSatisfied current id)) ->
                match work.Push with
                | Some { Outcome = PushOutcome.Observed remoteCommit } when
                    not (String.IsNullOrWhiteSpace remoteCommit)
                    && not (String.IsNullOrWhiteSpace resultReceipt) ->
                    Ok { current with
                            Works = current.Works.Add(workId,
                                { work with Domain = DomainState.FinalizedPendingTerminal resultReceipt
                                            IntegrationAuthorized = true })
                            ProducerCount = current.ProducerCount + 1 }
                | _ -> Error Rejection.MissingProof
            | _ -> Error Rejection.DependencyGatePending) project

    let private validatedHandoff (work: WorkStage) proof =
        match work.SourceLineage, proof with
        | None, SourceHandoffProof.ReadOnly receipt
            when work.Kind <> WorkKind.Implementation
                 && not (String.IsNullOrWhiteSpace receipt) -> Ok None
        | Some lineage, SourceHandoffProof.RemoteSnapshot evidence
            when evidence.ReachabilityVerified
                 && evidence.JobsReconciled
                 && evidence.RepositoryId = lineage.RepositoryId
                 && evidence.TaskId = lineage.TaskId
                 && evidence.LineageId = lineage.LineageId
                 && evidence.StageId = work.Id
                 && evidence.StageGeneration = work.StageGeneration
                 && evidence.ClaimGeneration = work.ClaimGeneration
                 && evidence.LineageGeneration = lineage.Generation
                 && evidence.ExpectedRef = (lineage.ReachableRef |> Option.defaultValue lineage.TargetRef)
                 && evidence.ExpectedSha = (lineage.ContinuationSha |> Option.defaultValue lineage.PinnedBaseCommit)
                 && not (String.IsNullOrWhiteSpace evidence.ExpectedSha)
                 && not (String.IsNullOrWhiteSpace evidence.ContinuationSha)
                 && not (String.IsNullOrWhiteSpace evidence.ContinuationRef) ->
            Ok(Some
                { lineage with
                    ReachableRef = Some evidence.ContinuationRef
                    ContinuationSha = Some evidence.ContinuationSha
                    Generation = lineage.Generation + 1 })
        | _, SourceHandoffProof.Unsafe _ -> Error Rejection.UnsafeExternalEffect
        | _ -> Error Rejection.MissingProof

    let resolveDependencyGate expected op launchId workId (sourceProof: SourceHandoffProof) now project =
        projectCas expected op (fullInput "resolve-dependency" (launchId, workId, sourceProof, now)) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.State = WorkState.Claimed launchId ->
                let pushSafe =
                    match work.Push with
                    | None -> true
                    | Some journal -> journal.Outcome = PushOutcome.DefinitiveNoEffect
                match work.Gate with
                | GateState.DependencyGatePending _
                | GateState.IntegrationGateHold _ when pushSafe ->
                    match validatedHandoff work sourceProof with
                    | Error e -> Error e
                    | Ok continuation ->
                        let unmet =
                            work.Dependencies
                            |> Set.exists (fun id ->
                                not (work.WaivedDependencies.ContainsKey id || isSatisfied current id))
                        let newStage =
                            if unmet then
                                { work with State = WorkState.Blocked
                                            Gate = GateState.Clear
                                            SourceLineage = continuation }
                            else
                                { work with
                                    State = WorkState.Available
                                    Gate = GateState.Clear
                                    SourceLineage = continuation
                                    AvailableAt = now
                                    AvailableSequence = current.NextAvailability + 1L }
                        finishClaim op launchId workId now (fun _ -> Ok newStage) current
                        |> Result.map (fun next ->
                            { next with NextAvailability = max next.NextAvailability newStage.AvailableSequence })
                        |> Result.bind (projectDependenciesChanged now)
                | _ -> Error Rejection.UnsafeExternalEffect
            | _ -> Error(Rejection.InvalidState "no gated active claim")) project

    let reconcileExternalEffect expected op launchId workId (evidence: VerifiedRemoteEffect) now project =
        projectCas expected op (fullInput "observed-effect" (launchId, workId, evidence, now)) (fun current ->
            match current.Works.TryFind workId with
            | Some work ->
                match work.Gate, work.Push with
                | GateState.IntegrationGateHold(graph, pushId), Some journal
                    when pushId = evidence.OriginalPushOperationId
                         && journal.OperationId = pushId
                         && journal.AcceptedSourceSha = evidence.AcceptedSourceSha
                         && journal.TargetRef = evidence.TargetRef
                         && evidence.AncestryVerified
                         && evidence.JobsReconciled
                         && not (String.IsNullOrWhiteSpace evidence.RemoteCommitSha)
                         && work.State = WorkState.Claimed launchId ->
                    let observed =
                        { work with
                            State = WorkState.GateException
                            Gate = GateState.ExternalEffectObservedPendingGate(graph, pushId, evidence.RemoteCommitSha)
                            Push = Some { journal with Outcome = PushOutcome.Observed evidence.RemoteCommitSha } }
                    finishClaim op launchId workId now (fun _ -> Ok observed) current
                | _ -> Error Rejection.UnsafeExternalEffect
            | None -> Error(Rejection.InvalidState "unknown integration stage")) project

    let resolveIntegrationGateException expected op workId (waiverReceipt: VerifiedWaiverReceipt option) now project =
        projectCas expected op (fullInput "resolve-exception" (workId, waiverReceipt, now)) (fun current ->
            match current.Works.TryFind workId with
            | Some work when work.State = WorkState.GateException ->
                match work.Gate with
                | GateState.ExternalEffectObservedPendingGate(_, _, remoteCommit) ->
                    let verified = work.Dependencies |> Set.forall (isSatisfied current)
                    let unmet = work.Dependencies |> Set.filter (fun id -> not (isSatisfied current id))
                    let validWaiver =
                        match waiverReceipt with
                        | Some receipt when waiverMatches work unmet receipt -> Some receipt
                        | _ -> None
                    if not verified && validWaiver.IsNone then Error Rejection.MissingProof
                    else
                        let waived =
                            match validWaiver with
                            | Some receipt ->
                                unmet
                                |> Seq.fold (fun (mapping: Map<string, string>) id ->
                                    mapping.Add(id, receipt.ReceiptId)) work.WaivedDependencies
                            | None -> work.WaivedDependencies
                        let finalized =
                            { work with State = WorkState.Completed
                                        Gate = GateState.Clear
                                        Domain = DomainState.Integrated remoteCommit
                                        IntegrationAuthorized = true
                                        WaivedDependencies = waived }
                        { current with
                            Works = current.Works.Add(workId, finalized)
                            ProducerCount = current.ProducerCount + 1 }
                        |> projectDependenciesChanged now
                | _ -> Error Rejection.MissingProof
            | _ -> Error(Rejection.InvalidState "no observed gate exception")) project

    let finishUnregistered expected op launchId journalSettled deadlinePassed project =
        projectCas expected op ("unregistered-reclaim|" + launchId + "|" + string journalSettled) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch when deadlinePassed && journalSettled ->
                match launch.State with
                | LaunchState.SubmittedUnregistered _ ->
                    Ok { current with
                            Launches = current.Launches.Add(launchId,
                                { launch with State = LaunchState.TerminalPendingRelease("unregistered:" + op)
                                              Version = launch.Version + 1 }) }
                | _ -> Error(Rejection.InvalidState "registered or terminal launch")
            | _ -> Error Rejection.UnsafeExternalEffect) project

    // The caller must obtain original-dispatch reconciliation and (if registered)
    // session terminal evidence outside this pure fixture. No boolean alone is
    // canonical proof; persistence must bind these to original journal/lease IDs.
    let finishInvalidatedLaunch expected op launchId (evidence: InvalidatedTerminalEvidence) project =
        projectCas expected op (fullInput "invalidated-terminal" (launchId, evidence)) (fun current ->
            match current.Launches.TryFind launchId with
            | Some launch ->
                match launch.State with
                | LaunchState.DemandInvalidatedPendingTerminal prior ->
                    let safe =
                        match prior with
                        | LaunchState.ReservedUnregistered -> true
                        | LaunchState.SubmittedUnregistered _ -> evidence.DispatchJournalSettled
                        | LaunchState.Registered _ ->
                            evidence.DispatchJournalSettled && evidence.SessionTerminalVerified
                        | _ -> false
                    if not safe then Error Rejection.UnsafeExternalEffect
                    else
                        let terminal =
                            { launch with
                                State = LaunchState.TerminalPendingRelease("invalidated:" + op)
                                Credit = None
                                Version = launch.Version + 1 }
                        Ok { current with Launches = current.Launches.Add(launchId, terminal) }
                | _ -> Error(Rejection.InvalidState "launch is not demand-invalidated")
            | None -> Error(Rejection.InvalidState "unknown launch")) project

    let releaseGlobal expected op launchId (project: Project) account =
        accountCas expected op ("release|" + launchId) (fun current ->
            match project.Launches.TryFind launchId, current.Tokens.TryFind launchId with
            | Some launch, Some false ->
                match launch.State with
                | LaunchState.TerminalPendingRelease receipt when not (String.IsNullOrWhiteSpace receipt) ->
                    Ok { current with Tokens = current.Tokens.Add(launchId, true) }
                | _ -> Error Rejection.MissingProof
            | Some _, Some true -> Ok current
            | _ -> Error Rejection.MissingProof) account

    let canDispatch (account: Account) (projects: Project list) =
        account.Migration = MigrationEpoch.Activated
        && (projects |> List.forall (fun p ->
            p.Migration = MigrationEpoch.Activated
            && p.QueueCovered && p.GraphCovered && p.MessageCovered && p.SourceCovered))

    // A deliberately small pure bridge fixture: a key belongs to a provider session, never a launch.
    type SessionKeys = Map<string, string>

    let sessionKeyForExecution providerExecutionId freshlyGeneratedKey (keys: SessionKeys) =
        match keys.TryFind providerExecutionId with
        | Some existing -> Ok(keys, existing)
        | None when String.IsNullOrWhiteSpace freshlyGeneratedKey -> Error(Rejection.InvalidState "no entropy")
        | None when keys |> Map.exists (fun _ value -> value = freshlyGeneratedKey) ->
            Error(Rejection.InvalidState "copied provider-session key")
        | None -> Ok(keys.Add(providerExecutionId, freshlyGeneratedKey), freshlyGeneratedKey)
