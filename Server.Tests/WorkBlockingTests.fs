module ServerWorkBlockingTests

open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts

let private ok = function
    | Ok value -> value
    | Error reason -> failwithf "Unexpected Work rejection: %A" reason

let private testSpec id linkedTask =
    { Title = id
      Goal = "Verified outcome"
      Scope = "Unit scope"
      AcceptanceCriteria = Set.singleton "Proof"
      Constraints = Set.empty
      LinkedTaskId = linkedTask }

let private add id kind parent task (graph: WorkCompositionGraph) =
    WorkComposition.apply graph.Version
        (WorkCompositionMutation.Create(id, kind, parent, testSpec id task)) graph
    |> ok

let private require id dependency (graph: WorkCompositionGraph) =
    WorkComposition.apply graph.Version (WorkCompositionMutation.Require(id, dependency)) graph
    |> ok

let private cancel id (graph: WorkCompositionGraph) =
    WorkComposition.apply graph.Version (WorkCompositionMutation.Cancel(id, "superseded")) graph
    |> ok

let private root () =
    WorkComposition.empty |> add "root" WorkUnitKind.Project None None

let private system id graph =
    graph |> add id WorkUnitKind.System (Some "root") (Some("task-" + id))

let private child id parent graph =
    graph |> add id WorkUnitKind.Feature (Some parent) (Some("task-" + id))

let private subsystem id parent graph =
    graph |> add id WorkUnitKind.System (Some parent) (Some("task-" + id))

