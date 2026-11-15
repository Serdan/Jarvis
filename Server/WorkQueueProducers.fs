namespace Jarvis.WorkQueues

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

// WQ-04 remains a pure, inert domain-to-queue projection. Its inputs must be
// read from one verified canonical project-head/source snapshot by a future
// producer adapter. Neither these helpers nor their tests launch a Scheduler.
[<RequireQualifiedAccess>]
type QueueProducerOrigin =
    | TaskRequested of taskId: string
    | ImplementationReady of taskId: string * lineage: SourceLineage
    | FrozenReviewRequested of taskId: string * reviewId: string * frozenSha: string * frozenRef: string
    | ReviewCorrection of taskId: string * reviewId: string * lineage: SourceLineage
    | AcceptedIntegration of taskId: string * reviewId: string * acceptedSha: string * targetRef: string
    | ChallengeFollowUp of taskId: string * challengeId: string
    | IntegrationPolicyResolved of taskId: string * priorIntegrationWorkId: string * receipt: string
    | OperatorMessage of SubjectMessage

/// SourceId identifies one immutable authoritative event, not a mutable task.
/// The generation is separately pinned, so replacements do not reuse old work.
type QueueProducerEvent =
    { ProjectId: string
      SourceId: string
      StageGeneration: int
      Origin: QueueProducerOrigin
      HardPrerequisites: Set<string> }

[<RequireQualifiedAccess>]
type ProducedProjection =
    | Work of WorkStage
    | RoleMessage of SubjectMessage * WorkStage
    | PassiveTaskAttention of SubjectMessage

/// Read-only verified high-water witness. The caller must derive every field
/// from canonical Git heads/indexes; this is never an authorization token.
type ProducerCoverage =
    { ProjectVersion: int
      GraphVersion: int
      ProducerHighWater: int64
      QueueIndexedThrough: int64
      MessageHighWater: int64
      MessageIndexedThrough: int64
      SourceHighWater: int64
      SourceIndexedThrough: int64
      RoleCatalogRevision: int
      ProjectHeadVerified: bool
      CrossProjectCovered: bool }

/// An immutable historical role definition. An in-flight launch keeps its
/// pinned Version when a new definition becomes current.
type RoleConfiguration =
    { RoleId: string
      Version: int
      Instructions: string
      QueueSubscriptions: Set<string>
      Enabled: bool }

type RoleCatalog =
    { Revision: int
      Covered: bool
      Current: Map<string, int>
      Versions: Map<string, Map<int, RoleConfiguration>>
      Receipts: Map<string, RoleConfiguration> }

type RankedRoleDemand =
    { WorkId: string
      ProjectId: string
      StageGeneration: int
      QueueId: string
      RoleId: string
      RoleVersion: int
      BlocksUnfinishedStage: bool
      AvailableAt: int64
      AvailableSequence: int64 }

type RoleDemandSelection =
    { Chosen: RankedRoleDemand option
      MatchingSkips: string list
      CycleWarnings: CycleWarning list }

