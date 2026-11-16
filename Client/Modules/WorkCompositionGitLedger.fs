module Client.WorkCompositionGitLedger

open System
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Jarvis.WorkQueues

// The existing project orphan ledger remains authoritative. This record is a
// composition specification/acceptance view, not another execution queue.
type CompositionEnvelope =
    { SchemaVersion: int
      ProjectId: string
      StateHash: string
      Graph: WorkCompositionGraph }

type CompositionCommand =
    { SchemaVersion: int
      ExpectedGraphVersion: int
      Mutation: WorkCompositionMutation }

// Immutable original-WQ04 -> composition spec identity. Written in the same
// original Git CAS as the WQ02 stage; no later caller can rebind its subject.
type OriginalCompositionStageLink =
    { SchemaVersion: int
      ProjectId: string
      WorkId: string
      StageGeneration: int
      SubjectId: string
      UnitId: string
      SpecVersion: int
      SpecDigest: string
      ParentId: string option
      HardPrerequisites: Set<string>
      // Immutable digest of this unit's parent + hard-prerequisite spec
      // closure (not acceptance results). Unrelated graph edits are ignored.
      ContextDigest: string
      GraphVersionAtPublication: int }

type CompositionLinkContext =
    { ParentId: string option
      SpecVersion: int
      SpecDigest: string
      HardPrerequisites: Set<string> }

