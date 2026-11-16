module ServerWorkCompositionTests

open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.WorkComposition

let private success = function
    | Ok state -> state
    | Error error -> failwithf "Unexpected composition error: %A" error

let private spec name task =
    { Title = name
      Goal = "Deliver " + name
      Scope = "Scope for " + name
      AcceptanceCriteria = Set.singleton ("Verify " + name)
      Constraints = Set.singleton "Preserve existing semantics"
      LinkedTaskId = task }

let private change (graph: WorkCompositionGraph) mutation =
    apply graph.Version mutation graph |> success

let private initial () =
    change empty (WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None))

let private system graph id parent =
    change graph (WorkCompositionMutation.Create(id, WorkUnitKind.System, Some parent, spec id None))

let private feature graph id parent task =
    change graph (WorkCompositionMutation.Create(id, WorkUnitKind.Feature, Some parent, spec id task))

let private accepted (graph: WorkCompositionGraph) id =
    change graph (WorkCompositionMutation.Accept(id, graph.Units.[id].SpecVersion, "verified:evidence:" + id))

[<Test>]
let ``nested systems with cross-system dependencies do not imply shared ownership`` () =
    let graph =
        initial ()
        |> fun g -> system g "a" "project"
        |> fun g -> system g "nested" "a"
        |> fun g -> feature g "f1" "nested" None
        |> fun g -> system g "b" "project"
        |> fun g -> feature g "f2" "b" None
        |> fun g -> change g (WorkCompositionMutation.Require("f1", "f2"))
    Assert.That(graph.Units.["f1"].ParentId, Is.EqualTo(Some "nested"))
    Assert.That(phase graph None "f1", Is.EqualTo(Some WorkUnitPhase.Blocked))
    Assert.That(phase graph None "f2", Is.EqualTo(Some WorkUnitPhase.AwaitingPublication))

[<Test>]
let ``dependency and containment cycles reject the mutation`` () =
    let g =
        initial ()
        |> fun g -> system g "a" "project"
        |> fun g -> system g "b" "a"
        |> fun g -> feature g "f1" "a" None
        |> fun g -> feature g "f2" "b" None
        |> fun g -> change g (WorkCompositionMutation.Require("f1", "f2"))
    match apply g.Version (WorkCompositionMutation.Require("f2", "f1")) g with
    | Error(WorkCompositionError.DependencyCycle path) ->
        Assert.That(path, Does.Contain("f1"))
        Assert.That(path, Does.Contain("f2"))
    | other -> Assert.Fail(sprintf "Expected dependency cycle: %A" other)
    match apply g.Version (WorkCompositionMutation.Move("a", Some "b")) g with
    | Error(WorkCompositionError.ContainmentCycle _) -> ()
    | other -> Assert.Fail(sprintf "Expected containment cycle: %A" other)

[<Test>]
let ``children finishing does not accept a parent`` () =
    let g =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "feature" "system" None
        |> fun g -> accepted g "feature"
    Assert.That(currentAcceptance g "feature" |> Option.isSome, Is.True)
    Assert.That(currentAcceptance g "system" |> Option.isSome, Is.False)
    Assert.That(currentAcceptance g "project" |> Option.isSome, Is.False)
    Assert.That(phase g None "system", Is.EqualTo(Some WorkUnitPhase.AwaitingAcceptance))
    let withSystem = accepted g "system"
    Assert.That(currentAcceptance withSystem "system" |> Option.isSome, Is.True)
    Assert.That(currentAcceptance withSystem "project" |> Option.isSome, Is.False)
    let withProject = accepted withSystem "project"
    Assert.That(currentAcceptance withProject "project" |> Option.isSome, Is.True)

[<Test>]
let ``parent acceptance fails if child is not accepted`` () =
    let g = initial () |> fun g -> system g "system" "project"
                       |> fun g -> feature g "feature" "system" None
    match apply g.Version (WorkCompositionMutation.Accept("system", 1, "evidence")) g with
    | Error(WorkCompositionError.IncompleteChildren ids) ->
        Assert.That(ids, Is.EquivalentTo([ "feature" ]))
    | other -> Assert.Fail(sprintf "Expected missing child proof: %A" other)
    match apply g.Version (WorkCompositionMutation.Accept("feature", 1, "")) g with
    | Error(WorkCompositionError.InvalidSpec _) -> ()
    | other -> Assert.Fail(sprintf "Expected missing evidence: %A" other)

[<Test>]
let ``an accepted target remains in history when corrective child is added`` () =
    let graph =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "first" "system" None
        |> fun g -> accepted g "first"
        |> fun g -> accepted g "system"
    let original = (currentAcceptance graph "system").Value
    let changed = feature graph "correction" "system" None
    Assert.That(currentAcceptance changed "system" |> Option.isSome, Is.False)
    Assert.That(changed.Units.["system"].Acceptances.ContainsKey(string original.GraphVersion), Is.True)
    let repaired = changed |> fun g -> accepted g "correction"
                           |> fun g -> accepted g "system"
    Assert.That(currentAcceptance repaired "system" |> Option.isSome, Is.True)
    Assert.That(repaired.Units.["system"].Acceptances.Count, Is.EqualTo(2))