module WorkQueueProducers =
    let private nonBlank (s: string) = not (String.IsNullOrWhiteSpace s)

    let private sourceToken (origin: QueueProducerOrigin) =
        match origin with
        | QueueProducerOrigin.TaskRequested _ -> "planning"
        | QueueProducerOrigin.ImplementationReady _ -> "implementation"
        | QueueProducerOrigin.FrozenReviewRequested _ -> "review"
        | QueueProducerOrigin.ReviewCorrection _ -> "correction"
        | QueueProducerOrigin.AcceptedIntegration _ -> "integration"
        | QueueProducerOrigin.ChallengeFollowUp _ -> "challenge"
        | QueueProducerOrigin.IntegrationPolicyResolved _ -> "policy"
        | QueueProducerOrigin.OperatorMessage _ -> "message"

    // Length-prefix prevents separator ambiguity; hash keeps Git-ref-safe stage
    // IDs even for arbitrary operator message and task identifiers.
    let workId (event: QueueProducerEvent) =
        // A completed integration has exactly one logical policy successor:
        // neither caller-selected SourceId, receipt nor generation is its key.
        let source, generation =
            match event.Origin with
            | QueueProducerOrigin.IntegrationPolicyResolved(_, preceding, _) -> preceding, 1
            | _ -> event.SourceId, event.StageGeneration
        let token = sourceToken event.Origin
        let identity = sprintf "%d:%s%d:%s%d:%s" event.ProjectId.Length event.ProjectId
                           source.Length source token.Length token
        let digest =
            SHA256.HashData(Encoding.UTF8.GetBytes identity)
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()
        sprintf "wq04-%s-%s-g%d" token digest generation

    let private validSha (sha: string) =
        nonBlank sha && (sha.Length = 40 || sha.Length = 64)
        && (sha |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let private validRef (sourceRef: string) =
        nonBlank sourceRef
        && sourceRef.StartsWith("refs/", StringComparison.Ordinal)
        && not (sourceRef.Contains("..", StringComparison.Ordinal))
        && not (sourceRef.Contains(' '))

    let private validLineage (lineage: SourceLineage) =
        nonBlank lineage.RepositoryId
        && nonBlank lineage.TaskId
        && nonBlank lineage.LineageId
        && validRef lineage.TargetRef
        && validSha lineage.PinnedBaseCommit
        && lineage.Generation = 1
        && lineage.ReachableRef.IsNone
        && lineage.ContinuationSha.IsNone

    let private completeProjectGraph (project: Project) =
        WorkQueueDependencyDiagnostics.analyze project.GraphVersion
            project.GraphCovered project.Works
        |> Result.mapError Rejection.IncompleteCoverage

    let verifyCoverage (project: Project) (catalog: RoleCatalog) (coverage: ProducerCoverage) =
        if not coverage.ProjectHeadVerified || not coverage.CrossProjectCovered then
            Error(Rejection.IncompleteCoverage "unverified project/cross-project producer head")
        elif coverage.ProjectVersion <> project.Version || coverage.GraphVersion <> project.GraphVersion then
            Error(Rejection.IncompleteCoverage "source/index generation does not match pinned project head")
        elif coverage.RoleCatalogRevision <> catalog.Revision || not catalog.Covered then
            Error(Rejection.IncompleteCoverage "role configuration version/index")
        elif coverage.ProducerHighWater < 0L
             || coverage.ProducerHighWater <> coverage.QueueIndexedThrough then
            Error(Rejection.IncompleteCoverage "producer queue high-water/index")
        elif coverage.MessageHighWater < 0L
             || coverage.MessageHighWater <> coverage.MessageIndexedThrough then
            Error(Rejection.IncompleteCoverage "operator message high-water/index")
        elif coverage.SourceHighWater < 0L
             || coverage.SourceHighWater <> coverage.SourceIndexedThrough then
            Error(Rejection.IncompleteCoverage "source/effect high-water/index")
        elif not project.QueueCovered || not project.MessageCovered || not project.SourceCovered then
            Error(Rejection.IncompleteCoverage "project queue/message/source coverage")
        else
            completeProjectGraph project
            |> Result.bind (fun _ ->
                // Equal caller assertions do not witness the source-event,
                // queue and message indexes at a pinned canonical Git head.
                // No durable CAS-visible role catalog exists either. Do not
                // infer a healthy empty queue or grant launch demand.
                Error(Rejection.IncompleteCoverage
                    "WQ-04 authoritative source/index and pinned role admission proof unavailable"))

    /// Parse one authoritative event to its deterministic, immutable projection.
    /// Upstream task/review/challenge source verification must precede this call.
    let projectEvent (project: Project) (event: QueueProducerEvent) =
        if not (nonBlank event.ProjectId)
           || not (nonBlank event.SourceId)
           || event.StageGeneration <= 0
           || (event.HardPrerequisites |> Set.exists (nonBlank >> not))
           || (project.Works |> Map.exists (fun _ stage -> stage.ProjectId <> event.ProjectId)) then
            Error(Rejection.InvalidState "invalid producer identity, generation or project binding")
        elif event.HardPrerequisites |> Set.exists (fun id -> not (project.Works.ContainsKey id)) then
            Error(Rejection.IncompleteCoverage "producer hard prerequisite stage not indexed")
        else
            let stageId = workId event
            let make kind queue subject lineage =
                { Id = stageId
                  ProjectId = event.ProjectId
                  SubjectId = subject
                  Kind = kind
                  QueueId = queue
                  StageGeneration = event.StageGeneration
                  State = WorkState.Available
                  Domain = DomainState.Undecided
                  Gate = GateState.Clear
                  ClaimGeneration = 0
                  Dependencies = event.HardPrerequisites
                  WaivedDependencies = Map.empty
                  AvailableAt = 0L
                  AvailableSequence = 0L
                  SourceLineage = lineage
                  Push = None
                  IntegrationAuthorized = false }
            match event.Origin with
            | QueueProducerOrigin.TaskRequested task when nonBlank task ->
                Ok(ProducedProjection.Work(make WorkKind.Planning "planning" task None))
            | QueueProducerOrigin.ImplementationReady(task, lineage)
                when nonBlank task && lineage.TaskId = task && validLineage lineage ->
                Ok(ProducedProjection.Work(make WorkKind.Implementation "implementation" task (Some lineage)))
            | QueueProducerOrigin.FrozenReviewRequested(task, reviewId, sha, sourceRef)
                when nonBlank task && nonBlank reviewId && validSha sha && validRef sourceRef ->
                Ok(ProducedProjection.Work(make WorkKind.FrozenReview "review" task None))
            | QueueProducerOrigin.ReviewCorrection(task, reviewId, lineage)
                when nonBlank task && nonBlank reviewId && lineage.TaskId = task && validLineage lineage
                     && (event.HardPrerequisites |> Set.exists (fun dep ->
                         match project.Works.TryFind dep with
                         | Some review -> review.Kind = WorkKind.FrozenReview
                         | None -> false)) ->
                Ok(ProducedProjection.Work(make WorkKind.Implementation "implementation" task (Some lineage)))
            | QueueProducerOrigin.AcceptedIntegration(task, reviewId, sha, target)
                when nonBlank task && nonBlank reviewId && validSha sha && validRef target
                     && (event.HardPrerequisites |> Set.exists (fun dep ->
                         match project.Works.TryFind dep with
                         | Some review -> review.Kind = WorkKind.FrozenReview
                         | None -> false)) ->
                Ok(ProducedProjection.Work(make WorkKind.Integration "integration" task None))
            | QueueProducerOrigin.ChallengeFollowUp(task, challenge)
                when nonBlank task && nonBlank challenge ->
                // Challenge triage is planning-like, but has its own explicit
                // subscribed queue; it never silently unblocks the subject.
                Ok(ProducedProjection.Work(make WorkKind.Planning "challenge" task None))
            | QueueProducerOrigin.IntegrationPolicyResolved(task, oldWork, receipt)
                when nonBlank task && nonBlank oldWork && nonBlank receipt
                     && event.StageGeneration = 1
                     && event.HardPrerequisites.Contains oldWork
                     && (match project.Works.TryFind oldWork with
                         | Some previous ->
                             previous.Kind = WorkKind.Integration
                             && previous.SubjectId = task
                             && previous.State = WorkState.Completed
                             && previous.Gate = GateState.Clear
                             && previous.IntegrationAuthorized
                             && (match previous.Domain with
                                 | DomainState.Integrated _ -> true
                                 | _ -> false)
                         | None -> false) ->
                // No second push or second integration. Resolve the workflow
                // policy exactly once as an explicit successor planning stage.
                Ok(ProducedProjection.Work(make WorkKind.Planning "planning" task None))
            | QueueProducerOrigin.OperatorMessage message
                when nonBlank message.Id && nonBlank message.Body
                     && message.State = MessageState.Pending
                     && message.HandlerGeneration = 0 && message.WorkId.IsNone
                     && event.HardPrerequisites.IsEmpty ->
                match message.Subject with
                | Subject.Task task when nonBlank task ->
                    Ok(ProducedProjection.PassiveTaskAttention message)
                | Subject.Role role when nonBlank role && role <> "Manager"
                    && (project.Subscriptions.TryFind role
                        |> Option.exists (fun queues -> queues.Contains("role:" + role))) ->
                    Ok(ProducedProjection.RoleMessage(message,
                        make (WorkKind.RoleMessage message.Id) ("role:" + role) role None))
                | _ -> Error(Rejection.InvalidState "invalid operator-message subject")
            | _ -> Error(Rejection.InvalidState "producer lacks exact canonical event evidence")

    /// Pure project-CAS *fixture*. The actual Git mutation remains disabled
    /// until a canonical event resolver and role-catalog CAS source exist.
    /// Retain complete event semantics, not only the derived work stage, for
    /// same-operation replay. A changed SourceId/payload must be rejected.
    let applyEvent expectedVersion operationId now (event: QueueProducerEvent) (project: Project) =
        let semanticReceipt =
            ProjectOperationPayload.OtherMutation(sprintf "wq04-fixture-event|%A" event)
        match project.Receipts.TryFind operationId with
        | Some original when original = semanticReceipt -> Ok project
        | Some _ -> Error Rejection.OperationIdReused
        | None when project.Version <> expectedVersion -> Error Rejection.CasConflict
        | None when not project.QueueCovered || not project.GraphCovered || not project.MessageCovered || not project.SourceCovered ->
            Error(Rejection.IncompleteCoverage "producer source/index coverage")
        | None ->
            completeProjectGraph project
            |> Result.bind (fun _ -> projectEvent project event)
            |> Result.bind (fun projection ->
                let stamp stage =
                    let unsatisfied =
                        stage.Dependencies |> Set.exists (fun id -> not (Contracts.isSatisfied project id))
                    if unsatisfied then { stage with State = WorkState.Blocked }
                    else { stage with AvailableAt = now; AvailableSequence = project.NextAvailability + 1L }
                match projection with
                | ProducedProjection.Work stage ->
                    Contracts.publishStage expectedVersion operationId (stamp stage) project
                | ProducedProjection.RoleMessage(message, stage) ->
                    Contracts.postMessage expectedVersion operationId message (Some(stamp stage)) project
                | ProducedProjection.PassiveTaskAttention message ->
                    Contracts.postMessage expectedVersion operationId message None project)
            |> Result.map (fun next ->
                { next with Receipts = next.Receipts.Add(operationId, semanticReceipt) })

    let emptyRoleCatalog =
        { Revision = 0; Covered = true
          Current = Map.empty; Versions = Map.empty; Receipts = Map.empty }

    let configureRole expectedCatalogVersion expectedPreviousVersion operationId
                      (definition: RoleConfiguration) (catalog: RoleCatalog) =
        match catalog.Receipts.TryFind operationId with
        | Some prior when prior = definition -> Ok catalog
        | Some _ -> Error Rejection.OperationIdReused
        | None when catalog.Revision <> expectedCatalogVersion -> Error Rejection.CasConflict
        | None when not catalog.Covered -> Error(Rejection.IncompleteCoverage "role catalog")
        | None when not (nonBlank operationId) || not (nonBlank definition.RoleId)
                    || definition.RoleId = "Manager"
                    || not (nonBlank definition.Instructions)
                    || definition.QueueSubscriptions.IsEmpty
                    || (definition.QueueSubscriptions |> Set.exists (nonBlank >> not))
                    || definition.Version <> expectedPreviousVersion + 1
                    || (catalog.Current.TryFind definition.RoleId |> Option.defaultValue 0) <> expectedPreviousVersion ->
            Error(Rejection.InvalidState "non-monotonic or invalid role configuration")
        | None ->
            let history =
                catalog.Versions.TryFind definition.RoleId
                |> Option.defaultValue Map.empty
                |> Map.add definition.Version definition
            Ok { catalog with
                    Revision = catalog.Revision + 1
                    Current = catalog.Current.Add(definition.RoleId, definition.Version)
                    Versions = catalog.Versions.Add(definition.RoleId, history)
                    Receipts = catalog.Receipts.Add(operationId, definition) }

    let private pinnedRole (catalog: RoleCatalog) roleId version =
        match catalog.Versions.TryFind roleId with
        | Some history ->
            match history.TryFind version with
            | Some definition when definition.RoleId = roleId && definition.Version = version ->
                Ok definition
            | _ -> Error(Rejection.IncompleteCoverage "missing historical role configuration")
        | None -> Error(Rejection.IncompleteCoverage "missing historical role configuration")

    let private eligibleRole (project: Project) (definition: RoleConfiguration) (work: WorkStage) =
        definition.Enabled && definition.QueueSubscriptions.Contains work.QueueId
        && (match work.Kind with
            | WorkKind.RoleMessage messageId ->
                match project.Messages.TryFind messageId with
                | Some message ->
                    message.Subject = Subject.Role definition.RoleId
                    && message.State = MessageState.Pending
                    && message.WorkId = Some work.Id
                    && work.QueueId = "role:" + definition.RoleId
                | None -> false
            | _ -> true)

    let private blockingUnfinished (project: Project) (work: WorkStage) =
        project.Works
        |> Map.exists (fun _ dependent ->
            Contracts.isUnfinished dependent
            && dependent.Dependencies.Contains work.Id
            && not (dependent.WaivedDependencies.ContainsKey work.Id)
            && not (Contracts.isSatisfied project work.Id))

    /// A provisional role launch is feasible only when every still-live,
    /// unclaimed launch can be injectively rematched after reserving its work.
    /// It uses immutable historical subscriptions for older pinned sessions.
    let private hasMatching (project: Project) (roles: Map<string, RoleConfiguration>)
                            (remaining: WorkStage list) (launches: Launch list) =
        let owners = Dictionary<string, string>(StringComparer.Ordinal)
        let lookup = launches |> List.map (fun launch -> launch.Id, launch) |> Map.ofList
        let rec augment launchId (seen: HashSet<string>) =
            let launch = lookup.[launchId]
            let role = roles.[launchId]
            remaining
            |> List.filter (eligibleRole project role)
            |> List.exists (fun work ->
                if not (seen.Add work.Id) then false
                else
                    match owners.TryGetValue work.Id with
                    | false, _ -> owners.[work.Id] <- launchId; true
                    | true, other when augment other seen ->
                        owners.[work.Id] <- launchId
                        true
                    | _ -> false)
        launches
        |> List.sortBy (fun launch -> launch.Id)
        |> List.forall (fun launch -> augment launch.Id (HashSet<string>(StringComparer.Ordinal)))

    let selectRoleDemand (coverage: ProducerCoverage) (catalog: RoleCatalog)
                         (project: Project) roleId =
        verifyCoverage project catalog coverage
        |> Result.bind (fun () ->
            match catalog.Current.TryFind roleId with
            | None -> Error Rejection.NoCompatibleMatching
            | Some version -> pinnedRole catalog roleId version)
        |> Result.bind (fun role ->
            Contracts.orderAvailable project
            |> Result.bind (fun ordered ->
                let live =
                    project.Launches
                    |> Map.toList |> List.map snd
                    |> List.filter (fun launch ->
                        not launch.EverClaimed
                        && (match launch.State with
                            | LaunchState.DemandInvalidatedPendingTerminal _
                            | LaunchState.TerminalPendingRelease _ -> false
                            | _ -> true))
                let roleResults =
                    live
                    |> List.map (fun launch ->
                        pinnedRole catalog launch.RoleId launch.RoleVersion
                        |> Result.map (fun definition -> launch.Id, definition))
                let missing = roleResults |> List.tryPick (function Error reason -> Some reason | _ -> None)
                match missing with
                | Some reason -> Error reason
                | None ->
                    let pinned = roleResults |> List.choose (function Ok pair -> Some pair | _ -> None) |> Map.ofList
                    let diagnostics =
                        WorkQueueDependencyDiagnostics.analyze project.GraphVersion true project.Works
                    match diagnostics with
                    | Error reason -> Error(Rejection.IncompleteCoverage reason)
                    | Ok graph ->
                        let eligible = ordered |> List.filter (eligibleRole project role)
                        let rec choose skipped (remaining: WorkStage list) =
                            match remaining with
                            | [] ->
                                Ok { Chosen = None
                                     MatchingSkips = List.rev skipped
                                     CycleWarnings = graph.CycleWarnings }
                            | (stage: WorkStage) :: rest ->
                                let others = ordered |> List.filter (fun candidate -> candidate.Id <> stage.Id)
                                if hasMatching project pinned others live then
                                    Ok { Chosen = Some {
                                            WorkId = stage.Id; ProjectId = stage.ProjectId
                                            StageGeneration = stage.StageGeneration
                                            QueueId = stage.QueueId; RoleId = role.RoleId
                                            RoleVersion = role.Version
                                            BlocksUnfinishedStage = blockingUnfinished project stage
                                            AvailableAt = stage.AvailableAt
                                            AvailableSequence = stage.AvailableSequence }
                                         MatchingSkips = List.rev skipped
                                         CycleWarnings = graph.CycleWarnings }
                                else
                                    choose (stage.Id :: skipped) rest
                        choose [] eligible))
