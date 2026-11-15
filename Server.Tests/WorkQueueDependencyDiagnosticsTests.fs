module ServerWorkQueueDependencyDiagnosticsTests

open System
open NUnit.Framework
open Jarvis.WorkQueues

let private stage id dependencies : WorkStage =
    { Id = id
      ProjectId = "Jarvis"
      SubjectId = "task:" + id
      Kind = WorkKind.Implementation
      QueueId = "implementation"
      StageGeneration = 1
      State = WorkState.Blocked
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = Set.ofList dependencies
      WaivedDependencies = Map.empty
      AvailableAt = 0L
      AvailableSequence = 0L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private graph (stages: WorkStage list) =
    stages |> List.map (fun work -> work.Id, work) |> Map.ofList

let private success result =
    match result with
    | Ok diagnostics -> diagnostics
    | Error error -> failwithf "Unexpected incomplete graph: %s" error

let private rejected result =
    match result with
    | Ok _ -> failwith "Expected fail-closed incomplete coverage"
    | Error error -> error

let private analyse works = WorkQueueDependencyDiagnostics.analyze 37 true works |> success

[<Test>]
let self_loop_is_a_cycle_with_exact_stage_and_hard_edge () =
    let result = graph [ stage "A:gen1" [ "A:gen1" ] ] |> analyse
    Assert.That(result.GraphVersion, Is.EqualTo(box 37))
    Assert.That(result.CoverageComplete, Is.True)
    Assert.That(result.StageIds, Is.EqualTo(box [ "A:gen1" ]))
    Assert.That(result.HardEdges, Is.EqualTo(box [ "A:gen1", "A:gen1" ]))
    Assert.That(
        result.CycleWarnings,
        Is.EqualTo(box [ { Members = [ "A:gen1" ]; WitnessEdges = [ "A:gen1", "A:gen1" ] } ])
    )

[<Test>]
let three_node_cycle_has_exact_ordered_witnesses () =
    let result =
        graph [ stage "C" [ "A" ]; stage "A" [ "B" ]; stage "B" [ "C" ] ]
        |> analyse
    Assert.That(result.StageIds, Is.EqualTo(box [ "A"; "B"; "C" ]))
    Assert.That(result.HardEdges, Is.EqualTo(box [ "A", "B"; "B", "C"; "C", "A" ]))
    Assert.That(
        result.CycleWarnings,
        Is.EqualTo(box [ { Members = [ "A"; "B"; "C" ];
                       WitnessEdges = [ "A", "B"; "B", "C"; "C", "A" ] } ])
    )

[<Test>]
let disjoint_cycles_and_acyclic_tail_are_not_conflated () =
    let result =
        graph
            [ stage "Z" []
              stage "D" [ "D" ]
              stage "C" [ "A" ]
              stage "B" [ "A" ]
              stage "A" [ "B" ] ]
        |> analyse
    Assert.That(
        result.CycleWarnings,
        Is.EqualTo(box
            [ { Members = [ "A"; "B" ]; WitnessEdges = [ "A", "B"; "B", "A" ] }
              { Members = [ "D" ]; WitnessEdges = [ "D", "D" ] } ])
    )
    Assert.That(result.HardEdges, Is.EqualTo(box [ "A", "B"; "B", "A"; "C", "A"; "D", "D" ]))

[<Test>]
let removing_one_hard_edge_clears_cycle_without_changing_other_edges () =
    let cyclic = graph [ stage "A" [ "B" ]; stage "B" [ "A" ] ]
    let acyclic = cyclic.Add("B", stage "B" [])
    Assert.That((analyse cyclic).CycleWarnings.Length, Is.EqualTo(box 1))
    let result = analyse acyclic
    Assert.That(result.CycleWarnings, Is.Empty)
    Assert.That(result.HardEdges, Is.EqualTo(box [ "A", "B" ]))

[<Test>]
let informational_subjects_and_lineage_are_not_hard_edges () =
    let source =
        { RepositoryId = "Jarvis"
          TaskId = "A"
          TargetRef = "refs/heads/main"
          LineageId = "B"
          PinnedBaseCommit = "base"
          ReachableRef = None
          ContinuationSha = None
          Generation = 1 }
    let a = { stage "A" [] with SubjectId = "B"; SourceLineage = Some source }
    let b = { stage "B" [] with SubjectId = "A" }
    let result = graph [ a; b ] |> analyse
    Assert.That(result.HardEdges, Is.Empty)
    Assert.That(result.CycleWarnings, Is.Empty)

[<Test>]
let waived_hard_edges_remain_in_diagnostic_graph () =
    let a =
        { stage "A" [ "B" ] with
            WaivedDependencies = Map.ofList [ "B", "verified-waiver" ] }
    let result = graph [ a; stage "B" [ "A" ] ] |> analyse
    Assert.That(result.CycleWarnings, Is.EqualTo(box
        [ { Members = [ "A"; "B" ]; WitnessEdges = [ "A", "B"; "B", "A" ] } ]))

[<Test>]
let incomplete_highwater_fails_closed_even_when_no_edge_is_missing () =
    let works = graph [ stage "A" [ "B" ]; stage "B" [ "A" ] ]
    let before = works
    let error = WorkQueueDependencyDiagnostics.analyze 37 false works |> rejected
    Assert.That(error, Does.Contain("v37"))
    Assert.That(error, Does.Contain("unverified graph high-water/index"))
    Assert.That(works, Is.EqualTo(box before))
    Assert.That(WorkQueueDependencyDiagnostics.analyze 37 false Map.empty |> rejected,
                Does.Contain("unverified graph high-water/index"))

[<Test>]
let missing_prerequisite_stages_fail_closed_with_sorted_exact_witnesses () =
    let works =
        graph [ stage "Z" [ "none"; "later" ]; stage "A" [ "unknown"; "later" ] ]
    let error = WorkQueueDependencyDiagnostics.analyze 37 true works |> rejected
    Assert.That(
        error,
        Is.EqualTo(box
            "dependency graph v37 incomplete: missing hard prerequisite stage generation(s): A -> later, A -> unknown, Z -> later, Z -> none"))
    let combined = WorkQueueDependencyDiagnostics.analyze 38 false works |> rejected
    Assert.That(combined, Does.Contain("unverified graph high-water/index"))
    Assert.That(combined, Does.Contain("A -> later"))

[<Test>]
let mismatched_stage_key_fails_closed_instead_of_fabricating_edges () =
    let works = Map.ofList [ "A", stage "different" [] ]
    let error = WorkQueueDependencyDiagnostics.analyze 37 true works |> rejected
    Assert.That(error, Does.Contain("stage ID/key mismatch: A != different"))

[<Test>]
let stage_generations_are_distinct_from_shared_task_identity () =
    let a = { stage "task:gen1" [ "task:gen2" ] with SubjectId = "task" }
    let b = { stage "task:gen2" [ "task:gen1" ] with SubjectId = "task" }
    let result = graph [ b; a ] |> analyse
    Assert.That(result.CycleWarnings, Is.EqualTo(box
        [ { Members = [ "task:gen1"; "task:gen2" ];
            WitnessEdges = [ "task:gen1", "task:gen2"; "task:gen2", "task:gen1" ] } ]))

[<Test>]
let insertion_order_and_repeated_reads_have_identical_ordered_results () =
    let a = stage "A" [ "B"; "D" ]
    let b = stage "B" [ "C" ]
    let c = stage "C" [ "A"; "D" ]
    let d = stage "D" []
    let forward = graph [ a; b; c; d ]
    let reverse = graph [ d; c; b; a ]
    let first = analyse forward
    let again = analyse forward
    let reordered = analyse reverse
    Assert.That(first, Is.EqualTo(box again))
    Assert.That(first, Is.EqualTo(box reordered))
    Assert.That(first.HardEdges, Is.EqualTo(box
        [ "A", "B"; "A", "D"; "B", "C"; "C", "A"; "C", "D" ]))
    Assert.That(first.CycleWarnings, Is.EqualTo(box
        [ { Members = [ "A"; "B"; "C" ];
            WitnessEdges = [ "A", "B"; "B", "C"; "C", "A" ] } ]))