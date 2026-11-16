module Client.WorkCompositionGitLedger

open System
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

module WorkCompositionGitLedger =
    [<Literal>]
    let RecordPath = "records/work-composition/graph.json"

    let private codec = CanonicalCodec.encode
    let private validText (value: string) = not (String.IsNullOrWhiteSpace value)

    let private structurallyCoherent (graph: WorkCompositionGraph) =
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

    let private envelope (binding: LedgerBinding) graph =
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
                            Ok [ { Path = RecordPath
                                   ExpectedBlobOid = oid
                                   Content = codec (envelope binding next) } ]))))

    let execute binding actor (operationId: Guid) expectedVersion mutation =
        transactWithVerifier binding actor operationId expectedVersion mutation None

    // Intended for the canonical task/review/integration adapter, not model
    // clients. Callback must verify a receipt at the *same* ledger snapshot.
    let internal executeVerifiedAcceptance binding actor (operationId: Guid) expectedVersion
                                        (unitId: string) specVersion receipt verifier =
        transactWithVerifier binding actor operationId expectedVersion
            (WorkCompositionMutation.Accept(unitId, specVersion, receipt)) (Some verifier)
