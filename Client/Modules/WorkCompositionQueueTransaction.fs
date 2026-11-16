module Client.WorkCompositionQueueTransaction

open System
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.WorkCompositionGitLedger
open Client.WorkCompositionAcceptanceAdapter
open Client.WorkQueueGitLedger
open Jarvis.WorkQueues

// A strictly frozen, project-only rehearsal. This proves a graph + WQ02
// stage transition in one ORIGINAL project-ledger Git CAS, but NOT WQ07
// account admission or WQ03 role/source launch authority. Do not register
// this with any scheduler until the ordinary writers also enforce this gate.
type FrozenCompositionStageRequest =
    { ProjectId: Guid
      SnapshotHead: string
      GraphVersion: int
      ExpectedProjectVersion: int
      AcceptedUnitId: string
      AcceptedSpecVersion: int
      DependentUnitId: string
      DependentSpecVersion: int
      WorkId: string
      StageGeneration: int
      OriginOid: string
      IntegrationSubmissionId: Guid }

module WorkCompositionQueueTransaction =
    let private reject text = InvalidBinding ("WorkCompositionQueue RecoveryBlocked: " + text)
    let private stale text = StaleEntityVersion ("WorkCompositionQueue: " + text)
    let private nonBlank (text: string) = not (String.IsNullOrWhiteSpace text)
    let private require<'t> (item: 't option) error : Result<'t, LedgerFailure> =
        match item with
        | Some value -> Ok value
        | None -> Error error


    let private sameHead (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                         (request: FrozenCompositionStageRequest) =
        if request.ProjectId <> binding.ProjectId then
            Error(reject "project identity is not the trusted ledger binding")
        elif not (nonBlank request.SnapshotHead) || request.SnapshotHead <> snapshot.Commit then
            Error(stale "source Git head changed; use a fresh source-operation UUID")
        elif request.GraphVersion < 1 || request.ExpectedProjectVersion < 1
             || request.StageGeneration < 1 || request.AcceptedSpecVersion < 1
             || request.DependentSpecVersion < 1
             || not (nonBlank request.AcceptedUnitId)
             || not (nonBlank request.DependentUnitId)
             || not (nonBlank request.WorkId) || not (nonBlank request.OriginOid) then
            Error(reject "invalid work, origin, or positive version identity")
        elif request.AcceptedUnitId = request.DependentUnitId then
            Error(reject "accepted prerequisite must differ from dependent work")
        else Ok ()

    let private load (binding: LedgerBinding) snapshot request = result {
        do! sameHead binding snapshot request
        let! graph, graphOid = WorkCompositionGitLedger.loadAt binding snapshot
        let! queue = WorkQueueGitLedger.loadAt binding snapshot
        let! _, queueOid = LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
        if graph.Version <> request.GraphVersion
           || queue.Version <> request.ExpectedProjectVersion then
            return! Error(stale "graph or WQ02 version changed")
        if queue.Migration <> MigrationEpoch.Frozen || not queue.Launches.IsEmpty then
            return! Error(reject "non-frozen project or existing account/host launch")
        let! prerequisite =
            graph.Units.TryFind request.AcceptedUnitId
            |> fun item -> require (item: CompositeWorkUnit option) (reject "accepted graph prerequisite is absent")
        let! dependent =
            graph.Units.TryFind request.DependentUnitId
            |> fun item -> require (item: CompositeWorkUnit option) (reject "dependent graph unit is absent")
        if prerequisite.SpecVersion <> request.AcceptedSpecVersion
           || dependent.SpecVersion <> request.DependentSpecVersion
           || dependent.CancellationReason.IsSome
           || not (dependent.HardPrerequisites.Contains request.AcceptedUnitId) then
            return! Error(reject "graph spec/edge changed or dependent cancelled")
        let! stage =
            queue.Works.TryFind request.WorkId
            |> fun item -> require (item: WorkStage option) (reject "original WQ02 stage missing")
        if stage.ProjectId <> binding.ProjectId.ToString("D")
           || stage.SubjectId <> (dependent.Spec.LinkedTaskId |> Option.defaultValue "")
           || stage.Kind <> WorkKind.Planning
           || stage.QueueId <> "planning"
           || stage.StageGeneration <> request.StageGeneration
           || stage.ClaimGeneration <> 0
           || (match stage.State, stage.Gate with
               | WorkState.Blocked, GateState.CompositionHold (unitId, version)
                    when unitId = dependent.Id && version = dependent.SpecVersion -> false
               | WorkState.Available, GateState.Clear -> false
               | _ -> true)
           || stage.Domain <> DomainState.Undecided
           || stage.Push.IsSome || stage.SourceLineage.IsSome
           || stage.IntegrationAuthorized || not stage.WaivedDependencies.IsEmpty then
            return! Error(reject "original WQ04 stage or side-effect fence changed")
        // The source stage must have been atomically born with this exact
        // feature, linked task, spec and prerequisite closure. A later graph
        // rewrite cannot silently rebind a previously released generation.
        do! WorkCompositionGitLedger.verifyStageLinkAt
                binding snapshot graph dependent.Id stage
        return graph, graphOid, queue, queueOid, prerequisite, dependent, stage
    }

    // The original WQ04 TaskCreated producer has NO role transition and no
    // source checkout. That is a legitimate project-only *planning* stage,
    // NOT proof of selected role, source-capable execution, or WQ07 credit.
    // Verify the complete immutable source operation rather than filling these
    // blank fields or accepting a caller-authored origin.
    let private originalPlanningOrigin (binding: LedgerBinding) snapshot request
                                       (stage: WorkStage) = result {
        let! maybeOrigin = WorkQueueGitLedger.readOriginAt binding snapshot stage.Id
        let! origin, oid =
            require maybeOrigin (reject "missing original WQ04 stage origin")
        if oid <> request.OriginOid || origin.SchemaVersion <> 1
           || origin.ProjectId <> binding.ProjectId.ToString("D")
           || origin.WorkId <> stage.Id
           || origin.SourceOperationKind <> "CreateTask"
           || origin.SourceEntityPath <> ("tasks/" + stage.SubjectId + "/task.json")
           || origin.RoleId <> "" || origin.RoleVersion <> 0
           || origin.SourceRef <> "" || origin.SourceCommit <> ""
           || origin.AccountCreditStatus <> "NotReserved" then
            return! Error(reject "original WQ04 project-only lineage or pinned origin OID mismatch")
        let! sourceOpId =
            match Guid.TryParse origin.SourceOperationId with
            | true, id when id <> Guid.Empty -> Ok id
            | _ -> Error(reject "missing original WQ04 source operation UUID")
        let! maybeOperation = LedgerStore.getOperationAt binding snapshot sourceOpId
        let! operation =
            require (maybeOperation: LedgerOperation option) (reject "original WQ04 source operation absent")
        if operation.OperationId <> origin.SourceOperationId
           || operation.Type <> origin.SourceOperationKind
           || operation.Actor <> origin.SourceOperationActor
           || operation.RequestHash <> origin.SourceRequestHash
           || operation.Sequence <> origin.SourceOperationSequence then
            return! Error(reject "original WQ04 request hash/actor/sequence mismatch")
        let! originHead = LedgerStore.getOperationOriginAt binding snapshot operation
        if originHead <> origin.SourceOriginHead then
            return! Error(reject "original WQ04 operation-origin commit differs")
        let! historical = LedgerStore.verifiedHistoricalSnapshot binding originHead
        let! raw, originalOid = LedgerStore.readAt binding historical origin.SourceEntityPath
        match raw, originalOid with
        | Some content, Some found when found = origin.SourceEntityBlobOid
                                        && CanonicalCodec.digest content = origin.SourceEntitySha256 ->
            return ()
        | _ -> return! Error(reject "original WQ04 source entity is not historically verified")
    }

    let private queueUpdate binding (queue: Project) (operationId: Guid)
                            (stage: WorkStage) (nextStage: WorkStage) =
        let next =
            { queue with
                Version = queue.Version + 1
                Works = queue.Works.Add(stage.Id, nextStage)
                NextAvailability =
                    if nextStage.State = WorkState.Available
                       then queue.NextAvailability + 1L
                       else queue.NextAvailability
                Receipts = queue.Receipts.Add(
                    operationId.ToString("D"),
                    ProjectOperationPayload.OtherMutation(
                        "WorkCompositionQueue.FrozenStage|" + stage.Id
                        + "|" + string stage.StageGeneration)) }
        WorkQueueGitLedger.validatedContent binding next

    // Separate, explicit gate-installation operation BEFORE accepting a child.
    // It never authorizes a stage and uses the same canonical Git ledger.
    // This is not a substitute for adapting every ordinary WQ04 producer.
    let holdBlocked (binding: LedgerBinding) actor (operationId: Guid)
                    (request: FrozenCompositionStageRequest) =
        let intent =
            { OperationId = operationId; Actor = actor
              Kind = "WorkCompositionQueue.FrozenHold"
              RequestJson = System.Text.Json.JsonSerializer.Serialize request }
        LedgerStore.transact binding intent (fun snapshot -> result {
            let! graph, _, queue, queueOid, _, dependent, stage =
                load binding snapshot request
            do! originalPlanningOrigin binding snapshot request stage
            if stage.State <> WorkState.Available then
                return! Error(reject "hold requires an unclaimed available original stage")
            if dependent.HardPrerequisites
               |> Set.forall (fun id -> WorkComposition.currentAcceptance graph id |> Option.isSome) then
                return! Error(reject "hold requires a real unmet composition prerequisite")
            let blocked =
                { stage with State = WorkState.Blocked
                             Gate = GateState.CompositionHold(dependent.Id, dependent.SpecVersion)
                             AvailableAt = 0L
                             AvailableSequence = 0L }
            let! content = queueUpdate binding queue operationId stage blocked
            return [ { Path = WorkQueueGitLedger.RecordPath
                       ExpectedBlobOid = queueOid; Content = content } ]
        })

    // One accepted source review/integration and one ORIGINAL Git CAS publish:
    // current graph proof and precisely this blocked, original WQ04 WQ02 stage.
    let acceptAndRelease (binding: LedgerBinding) actor (operationId: Guid)
                         (request: FrozenCompositionStageRequest) =
        let intent =
            { OperationId = operationId; Actor = actor
              Kind = "WorkCompositionQueue.FrozenAcceptAndRelease"
              RequestJson = System.Text.Json.JsonSerializer.Serialize request }
        LedgerStore.transact binding intent (fun snapshot -> result {
            let! graph, graphOid, queue, queueOid, prerequisite, dependent, stage =
                load binding snapshot request
            do! originalPlanningOrigin binding snapshot request stage
            if request.IntegrationSubmissionId = Guid.Empty then
                return! Error(reject "independent integration receipt ID required")
            if stage.State <> WorkState.Blocked
               || (WorkComposition.currentAcceptance graph prerequisite.Id).IsSome then
                return! Error(reject "stage not held or prerequisite was already accepted")
            let receipt = "integration:" + request.IntegrationSubmissionId.ToString("N")
            do! WorkCompositionAcceptanceAdapter.checkAt binding snapshot graph prerequisite.Id receipt
            let! nextGraph =
                WorkComposition.apply graph.Version
                    (WorkCompositionMutation.Accept(
                        prerequisite.Id, prerequisite.SpecVersion, receipt)) graph
                |> Result.mapError (fun e -> reject ("graph rejected independently verified acceptance: " + string e))
            if not (WorkCompositionGitLedger.structurallyCoherent nextGraph) then
                return! Error(reject "graph candidate fails canonical structure")
            if dependent.HardPrerequisites
               |> Set.exists (fun id -> WorkComposition.currentAcceptance nextGraph id |> Option.isNone) then
                return! Error(reject "other hard composition prerequisites remain unaccepted")
            if stage.Dependencies
               |> Set.exists (fun id -> not (Contracts.isSatisfied queue id)) then
                return! Error(reject "independent WQ02 hard-stage prerequisites remain unfulfilled")
            let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let available =
                { stage with State = WorkState.Available
                             Gate = GateState.Clear
                             AvailableAt = now
                             AvailableSequence = queue.NextAvailability + 1L }
            let! queueContent = queueUpdate binding queue operationId stage available
            return
                [ { Path = WorkCompositionGitLedger.RecordPath
                    ExpectedBlobOid = graphOid
                    Content = CanonicalCodec.encode (WorkCompositionGitLedger.envelope binding nextGraph) }
                  { Path = WorkQueueGitLedger.RecordPath
                    ExpectedBlobOid = queueOid; Content = queueContent } ]
        })

    // A correction must invalidate queue eligibility in the very same Git CAS
    // as the graph mutation. This is intentionally a scoped rehearsal seam;
    // unrestricted existing composition mutations remain a migration blocker.
    let revokeOnGraphMutation (binding: LedgerBinding) actor (operationId: Guid)
                              (request: FrozenCompositionStageRequest)
                              (mutation: WorkCompositionMutation) =
        let intent =
            { OperationId = operationId; Actor = actor
              Kind = "WorkCompositionQueue.FrozenMutateAndRevoke"
              RequestJson = System.Text.Json.JsonSerializer.Serialize(
                  {| request = request; mutation = CanonicalCodec.encode mutation |}) }
        LedgerStore.transact binding intent (fun snapshot -> result {
            let! graph, graphOid, queue, queueOid, _, dependent, stage =
                load binding snapshot request
            do! originalPlanningOrigin binding snapshot request stage
            match mutation with
            | WorkCompositionMutation.Accept _ ->
                return! Error(reject "acceptance requires independent integrated verifier")
            | _ -> ()
            let! nextGraph = WorkComposition.apply graph.Version mutation graph
                             |> Result.mapError (fun e -> reject ("invalid graph correction: " + string e))
            if not (WorkCompositionGitLedger.structurallyCoherent nextGraph) then
                return! Error(reject "invalid corrected graph candidate")
            if stage.State <> WorkState.Available && stage.State <> WorkState.Blocked then
                return! Error(reject "claimed/external-effect stage cannot be revoked without settlement")
            let! link = WorkCompositionGitLedger.readStageLinkAt binding snapshot stage.Id
            let nowBlocked =
                match link with
                | None -> true
                | Some original ->
                    not (WorkCompositionGitLedger.stageLinkStillCurrent original stage nextGraph)
                    || (nextGraph.Units.TryFind original.UnitId
                        |> Option.map (WorkCompositionGitLedger.graphBlocked nextGraph)
                        |> Option.defaultValue true)
            // Never auto-awaken on a graph revision: fresh independent
            // review/integration evidence must reauthorize release.
            let! original =
                match link with
                | Some existing -> Ok existing
                | None -> Error(reject "missing immutable composition stage link")
            let blocked = if nowBlocked then
                              { stage with State = WorkState.Blocked
                                           Gate = GateState.CompositionHold(original.UnitId, original.SpecVersion)
                                           AvailableAt = 0L
                                           AvailableSequence = 0L }
                          else stage
            let! queueContent = queueUpdate binding queue operationId stage blocked
            return
                [ { Path = WorkCompositionGitLedger.RecordPath
                    ExpectedBlobOid = graphOid
                    Content = CanonicalCodec.encode (WorkCompositionGitLedger.envelope binding nextGraph) }
                  { Path = WorkQueueGitLedger.RecordPath
                    ExpectedBlobOid = queueOid; Content = queueContent } ]
        })