module WorkCompositionGitLedger =
    [<Literal>]
    let RecordPath = "records/work-composition/graph.json"

    let private codec = CanonicalCodec.encode
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)

    let internal structurallyCoherent (graph: WorkCompositionGraph) =
        let known id = graph.Units.ContainsKey id
        let validProof (unit: CompositeWorkUnit) acceptanceKey (proof: WorkUnitAcceptance) =
            acceptanceKey = string proof.GraphVersion
            && proof.SpecVersion > 0
            && proof.SpecVersion <= unit.SpecVersion
            && proof.GraphVersion > 0
            && proof.GraphVersion <= graph.Version
            && validText proof.EvidenceRef
            && (proof.ChildAcceptances
                |> Map.forall (fun childId version ->
                    match graph.Units.TryFind childId with
                    | None -> false
                    | Some child ->
                        // Cancellation proofs are -SpecVersion; a reopened child
                        // can later be cancelled at version 2 or higher.
                        // Preserve valid historical proof versions.
                        version > 0 || (version < 0 && version >= -child.SpecVersion)))
            && (proof.PrerequisiteAcceptances
                |> Map.forall (fun dependencyId version -> known dependencyId && version > 0))
        graph.Version >= 0
        && (WorkComposition.validate graph |> Result.isOk)
        && (graph.Units
            |> Map.forall (fun key unit ->
                key = unit.Id
                && (unit.Acceptances
                    |> Map.forall (validProof unit))))

    let internal envelope (binding: LedgerBinding) graph =
        { SchemaVersion = 1
          ProjectId = binding.ProjectId.ToString("D")
          StateHash = CanonicalCodec.digest (codec graph)
          Graph = graph }

    // A missing graph is a distinct, empty composition, never an inferred
    // conversion from legacy agent/task state or an empty work-stage queue.
    let loadAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) =
        LedgerStore.readAt binding snapshot RecordPath
        |> Result.bind (function
            | None, None -> Ok(WorkComposition.empty, None)
            | Some raw, Some oid ->
                try
                    let persisted = CanonicalCodec.decode<CompositionEnvelope> raw
                    if persisted.SchemaVersion <> 1
                       || persisted.ProjectId <> binding.ProjectId.ToString("D")
                       || persisted.StateHash <> CanonicalCodec.digest (codec persisted.Graph)
                       || codec persisted <> raw
                       || not (structurallyCoherent persisted.Graph) then
                        Error(LedgerIntegrityError "Composition graph identity, canonical serialization, acceptance or state hash mismatch.")
                    else Ok(persisted.Graph, Some oid)
                with ex ->
                    Error(LedgerIntegrityError ("Malformed composition graph: " + ex.Message))
            | _ -> Error(LedgerIntegrityError "Incomplete composition graph record."))

    [<Literal>]
    let private LinkDirectory = "records/work-composition/stage-links/"

    let internal stageLinkPath (stageId: string) = LinkDirectory + stageId + ".json"

    let internal readStageLinkAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (stageId: string) =
        if not (stageId.StartsWith("wq04-", StringComparison.Ordinal)) then Ok None
        else
            LedgerStore.readAt binding snapshot (stageLinkPath stageId)
            |> Result.bind (function
                | None, None -> Ok None
                | Some raw, Some _ ->
                    try
                        let link = CanonicalCodec.decode<OriginalCompositionStageLink> raw
                        if link.SchemaVersion <> 1
                           || link.ProjectId <> binding.ProjectId.ToString("D")
                           || link.WorkId <> stageId
                           || link.StageGeneration < 1
                           || link.SpecVersion < 1
                           || link.GraphVersionAtPublication < 1
                           || not (validText link.UnitId && validText link.SubjectId)
                           || not (validText link.SpecDigest && validText link.ContextDigest)
                           || CanonicalCodec.encode link <> raw then
                            Error(LedgerIntegrityError "Invalid immutable original composition stage-link identity.")
                        else Ok(Some link)
                    with ex ->
                        Error(LedgerIntegrityError ("Malformed original composition stage link: " + ex.Message))
                | _ -> Error(LedgerIntegrityError "Incomplete composition stage-link record."))

    let internal graphBlocked (graph: WorkCompositionGraph) (unit: CompositeWorkUnit) =
        let rec cancelledParent seen id =
            if Set.contains id seen then true
            else
                match graph.Units.TryFind id with
                | None -> true
                | Some parent ->
                    parent.CancellationReason.IsSome
                    || (parent.ParentId
                        |> Option.exists (cancelledParent (Set.add id seen)))
        unit.CancellationReason.IsSome
        || (unit.ParentId |> Option.exists (cancelledParent Set.empty))
        || (unit.HardPrerequisites
            |> Set.exists (fun id -> WorkComposition.currentAcceptance graph id |> Option.isNone))

    let private contextDigest (graph: WorkCompositionGraph) unitId =
        let rec visit seen pending =
            match pending with
            | [] -> seen
            | id :: remaining when Set.contains id seen -> visit seen remaining
            | id :: remaining ->
                match graph.Units.TryFind id with
                | None -> visit (Set.add id seen) remaining
                | Some unit ->
                    visit (Set.add id seen)
                        ((unit.ParentId |> Option.toList)
                         @ (unit.HardPrerequisites |> Set.toList) @ remaining)
        let context =
            visit Set.empty [unitId]
            |> Seq.choose (fun id ->
                graph.Units.TryFind id
                |> Option.map (fun unit ->
                    id,
                    { ParentId = unit.ParentId
                      SpecVersion = unit.SpecVersion
                      SpecDigest = CanonicalCodec.digest (CanonicalCodec.encode unit.Spec)
                      HardPrerequisites = unit.HardPrerequisites }))
            |> Map.ofSeq
        CanonicalCodec.digest (CanonicalCodec.encode context)

    let internal stageLinkStillCurrent (link: OriginalCompositionStageLink)
                                       (stage: WorkStage) (graph: WorkCompositionGraph) =
        stage.Id = link.WorkId
        && stage.ProjectId = link.ProjectId
        && stage.StageGeneration = link.StageGeneration
        && stage.SubjectId = link.SubjectId
        && (graph.Units.TryFind link.UnitId
            |> Option.exists (fun unit ->
                unit.Spec.LinkedTaskId = Some link.SubjectId
                && unit.Kind = WorkUnitKind.Feature
                && unit.SpecVersion = link.SpecVersion
                && CanonicalCodec.digest (CanonicalCodec.encode unit.Spec) = link.SpecDigest
                && unit.ParentId = link.ParentId
                && unit.HardPrerequisites = link.HardPrerequisites
                && contextDigest graph unit.Id = link.ContextDigest))

    let internal verifyStageLinkAt binding snapshot graph unitId (stage: WorkStage) =
        readStageLinkAt binding snapshot stage.Id
        |> Result.bind (function
            | Some link when link.UnitId = unitId
                             && stageLinkStillCurrent link stage graph ->
                Ok ()
            | _ -> Error(InvalidBinding
                "Composition stage lacks its original immutable WQ04 unit/spec binding; recovery required."))

    // Called from the WQ04 producer's WINNING original project Git CAS.
    // The stage and immutable unit/spec binding are born together. The ordinary
    // source, role and account gates remain unchanged and default-off.
    let internal planOriginalStage binding snapshot (stage: WorkStage) = result {
        if stage.Kind <> WorkKind.Planning then
            return None, []
        else
            let! graph, _ = loadAt binding snapshot
            let linked =
                graph.Units
                |> Map.toList
                |> List.map snd
                |> List.filter (fun unit -> unit.Spec.LinkedTaskId = Some stage.SubjectId)
            match linked with
            | [] -> return None, []
            | [unit] when unit.Kind = WorkUnitKind.Feature ->
                let! previous = readStageLinkAt binding snapshot stage.Id
                if previous.IsSome then
                    return! Error(StaleEntityVersion "Original composition stage link already exists.")
                let link =
                    { SchemaVersion = 1
                      ProjectId = binding.ProjectId.ToString("D")
                      WorkId = stage.Id
                      StageGeneration = stage.StageGeneration
                      SubjectId = stage.SubjectId
                      UnitId = unit.Id
                      SpecVersion = unit.SpecVersion
                      SpecDigest = CanonicalCodec.digest (CanonicalCodec.encode unit.Spec)
                      ParentId = unit.ParentId
                      HardPrerequisites = unit.HardPrerequisites
                      ContextDigest = contextDigest graph unit.Id
                      GraphVersionAtPublication = graph.Version }
                let hold =
                    if graphBlocked graph unit then
                        Some(GateState.CompositionHold(unit.Id, unit.SpecVersion))
                    else None
                return hold,
                       [ { Path = stageLinkPath stage.Id
                           ExpectedBlobOid = None
                           Content = CanonicalCodec.encode link } ]
            | _ ->
                return! Error(InvalidBinding
                    "WQ04 planning stage has an ambiguous or non-feature composition owner.")
    }
    let load binding =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            loadAt binding snapshot |> Result.map (fun (graph, _) -> snapshot, graph))

    let loadOverviews binding =
        LedgerStore.fetch binding
        |> Result.bind (fun snapshot ->
            loadAt binding snapshot
            |> Result.bind (fun (graph, _) ->
                // Queue state, when present, must be read from the exact same
                // verified ledger head. Lack of queue state means Unverified
                // for task-linked units; never invent a runnable stage.
                LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
                |> Result.bind (fun (raw, _) ->
                    match raw with
                    | None -> Ok(WorkComposition.allOverviews graph None)
                    | Some _ ->
                        WorkQueueGitLedger.loadAt binding snapshot
                        |> Result.map (fun queue ->
                            WorkComposition.allOverviews graph (Some queue)))))

    let private rejected error =
        match error with
        | WorkCompositionError.StaleGraph (expected, actual) ->
            StaleEntityVersion (sprintf "Composition graph expected version %d, actual %d." expected actual)
        | WorkCompositionError.StaleUnit unitId ->
            StaleEntityVersion ("Composition specification changed: " + unitId)
        | _ -> InvalidBinding ("Rejected composition transition: " + string error)

    // Any ordinary graph mutation must atomically invalidate preexisting WQ02
    // eligibility if the immutable original unit/spec/parent/task binding has
    // changed OR its current acceptance closure is no longer satisfied.
    // Compare old AND new graph: a rewired LinkedTaskId is not discoverable
    // by searching only the post-mutation graph.
    let private queueRevocations binding snapshot (operationId: Guid)
                                 (graph: WorkCompositionGraph) (nextGraph: WorkCompositionGraph) =
        LedgerStore.readAt binding snapshot WorkQueueGitLedger.RecordPath
        |> Result.bind (fun (raw, queueOid) ->
            match raw with
            | None -> Ok []
            | Some _ ->
                WorkQueueGitLedger.loadAt binding snapshot
                |> Result.bind (fun queue ->
                    let linkedIn (view: WorkCompositionGraph) subject =
                        view.Units
                        |> Map.exists (fun _ unit -> unit.Spec.LinkedTaskId = Some subject)
                    let revocation (stage: WorkStage) = result {
                        if stage.Kind <> WorkKind.Planning || stage.QueueId <> "planning" then
                            return false, None
                        else
                            let oldLink = linkedIn graph stage.SubjectId
                            let newLink = linkedIn nextGraph stage.SubjectId
                            let! link = readStageLinkAt binding snapshot stage.Id
                            if not (oldLink || newLink || link.IsSome) then
                                return false, None
                            else
                                match link with
                                | Some original ->
                                    return (not (stageLinkStillCurrent original stage nextGraph)
                                            || (nextGraph.Units.TryFind original.UnitId
                                                |> Option.exists (graphBlocked nextGraph))),
                                           Some original
                                | None ->
                                    // Pre-gate or newly attached stages cannot
                                    // acquire an invented immutable association.
                                    return true, None
                    }
                    let affected =
                        (Ok [], queue.Works |> Map.toList |> List.map snd)
                        ||> List.fold (fun prior stage ->
                            prior |> Result.bind (fun selected ->
                                revocation stage
                                |> Result.bind (fun (broken, link) ->
                                    if not broken then
                                        Ok selected
                                    elif stage.State <> WorkState.Available
                                         && stage.State <> WorkState.Blocked then
                                        Error(InvalidBinding
                                            "Composition revision requires recovery of a claimed/live WQ02 stage.")
                                    else
                                        match link with
                                        | Some original ->
                                            let desired = GateState.CompositionHold(original.UnitId, original.SpecVersion)
                                            // Even an already-Blocked WQ02 stage must
                                            // acquire the composition hold, otherwise
                                            // an unrelated queue dependency wake can
                                            // resurrect stale graph eligibility.
                                            if stage.State = WorkState.Blocked && stage.Gate = desired then
                                                Ok selected
                                            elif stage.Gate = GateState.Clear
                                                 || stage.Gate = desired then
                                                Ok((stage, original) :: selected)
                                            else
                                                Error(InvalidBinding
                                                    "Cannot replace a distinct WQ02 gate without recovery.")
                                        | None -> Error(InvalidBinding
                                            "WQ02 stage lacks immutable original composition binding; reconcile before graph mutation."))))
                    affected
                    |> Result.bind (fun stages ->
                        if stages.IsEmpty then Ok []
                        elif queue.Migration <> MigrationEpoch.Frozen
                             || not queue.Launches.IsEmpty then
                            Error(InvalidBinding
                                "Composition revision cannot revoke live or non-Frozen WQ02 eligibility.")
                        else
                            let works =
                                (queue.Works, stages)
                                ||> List.fold (fun current (stage, link) ->
                                    current.Add(stage.Id,
                                        { stage with State = WorkState.Blocked
                                                     Gate = GateState.CompositionHold(link.UnitId, link.SpecVersion)
                                                     AvailableAt = 0L
                                                     AvailableSequence = 0L }))
                            let next =
                                { queue with Version = queue.Version + 1
                                             Works = works
                                             Receipts = queue.Receipts.Add(operationId.ToString("D"),
                                                 ProjectOperationPayload.OtherMutation "Composition.AutoRevoke") }
                            WorkQueueGitLedger.validatedContent binding next
                            |> Result.map (fun content ->
                                [ { Path = WorkQueueGitLedger.RecordPath
                                    ExpectedBlobOid = queueOid; Content = content } ]))))

    // Evidence is a reference to existing canonical review/integration/test
    // receipts, never an unverified caller assertion. Normal mutation rejects
    // acceptance until its verifier is bound into the same Git-CAS planner.
    let private transactWithVerifier binding actor operationId expectedVersion mutation verifier =
        let input =
            { SchemaVersion = 1
              ExpectedGraphVersion = expectedVersion
              Mutation = mutation }
        let intent: LedgerIntent =
            { OperationId = operationId
              Actor = actor
              Kind = "Composition.Mutate"
              RequestJson = codec input }
        LedgerStore.transact binding intent (fun snapshot ->
            loadAt binding snapshot
            |> Result.bind (fun (graph, oid) ->
                let evidenceCheck =
                    match mutation, verifier with
                    | WorkCompositionMutation.Accept (id, _, receipt), Some verify ->
                        verify snapshot graph id receipt
                    | WorkCompositionMutation.Accept _, None ->
                        Error(InvalidBinding "Composition acceptance requires independently verified canonical evidence.")
                    | _ -> Ok ()
                evidenceCheck
                |> Result.bind (fun () ->
                    WorkComposition.apply expectedVersion mutation graph
                    |> Result.mapError rejected
                    |> Result.bind (fun next ->
                        if not (structurallyCoherent next) then
                            Error(LedgerIntegrityError "Composition candidate failed structural integrity checks.")
                        else
                            queueRevocations binding snapshot operationId graph next
                            |> Result.map (fun revocations ->
                                { Path = RecordPath; ExpectedBlobOid = oid
                                  Content = codec (envelope binding next) } :: revocations)))))

    let execute binding actor (operationId: Guid) expectedVersion mutation =
        transactWithVerifier binding actor operationId expectedVersion mutation None

    // Intended for the canonical task/review/integration adapter, not model
    // clients. Callback must verify a receipt at the *same* ledger snapshot.
    let internal executeVerifiedAcceptance binding actor (operationId: Guid) expectedVersion
                                        (unitId: string) specVersion receipt verifier =
        transactWithVerifier binding actor operationId expectedVersion
            (WorkCompositionMutation.Accept(unitId, specVersion, receipt)) (Some verifier)