let private stage id sequence : WorkStage =
    { Id = "stage-" + id
      ProjectId = "Jarvis"
      SubjectId = "task-" + id
      Kind = WorkKind.Implementation
      QueueId = "implementation"
      StageGeneration = 1
      State = WorkState.Available
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.empty
      WaivedDependencies = Map.empty
      AvailableAt = sequence
      AvailableSequence = sequence
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private queue (graph: WorkCompositionGraph) (stages: WorkStage list) : Project =
    { emptyProject Map.empty with
        GraphVersion = graph.Version
        Works = stages |> List.map (fun s -> s.Id, s) |> Map.ofList
        NextAvailability = 100L }

let private rank (graph: WorkCompositionGraph) (project: Project) (optional: Set<string>) =
    WorkBlocking.project
        { ExpectedGraphVersion = graph.Version
          ExpectedProjectVersion = project.Version
          OptionalUnitIds = optional } graph project
    |> ok

let private ids (projection: WorkBlockingProjection) =
    projection.Eligible |> List.map (fun entry -> entry.Stage.Id)

[<Test>]
let blockerPropagatesToRequiredUnfinishedDescendantsOnly () =
    let graph =
        root ()
        |> system "a" |> system "b" |> system "ordinary"
        |> child "a-child" "a"
        |> subsystem "b-child" "b"
        |> child "b-grandchild" "b-child"
        |> require "a" "b"
    let result =
        queue graph
            [ stage "b" 1L; stage "b-child" 2L; stage "b-grandchild" 3L
              stage "a-child" 4L; stage "ordinary" 5L ]
        |> fun p -> rank graph p Set.empty
    Assert.That(result.BlockingUnits = Set.ofList [ "b"; "b-child"; "b-grandchild" ], Is.True)
    Assert.That((ids result =
                [ "stage-b"; "stage-b-child"; "stage-b-grandchild"
                  "stage-a-child"; "stage-ordinary" ]), Is.True)
    Assert.That(result.Eligible |> List.take 3 |> List.forall (fun entry -> entry.IsBlocking), Is.True)
    Assert.That(result.Eligible |> List.skip 3 |> List.forall (fun entry -> not entry.IsBlocking), Is.True)

[<Test>]
let optionalAndCancelledDescendantsDoNotInheritBlockerRank () =
    let graph =
        root () |> system "a" |> system "b"
        |> child "required" "b" |> child "optional" "b" |> child "cancelled" "b"
        |> require "a" "b"
        |> cancel "cancelled"
    let result =
        queue graph [ stage "required" 10L; stage "optional" 1L; stage "cancelled" 2L ]
        |> fun p -> rank graph p (Set.singleton "optional")
    Assert.That(result.BlockingUnits = Set.ofList [ "b"; "required" ], Is.True)
    Assert.That(ids result = [ "stage-required"; "stage-optional" ], Is.True)
    Assert.That(result.Eligible.[1].IsBlocking, Is.False)

[<Test>]
let transitiveHardPrerequisitesBlockButUnmetCandidatesCannotDispatch () =
    let graph =
        root () |> system "a" |> system "b" |> system "c"
        |> require "a" "b" |> require "b" "c"
    let result =
        queue graph [ stage "a" 1L; stage "b" 2L; stage "c" 3L ]
        |> fun p -> rank graph p Set.empty
    Assert.That(result.BlockingUnits = Set.ofList [ "b"; "c" ], Is.True)
    Assert.That(ids result = [ "stage-c" ], Is.True)
    Assert.That(result.Eligible.Head.IsBlocking, Is.True)

[<Test>]
let containmentAloneDoesNotPromoteOrdinaryChildren () =
    let graph =
        root () |> system "a" |> system "b" |> child "a-child" "a"
    let result =
        queue graph [ stage "a-child" 1L; stage "b" 2L ]
        |> fun p -> rank graph p Set.empty
    Assert.That(result.BlockingUnits, Is.Empty)
    Assert.That(result.Eligible |> List.forall (fun entry -> not entry.IsBlocking), Is.True)

[<Test>]
let blockingTierOutranksManualPriorityAndVersionedPriorityCas () =
    let graph =
        root () |> system "a" |> system "b" |> system "ordinary"
        |> require "a" "b"
    let baseline = queue graph [ stage "b" 90L; stage "ordinary" 1L ]
    let elevated =
        assignPriority baseline.Version "priority-ordinary" "stage-ordinary" 1000 "operator ranking" baseline
        |> ok
    let result = rank graph elevated Set.empty
    Assert.That(ids result = [ "stage-b"; "stage-ordinary" ], Is.True)
    Assert.That(result.Eligible.Head.LeadPriority, Is.EqualTo(0))
    assignPriority baseline.Version "stale" "stage-b" 8 "late writer" elevated
    |> function
        | Error Rejection.CasConflict -> ()
        | other -> Assert.Fail(sprintf "Expected stale version rejection, got %A" other)

[<Test>]
let fifoTiesUseNewAvailableSequenceAfterRequeue () =
    let graph = root () |> system "x" |> system "y"
    let before = queue graph [ stage "x" 1L; stage "y" 2L ]
    Assert.That((rank graph before Set.empty |> ids) = [ "stage-x"; "stage-y" ], Is.True)
    // The accepted queue contract supplies a fresh availability sequence on
    // requeue. This fixture models the committed post-requeue queue snapshot.
    let x = { before.Works.["stage-x"] with AvailableSequence = 3L; AvailableAt = 3L }
    let after =
        { before with
            Version = before.Version + 1
            NextAvailability = 3L
            Works = before.Works.Add(x.Id, x) }
    Assert.That((rank graph after Set.empty |> ids) = [ "stage-y"; "stage-x" ], Is.True)

[<Test>]
let staleGraphAndProjectSnapshotsRejectBeforeQueueProjection () =
    let graph = root () |> system "a" |> system "b"
    let project = queue graph [ stage "a" 1L ]
    WorkBlocking.project
        { ExpectedGraphVersion = graph.Version - 1
          ExpectedProjectVersion = project.Version
          OptionalUnitIds = Set.empty } graph project
    |> function
        | Error(WorkBlockingError.StaleGraph _) -> ()
        | other -> Assert.Fail(sprintf "Expected graph conflict: %A" other)
    WorkBlocking.project
        { ExpectedGraphVersion = graph.Version
          ExpectedProjectVersion = project.Version - 1
          OptionalUnitIds = Set.empty } graph project
    |> function
        | Error(WorkBlockingError.StaleProject _) -> ()
        | other -> Assert.Fail(sprintf "Expected project conflict: %A" other)
    let changed = require "a" "b" graph
    WorkBlocking.project
        { ExpectedGraphVersion = graph.Version
          ExpectedProjectVersion = project.Version
          OptionalUnitIds = Set.empty } changed project
    |> function
        | Error(WorkBlockingError.StaleGraph _) -> ()
        | other -> Assert.Fail(sprintf "Expected changed-graph conflict: %A" other)

[<Test>]
let graphCycleAndStageCycleAreDiagnosedWithoutAnyDispatchProjection () =
    let graph = root () |> system "a" |> system "b" |> require "a" "b"
    let invalid =
        { graph with
            Units =
                graph.Units.Add(
                    "b", { graph.Units.["b"] with HardPrerequisites = Set.singleton "a" }) }
    let project = queue graph [ stage "a" 1L; stage "b" 2L ]
    WorkBlocking.project
        { ExpectedGraphVersion = invalid.Version
          ExpectedProjectVersion = project.Version
          OptionalUnitIds = Set.empty } invalid project
    |> function
        | Error(WorkBlockingError.InvalidGraph(WorkCompositionError.DependencyCycle _)) -> ()
        | other -> Assert.Fail(sprintf "Expected graph cycle: %A" other)
    let x = { stage "a" 1L with Dependencies = Set.singleton "stage-b" }
    let y = { stage "b" 2L with Dependencies = Set.singleton "stage-a" }
    let cyclic = queue graph [ x; y; stage "ordinary" 3L ]
    WorkBlocking.project
        { ExpectedGraphVersion = graph.Version
          ExpectedProjectVersion = cyclic.Version
          OptionalUnitIds = Set.empty } graph cyclic
    |> function
        | Error(WorkBlockingError.DependencyCycle cycles) ->
            Assert.That(cycles, Is.Not.Empty)
        | other -> Assert.Fail(sprintf "Expected stage cycle: %A" other)

[<Test>]
let stageGatesAndDependenciesAreCheckedBeforeBlockingPriority () =
    let graph = root () |> system "a" |> system "b" |> system "ordinary" |> require "a" "b"
    let blockedB = { stage "b" 1L with State = WorkState.Blocked }
    let blockedA = { stage "a" 2L with Dependencies = Set.singleton blockedB.Id }
    let result =
        queue graph [ blockedB; blockedA; stage "ordinary" 3L ]
        |> fun p -> rank graph p Set.empty
    Assert.That(ids result = [ "stage-ordinary" ], Is.True)
    Assert.That(result.BlockingUnits.Contains "b", Is.True)
