namespace Jarvis.WorkQueues

open System

// Composition describes durable outcomes; the queue owns ephemeral executions.
// A child is contained by one parent, but hard dependencies may cross systems.
[<RequireQualifiedAccess>]
type WorkUnitKind =
    | Project
    | System
    | Feature

type WorkUnitSpec =
    { Title: string
      Goal: string
      Scope: string
      AcceptanceCriteria: Set<string>
      Constraints: Set<string>
      LinkedTaskId: string option }

type WorkUnitAcceptance =
    { SpecVersion: int
      GraphVersion: int
      EvidenceRef: string
      ChildAcceptances: Map<string, int>
      PrerequisiteAcceptances: Map<string, int> }

type CompositeWorkUnit =
    { Id: string
      Kind: WorkUnitKind
      ParentId: string option
      Spec: WorkUnitSpec
      SpecVersion: int
      HardPrerequisites: Set<string>
      Acceptances: Map<string, WorkUnitAcceptance>
      CancellationReason: string option }

type WorkCompositionGraph =
    { Version: int
      Units: Map<string, CompositeWorkUnit> }

[<RequireQualifiedAccess>]
type WorkCompositionMutation =
    | Create of id: string * kind: WorkUnitKind * parentId: string option * spec: WorkUnitSpec
    | Revise of id: string * expectedSpecVersion: int * spec: WorkUnitSpec
    | Move of id: string * newParentId: string option
    | Require of id: string * prerequisiteId: string
    | Unrequire of id: string * prerequisiteId: string
    // EvidenceRef must be checked against the authoritative review/integration
    // or validation receipt by the adapter before this transition is published.
    | Accept of id: string * expectedSpecVersion: int * evidenceRef: string
    | Cancel of id: string * reason: string
    | Reopen of id: string

[<RequireQualifiedAccess>]
type WorkCompositionError =
    | StaleGraph of expected: int * actual: int
    | StaleUnit of id: string
    | InvalidSpec of reason: string
    | MissingUnit of id: string
    | DuplicateUnit of id: string
    | InvalidParent of reason: string
    | ContainmentCycle of string list
    | DependencyCycle of string list
    | DuplicateDependency
    | MissingDependency
    | IncompleteChildren of ids: string list
    | IncompletePrerequisites of ids: string list
    | AlreadyAccepted
    | AlreadyCancelled

[<RequireQualifiedAccess>]
type WorkUnitPhase =
    | Accepted
    | Cancelled
    | Blocked
    | Executing
    // Project-local queue eligibility, not global admission or authorization to launch.
    | ReadyForAdmission
    | AwaitingAcceptance
    | AwaitingChildren
    | AwaitingPublication
    | Unverified

type WorkUnitProgress =
    { Descendants: int
      Accepted: int
      Blocked: int
      Executing: int
      ReadyForAdmission: int
      Awaiting: int
      Cancelled: int }

type WorkUnitOverview =
    { Id: string
      Kind: WorkUnitKind
      ParentId: string option
      SpecVersion: int
      AcceptedGraphVersion: int option
      Phase: WorkUnitPhase
      UnmetPrerequisites: string list
      ChildrenAwaitingAcceptance: string list
      ExecutionStages: string list
      ReviewStages: string list
      IntegrationStages: string list
      Progress: WorkUnitProgress }

