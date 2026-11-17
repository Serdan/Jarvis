namespace Jarvis.WorkQueues

open System
open Jarvis.WorkQueues.Contracts

// Stateful Work target: read-only, flat projection from the canonical graph
// and an already version-verified queue snapshot. No SQL, Git or Scheduler I/O.
// Role compatibility and global feasibility are checked by the caller AFTER
// ranking. This projection never grants source/role/claim authorization.
type WorkBlockingScope =
    { ExpectedGraphVersion: int
      ExpectedProjectVersion: int
      // Until required/optional scope is stored in the new graph schema, the
      // verified adapter supplies the optional child IDs in the SAME snapshot.
      OptionalUnitIds: Set<string> }

type RankedWorkCandidate =
    { Stage: WorkStage
      IsBlocking: bool
      LeadPriority: int
      AvailableSequence: int64
      BlockingUnit: string option }

type WorkBlockingProjection =
    { GraphVersion: int
      ProjectVersion: int
      BlockingUnits: Set<string>
      Eligible: RankedWorkCandidate list }

[<RequireQualifiedAccess>]
type WorkBlockingError =
    | StaleGraph of expected: int * actual: int
    | StaleProject of expected: int * actual: int
    | InvalidGraph of WorkCompositionError
    | InvalidScope of string
    | IncompleteQueue of Rejection
    | DependencyDiagnostics of string
    | DependencyCycle of CycleWarning list

[<RequireQualifiedAccess>]
module WorkBlocking =
    let private unfinished (graph: WorkCompositionGraph) id =
        match graph.Units.TryFind id with
        | Some unit when unit.CancellationReason.IsNone ->
            WorkComposition.currentAcceptance graph id |> Option.isNone
        | _ -> false

    let private requiredChildren (graph: WorkCompositionGraph) (optionalUnits: Set<string>) parentId =
        graph.Units
        |> Map.toSeq
        |> Seq.choose (fun (id, unit) ->
            if unit.ParentId = Some parentId
               && not (optionalUnits.Contains id)
               && unfinished graph id then Some id
            else None)
        |> Set.ofSeq

    // Relevance starts ONLY at an explicit, unsatisfied hard-prerequisite edge.
    // From the required prerequisite we walk both its own hard prerequisites
    // and the unfinished required descendants needed for its acceptance.
    // Ordinary containment of a nonblocking parent is NOT a seed.
    let private blockingUnitIds (graph: WorkCompositionGraph) (optionalUnits: Set<string>) =
        let rec cascade visited id =
            if visited |> Set.contains id || not (unfinished graph id) then visited
            else
                let expanded = Set.add id visited
                let unit = graph.Units.[id]
                Set.union unit.HardPrerequisites (requiredChildren graph optionalUnits id)
                |> Set.fold cascade expanded

        graph.Units
        |> Map.toSeq
        |> Seq.choose (fun (id, unit) ->
            if not (unfinished graph id) || optionalUnits.Contains id then None
            else
                unit.HardPrerequisites
                |> Set.filter (unfinished graph)
                |> Some)
        |> Seq.fold (fun active prerequisites -> Set.fold cascade active prerequisites) Set.empty

    let private stageBlocksAnother (project: Project) (stage: WorkStage) =
        project.Works
        |> Map.exists (fun _ dependent ->
            Contracts.isUnfinished dependent
            && dependent.Dependencies.Contains stage.Id
            && not (dependent.WaivedDependencies.ContainsKey stage.Id)
            && not (Contracts.isSatisfied project stage.Id))

    let private unitMayRun graph (unit: CompositeWorkUnit) =
        unfinished graph unit.Id
        && (unit.HardPrerequisites
            |> Set.forall (fun prerequisite ->
                (WorkComposition.currentAcceptance graph prerequisite).IsSome))

    // Sort key is (isBlocking, leadPriority, -availableSequence) descending.
    // Unique work ID breaks pathological equal-sequence ties deterministically.
    // No unavailable/claimed/blocked stage enters this list, regardless of rank.
    let project (scope: WorkBlockingScope)
                (graph: WorkCompositionGraph) (queue: Project)
                : Result<WorkBlockingProjection, WorkBlockingError> =
        if graph.Version <> scope.ExpectedGraphVersion then
            Error(WorkBlockingError.StaleGraph(scope.ExpectedGraphVersion, graph.Version))
        elif queue.Version <> scope.ExpectedProjectVersion then
            Error(WorkBlockingError.StaleProject(scope.ExpectedProjectVersion, queue.Version))
        elif scope.OptionalUnitIds |> Set.exists (fun id ->
            match graph.Units.TryFind id with
            | None -> true
            | Some unit -> unit.ParentId.IsNone) then
            Error(WorkBlockingError.InvalidScope "Optional scope must reference existing non-root children.")
        else
            match WorkComposition.validate graph with
            | Error why -> Error(WorkBlockingError.InvalidGraph why)
            | Ok () ->
                match WorkQueueDependencyDiagnostics.analyze queue.GraphVersion queue.GraphCovered queue.Works with
                | Error why -> Error(WorkBlockingError.DependencyDiagnostics why)
                | Ok diagnostics when not diagnostics.CycleWarnings.IsEmpty ->
                    Error(WorkBlockingError.DependencyCycle diagnostics.CycleWarnings)
                | Ok _ ->
                    match Contracts.orderAvailable queue with
                    | Error why -> Error(WorkBlockingError.IncompleteQueue why)
                    | Ok available ->
                        let blocking = blockingUnitIds graph scope.OptionalUnitIds
                        let unitsByTask =
                            graph.Units
                            |> Map.toSeq
                            |> Seq.choose (fun (_, unit) ->
                                unit.Spec.LinkedTaskId |> Option.map (fun taskId -> taskId, unit))
                            |> Map.ofSeq
                        let ranked =
                            available
                            |> List.choose (fun stage ->
                                let owner = unitsByTask.TryFind stage.SubjectId
                                if owner |> Option.exists (unitMayRun graph >> not) then None
                                else
                                    let inherited = owner |> Option.exists (fun unit -> blocking.Contains unit.Id)
                                    Some
                                        { Stage = stage
                                          IsBlocking = inherited || stageBlocksAnother queue stage
                                          LeadPriority = Contracts.priorityOf queue stage.Id
                                          AvailableSequence = stage.AvailableSequence
                                          BlockingUnit =
                                            owner
                                            |> Option.bind (fun unit ->
                                                if inherited then Some unit.Id else None) })
                            |> List.sortBy (fun entry ->
                                (if entry.IsBlocking then 0 else 1),
                                -(int64 entry.LeadPriority),
                                entry.AvailableSequence,
                                entry.Stage.ProjectId,
                                entry.Stage.Id)
                        Ok
                            { GraphVersion = graph.Version
                              ProjectVersion = queue.Version
                              BlockingUnits = blocking
                              Eligible = ranked }