[<Test>]
let ``revising an accepted child invalidates ancestor acceptance transitively`` () =
    let graph =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "feature" "system" None
        |> fun g -> accepted g "feature"
        |> fun g -> accepted g "system"
        |> fun g -> accepted g "project"
    let newSpec = { graph.Units.["feature"].Spec with Scope = "Updated scope" }
    let revised = change graph (WorkCompositionMutation.Revise("feature", 1, newSpec))
    for id in [ "feature"; "system"; "project" ] do
        Assert.That(currentAcceptance revised id |> Option.isSome, Is.False, id)
    let repaired =
        revised
        |> fun g -> accepted g "feature"
        |> fun g -> accepted g "system"
        |> fun g -> accepted g "project"
    Assert.That(currentAcceptance repaired "project" |> Option.isSome, Is.True)
    Assert.That(repaired.Units.["project"].Acceptances.Count, Is.EqualTo(2))

[<Test>]
let ``prerequisite evidence is required and changing it invalidates dependent acceptance`` () =
    let graph =
        initial ()
        |> fun g -> feature g "first" "project" None
        |> fun g -> feature g "dependent" "project" None
        |> fun g -> change g (WorkCompositionMutation.Require("dependent", "first"))
    match apply graph.Version (WorkCompositionMutation.Accept("dependent", 2, "receipt")) graph with
    | Error(WorkCompositionError.IncompletePrerequisites [ "first" ]) -> ()
    | other -> Assert.Fail(sprintf "Expected prerequisite evidence: %A" other)
    let acceptedGraph = graph |> fun g -> accepted g "first"
                              |> fun g -> accepted g "dependent"
    Assert.That(currentAcceptance acceptedGraph "dependent" |> Option.isSome, Is.True)
    let revised =
        change acceptedGraph
            (WorkCompositionMutation.Revise("first", 1, { graph.Units.["first"].Spec with Scope = "Changed" }))
    Assert.That(currentAcceptance revised "dependent" |> Option.isSome, Is.False)
    Assert.That(phase revised None "dependent", Is.EqualTo(Some WorkUnitPhase.Blocked))

[<Test>]
let ``crossing containment and dependency edges cannot create implicit acceptance cycles`` () =
    let graph =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "nested" "system" None
    match apply graph.Version (WorkCompositionMutation.Require("nested", "system")) graph with
    | Error(WorkCompositionError.DependencyCycle path) ->
        Assert.That(path, Does.Contain("system"))
        Assert.That(path, Does.Contain("nested"))
    | other -> Assert.Fail(sprintf "Expected implicit acceptance cycle: %A" other)

[<Test>]
let ``cancelled child remains in the progress denominator until explicitly reopened`` () =
    let graph =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "kept" "system" None
        |> fun g -> feature g "cancelled" "system" None
        |> fun g -> accepted g "kept"
        |> fun g -> change g (WorkCompositionMutation.Cancel("cancelled", "No longer in scope"))
    let progress = (overview graph None "system").Value.Progress
    Assert.That(progress.Descendants, Is.EqualTo(2))
    Assert.That(progress.Accepted, Is.EqualTo(1))
    Assert.That(progress.Cancelled, Is.EqualTo(1))
    let acceptedSystem = accepted graph "system"
    Assert.That(currentAcceptance acceptedSystem "system" |> Option.isSome, Is.True)
    let reopened = change acceptedSystem (WorkCompositionMutation.Reopen "cancelled")
    Assert.That(currentAcceptance reopened "system" |> Option.isSome, Is.False)
    Assert.That(reopened.Units.["cancelled"].SpecVersion, Is.EqualTo(2))

[<Test>]
let ``stale concurrent edits and invalid references fail closed`` () =
    let graph = initial ()
    let committed = system graph "a" "project"
    match apply graph.Version (WorkCompositionMutation.Create("b", WorkUnitKind.System, Some "project", spec "b" None)) committed with
    | Error(WorkCompositionError.StaleGraph(expected, actual)) ->
        Assert.That(expected, Is.EqualTo(graph.Version))
        Assert.That(actual, Is.EqualTo(committed.Version))
    | other -> Assert.Fail(sprintf "Expected stale graph conflict: %A" other)
    match apply graph.Version (WorkCompositionMutation.Require("project", "missing")) graph with
    | Error(WorkCompositionError.MissingUnit "missing") -> ()
    | other -> Assert.Fail(sprintf "Expected missing reference: %A" other)

[<Test>]
let ``missing queue coverage does not imply runnable work`` () =
    let graph =
        initial ()
        |> fun g -> feature g "task" "project" (Some "canonical-task")
    Assert.That(phase graph None "task", Is.EqualTo(Some WorkUnitPhase.Unverified))
    let queue = Jarvis.WorkQueues.Contracts.emptyProject Map.empty
    Assert.That(phase graph (Some queue) "task", Is.EqualTo(Some WorkUnitPhase.AwaitingPublication))
    Assert.That(phase graph (Some { queue with GraphCovered = false }) "task",
                Is.EqualTo(Some WorkUnitPhase.Unverified))