module WorkComposition =
    let empty = { Version = 0; Units = Map.empty }

    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    let private validTextSet (values: Set<string>) =
        not values.IsEmpty && (values |> Set.forall nonBlank)

    let private validSpec (spec: WorkUnitSpec) =
        if not (nonBlank spec.Title && nonBlank spec.Goal && nonBlank spec.Scope) then
            Error(WorkCompositionError.InvalidSpec "Title, goal and scope are required.")
        elif not (validTextSet spec.AcceptanceCriteria) then
            Error(WorkCompositionError.InvalidSpec "At least one nonempty acceptance criterion is required.")
        elif not (spec.Constraints |> Set.forall nonBlank) then
            Error(WorkCompositionError.InvalidSpec "Constraints must not contain blanks.")
        elif spec.LinkedTaskId |> Option.exists (nonBlank >> not) then
            Error(WorkCompositionError.InvalidSpec "A linked canonical task ID must not be blank.")
        else Ok ()

    let private children (graph: WorkCompositionGraph) parentId =
        graph.Units
        |> Map.toList
        |> List.choose (fun (_, unit) ->
            if unit.ParentId = Some parentId then Some unit else None)

    // Child proofs use positive acceptance graph versions for completed work,
    // and negative spec versions for cancellations. A reopened or revised
    // cancelled child must never match an ancestor's historical proof again.
    // Reopen bumps SpecVersion before a cancellation reason can change.
    let private cancelledChildProofVersion (child: CompositeWorkUnit) =
        -child.SpecVersion

    // Acceptances are immutable target snapshots, not a mutable "done" bit.
    // A newly attached child, changed specification or changed child evidence
    // invalidates current acceptance without erasing the old accepted target.
    let rec private currentAcceptanceIn (graph: WorkCompositionGraph) visited id =
        if Set.contains id visited then None
        else
            match graph.Units.TryFind id with
            | None -> None
            | Some unit when unit.CancellationReason.IsSome -> None
            | Some unit ->
                let latest =
                    unit.Acceptances
                    |> Map.toSeq
                    |> Seq.map snd
                    |> Seq.sortByDescending (fun acceptance -> acceptance.GraphVersion)
                    |> Seq.tryHead
                latest
                |> Option.filter (fun acceptance ->
                    if acceptance.SpecVersion <> unit.SpecVersion then false
                    else
                        let childVersions =
                            children graph id
                            |> List.map (fun child ->
                                match child.CancellationReason with
                                | Some _ -> Some(child.Id, cancelledChildProofVersion child)
                                | None ->
                                    currentAcceptanceIn graph (Set.add id visited) child.Id
                                    |> Option.map (fun proof -> child.Id, proof.GraphVersion))
                        (childVersions |> List.forall Option.isSome)
                        && (childVersions |> List.choose (fun item -> item) |> Map.ofList) = acceptance.ChildAcceptances
                        && (let prerequisites =
                                unit.HardPrerequisites
                                |> Seq.map (fun dependency ->
                                    currentAcceptanceIn graph (Set.add id visited) dependency
                                    |> Option.map (fun proof -> dependency, proof.GraphVersion))
                                |> Seq.toList
                            (prerequisites |> List.forall Option.isSome)
                            && (prerequisites |> List.choose (fun item -> item) |> Map.ofList) = acceptance.PrerequisiteAcceptances))

    let currentAcceptance graph id = currentAcceptanceIn graph Set.empty id

    let private parentValid (graph: WorkCompositionGraph) (unit: CompositeWorkUnit) =
        match unit.Kind, unit.ParentId with
        | WorkUnitKind.Project, None -> Ok ()
        | WorkUnitKind.Project, Some _ ->
            Error(WorkCompositionError.InvalidParent "Project nodes must be roots.")
        | _, None ->
            Error(WorkCompositionError.InvalidParent "Systems and features require a parent.")
        | _, Some id ->
            match graph.Units.TryFind id with
            | None -> Error(WorkCompositionError.MissingUnit id)
            | Some parent when parent.Kind = WorkUnitKind.Feature ->
                Error(WorkCompositionError.InvalidParent "Features cannot contain other work.")
            | Some _ -> Ok ()

    let private cycle (edges: CompositeWorkUnit -> Set<string>) (graph: WorkCompositionGraph) =
        let rec visit path current =
            if List.contains current path then
                Some(List.rev (current :: path))
            else
                match graph.Units.TryFind current with
                | None -> None
                | Some unit ->
                    edges unit
                    |> Seq.tryPick (visit (current :: path))
        graph.Units
        |> Map.toSeq
        |> Seq.tryPick (fun (id, _) -> visit [] id)

    let validate (graph: WorkCompositionGraph) : Result<unit, WorkCompositionError> =
        let nodes = graph.Units |> Map.toList |> List.map snd
        let roots = nodes |> List.filter (fun unit -> unit.Kind = WorkUnitKind.Project)
        let inconsistent =
            nodes
            |> List.tryFind (fun unit -> unit.SpecVersion < 1 || not (nonBlank unit.Id)
                                         || graph.Units.[unit.Id] <> unit)
        match inconsistent with
        | Some unit -> Error(WorkCompositionError.InvalidSpec ("Invalid unit identity/version: " + unit.Id))
        | None when List.length roots > 1 ->
            Error(WorkCompositionError.InvalidParent "A project graph must have a single root.")
        | None ->
            nodes
            |> List.tryPick (fun unit ->
                match validSpec unit.Spec with
                | Error error -> Some error
                | Ok () ->
                    match parentValid graph unit with
                    | Error error -> Some error
                    | Ok () ->
                        unit.HardPrerequisites
                        |> Seq.tryPick (fun prerequisite ->
                            if prerequisite = unit.Id then
                                Some(WorkCompositionError.DependencyCycle [ unit.Id; prerequisite ])
                            elif not (graph.Units.ContainsKey prerequisite) then
                                Some(WorkCompositionError.MissingUnit prerequisite)
                            else None))
            |> function
                | Some error -> Error error
                | None ->
                    match cycle (fun unit -> unit.ParentId |> Option.toList |> Set.ofList) graph with
                    | Some path -> Error(WorkCompositionError.ContainmentCycle path)
                    | None ->
                        // Parent acceptance requires child acceptance, so
                        // containment contributes implicit prerequisite edges.
                        let acceptanceEdges (unit: CompositeWorkUnit) =
                            let ids = children graph unit.Id |> List.map (fun child -> child.Id) |> Set.ofList
                            Set.union unit.HardPrerequisites ids
                        match cycle acceptanceEdges graph with
                        | Some path -> Error(WorkCompositionError.DependencyCycle path)
                        | None -> Ok ()

    let private mutate (graph: WorkCompositionGraph) mutation =
        let requireUnit (id: string) : Result<CompositeWorkUnit, WorkCompositionError> =
            match graph.Units.TryFind id with
            | Some unit -> Ok unit
            | None -> Error(WorkCompositionError.MissingUnit id)
        let update (unit: CompositeWorkUnit) =
            { graph with Units = graph.Units.Add(unit.Id, unit) }
        let bump (unit: CompositeWorkUnit) = { unit with SpecVersion = unit.SpecVersion + 1 }
        let updatePrerequisites id prerequisite insert =
            requireUnit id
            |> Result.bind (fun unit ->
                let exists = unit.HardPrerequisites.Contains prerequisite
                if insert && exists then Error WorkCompositionError.DuplicateDependency
                elif not insert && not exists then Error WorkCompositionError.MissingDependency
                elif insert && not (graph.Units.ContainsKey prerequisite) then
                    Error(WorkCompositionError.MissingUnit prerequisite)
                else
                    let next = if insert then Set.add prerequisite unit.HardPrerequisites
                               else Set.remove prerequisite unit.HardPrerequisites
                    Ok(update (bump { unit with HardPrerequisites = next })))
        match mutation with
        | WorkCompositionMutation.Create (id, kind, parentId, spec) ->
            if not (nonBlank id) then Error(WorkCompositionError.InvalidSpec "ID is required.")
            elif graph.Units.ContainsKey id then Error(WorkCompositionError.DuplicateUnit id)
            else
                let unit =
                    { Id = id; Kind = kind; ParentId = parentId; Spec = spec
                      SpecVersion = 1; HardPrerequisites = Set.empty
                      Acceptances = Map.empty; CancellationReason = None }
                Ok(update unit)
        | WorkCompositionMutation.Revise (id, version, spec) ->
            requireUnit id
            |> Result.bind (fun unit ->
                if unit.SpecVersion <> version then Error(WorkCompositionError.StaleUnit id)
                else Ok(update (bump { unit with Spec = spec })))
        | WorkCompositionMutation.Move (id, parent) ->
            requireUnit id
            |> Result.bind (fun unit ->
                if unit.ParentId = parent then
                    Error(WorkCompositionError.InvalidParent "The requested parent is unchanged.")
                else Ok(update (bump { unit with ParentId = parent })))
        | WorkCompositionMutation.Require (id, prerequisite) ->
            updatePrerequisites id prerequisite true
        | WorkCompositionMutation.Unrequire (id, prerequisite) ->
            updatePrerequisites id prerequisite false
        | WorkCompositionMutation.Accept (id, version, evidence) ->
            requireUnit id
            |> Result.bind (fun unit ->
                if unit.SpecVersion <> version then Error(WorkCompositionError.StaleUnit id)
                elif unit.CancellationReason.IsSome then Error WorkCompositionError.AlreadyCancelled
                elif not (nonBlank evidence) then
                    Error(WorkCompositionError.InvalidSpec "Verified acceptance evidence reference is required.")
                elif (currentAcceptance graph id).IsSome then Error WorkCompositionError.AlreadyAccepted
                else
                    let pending =
                        children graph id
                        |> List.filter (fun child ->
                            child.CancellationReason.IsNone &&
                            (currentAcceptance graph child.Id).IsNone)
                    if not pending.IsEmpty then
                        Error(WorkCompositionError.IncompleteChildren (pending |> List.map (fun u -> u.Id)))
                    else
                        let unmetPrerequisites =
                            unit.HardPrerequisites
                            |> Set.filter (fun prerequisite -> (currentAcceptance graph prerequisite).IsNone)
                        if not unmetPrerequisites.IsEmpty then
                            Error(WorkCompositionError.IncompletePrerequisites (Set.toList unmetPrerequisites))
                        else
                            let refs =
                                children graph id
                                |> List.map (fun child ->
                                    child.Id,
                                    (if child.CancellationReason.IsSome then cancelledChildProofVersion child
                                     else (currentAcceptance graph child.Id).Value.GraphVersion))
                                |> Map.ofList
                            let proof =
                                { SpecVersion = version
                                  GraphVersion = graph.Version + 1
                                  EvidenceRef = evidence
                                  ChildAcceptances = refs
                                  PrerequisiteAcceptances =
                                    unit.HardPrerequisites
                                    |> Seq.map (fun prerequisite ->
                                        prerequisite, (currentAcceptance graph prerequisite).Value.GraphVersion)
                                    |> Map.ofSeq }
                            let nextUnit = { unit with Acceptances = unit.Acceptances.Add(string proof.GraphVersion, proof) }
                            Ok(update nextUnit))
        | WorkCompositionMutation.Cancel (id, reason) ->
            requireUnit id
            |> Result.bind (fun unit ->
                if unit.CancellationReason.IsSome then Error WorkCompositionError.AlreadyCancelled
                elif not (nonBlank reason) then
                    Error(WorkCompositionError.InvalidSpec "Cancellation reason is required.")
                else Ok(update { unit with CancellationReason = Some reason }))
        | WorkCompositionMutation.Reopen id ->
            requireUnit id
            |> Result.bind (fun unit ->
                if unit.CancellationReason.IsNone then
                    Error(WorkCompositionError.InvalidSpec "Only cancelled work can be reopened.")
                else Ok(update (bump { unit with CancellationReason = None })))

    let apply expectedVersion mutation graph =
        if graph.Version <> expectedVersion then
            Error(WorkCompositionError.StaleGraph(expectedVersion, graph.Version))
        else
            mutate graph mutation
            |> Result.map (fun candidate -> { candidate with Version = graph.Version + 1 })
            |> Result.bind (fun candidate ->
                validate candidate |> Result.map (fun () -> candidate))

    let private descendants graph id =
        let rec walk current =
            children graph current
            |> List.collect (fun child -> child :: walk child.Id)
        walk id

    let private stageKinds (queue: Project option) (unit: CompositeWorkUnit) =
        match queue, unit.Spec.LinkedTaskId with
        | Some project, Some taskId ->
            project.Works
            |> Map.toList
            |> List.map snd
            |> List.filter (fun stage -> stage.SubjectId = taskId)
        | _ -> []

    // This is the canonical WQ project selector: coverage, hard-stage
    // prerequisites and non-clear gate checks are not duplicated here.
    // Candidates still need independent account-wide admission and provenance.
    let private eligibleStageIds (queue: Project option) =
        match queue with
        | Some project ->
            Contracts.orderAvailable project
            |> Result.toOption
            |> Option.map (List.map (fun stage -> stage.Id) >> Set.ofList)
        | None -> None

    let private unmet graph (unit: CompositeWorkUnit) =
        unit.HardPrerequisites
        |> Set.toList
        |> List.filter (fun id -> (currentAcceptance graph id).IsNone)

    let private waitingChildren graph id =
        children graph id
        |> List.choose (fun child ->
            if child.CancellationReason.IsNone && (currentAcceptance graph child.Id).IsNone
            then Some child.Id else None)

    // ReadyForAdmission means only local project queue eligibility. It is
    // not global scheduler, role, capacity, migration or launch authorization.
    // Publishing a stage is a separate Git-CAS effect.
    let phase graph queue id =
        match graph.Units.TryFind id with
        | None -> None
        | Some unit when unit.CancellationReason.IsSome -> Some WorkUnitPhase.Cancelled
        | Some unit when (currentAcceptance graph id).IsSome -> Some WorkUnitPhase.Accepted
        | Some unit ->
            let blocked = unmet graph unit
            let candidates = eligibleStageIds queue
            if not blocked.IsEmpty then Some WorkUnitPhase.Blocked
            elif unit.Spec.LinkedTaskId.IsSome && candidates.IsNone then
                Some WorkUnitPhase.Unverified
            else
                let stages = stageKinds queue unit
                if stages |> List.exists (fun stage ->
                    match stage.State with WorkState.Claimed _ -> true | _ -> false) then
                    Some WorkUnitPhase.Executing
                elif (queue |> Option.exists (fun project -> project.Migration = MigrationEpoch.Activated))
                     && (stages |> List.exists (fun stage ->
                         candidates |> Option.exists (fun ids -> ids.Contains stage.Id))) then
                    Some WorkUnitPhase.ReadyForAdmission
                elif stages |> List.exists (fun stage -> stage.State = WorkState.Blocked
                                                        || stage.State = WorkState.GateException
                                                        || stage.State = WorkState.Available) then
                    Some WorkUnitPhase.Blocked
                elif not (waitingChildren graph id).IsEmpty then
                    Some WorkUnitPhase.AwaitingChildren
                elif unit.Kind <> WorkUnitKind.Feature then
                    // A parent with accepted children awaits its own validation.
                    Some WorkUnitPhase.AwaitingAcceptance
                elif stages |> List.exists (fun stage -> stage.State = WorkState.Completed) then
                    Some WorkUnitPhase.AwaitingAcceptance
                else Some WorkUnitPhase.AwaitingPublication

    let overview graph queue id =
        graph.Units.TryFind id
        |> Option.map (fun unit ->
            let stages = stageKinds queue unit
            let ids kind =
                stages
                |> List.choose (fun stage -> if stage.Kind = kind then Some stage.Id else None)
            let descendants = descendants graph id
            let count wanted =
                descendants
                |> List.filter (fun child -> phase graph queue child.Id = Some wanted)
                |> List.length
            let cancelled = count WorkUnitPhase.Cancelled
            { Id = id
              Kind = unit.Kind
              ParentId = unit.ParentId
              SpecVersion = unit.SpecVersion
              AcceptedGraphVersion = currentAcceptance graph id |> Option.map (fun a -> a.GraphVersion)
              Phase = phase graph queue id |> Option.get
              UnmetPrerequisites = unmet graph unit
              ChildrenAwaitingAcceptance = waitingChildren graph id
              ExecutionStages = ids WorkKind.Implementation
              ReviewStages = ids WorkKind.FrozenReview
              IntegrationStages = ids WorkKind.Integration
              Progress =
                { Descendants = descendants.Length
                  Accepted = count WorkUnitPhase.Accepted
                  Blocked = count WorkUnitPhase.Blocked
                  Executing = count WorkUnitPhase.Executing
                  ReadyForAdmission = count WorkUnitPhase.ReadyForAdmission
                  Awaiting =
                    descendants.Length - cancelled - count WorkUnitPhase.Accepted -
                    count WorkUnitPhase.Blocked - count WorkUnitPhase.Executing -
                    count WorkUnitPhase.ReadyForAdmission
                  Cancelled = cancelled } })

    let allOverviews graph queue =
        graph.Units |> Map.toList |> List.choose (fun (id, _) -> overview graph queue id)
