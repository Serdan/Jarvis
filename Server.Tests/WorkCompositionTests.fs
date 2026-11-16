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
let ``a canonical linked task has exactly one graph owner across create and revise`` () =
    let graph =
        initial ()
        |> fun g -> system g "system" "project"
        |> fun g -> feature g "first" "system" (Some "same-task")
    let duplicateCreate =
        WorkCompositionMutation.Create("second", WorkUnitKind.Feature,
            Some "system", spec "second" (Some "same-task"))
    match apply graph.Version duplicateCreate graph with
    | Error(WorkCompositionError.InvalidSpec reason) ->
        Assert.That(reason, Does.Contain("multiple composition owners"))
    | other -> Assert.Fail(sprintf "Duplicate create was not rejected: %A" other)

    let unlinked = feature graph "second" "system" None
    let revisedSpec =
        { unlinked.Units.["second"].Spec with LinkedTaskId = Some "same-task" }
    match apply unlinked.Version (WorkCompositionMutation.Revise("second", 1, revisedSpec)) unlinked with
    | Error(WorkCompositionError.InvalidSpec reason) ->
        Assert.That(reason, Does.Contain("same-task"))
    | other -> Assert.Fail(sprintf "Duplicate revise was not rejected: %A" other)

    // Also refuse a malformed imported graph; callers cannot bypass
    // the invariant by constructing a snapshot instead of using apply.
    let revisedUnit = { unlinked.Units.["second"] with Spec = revisedSpec }
    let malformed = { unlinked with Units = unlinked.Units.Add("second", revisedUnit) }
    Assert.That(validate malformed |> Result.isError, Is.True)

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
let ``re-cancelling a reopened child never resurrects historical parent acceptance`` () =
    for reason in [ "No longer in scope"; "Different scope decision" ] do
        let original =
            initial ()
            |> fun g -> system g "system" "project"
            |> fun g -> feature g "child" "system" None
            |> fun g -> change g (WorkCompositionMutation.Cancel("child", "No longer in scope"))
            |> fun g -> accepted g "system"
            |> fun g -> accepted g "project"
        let systemProof = (currentAcceptance original "system").Value
        let projectProof = (currentAcceptance original "project").Value
        Assert.That(systemProof.ChildAcceptances.["child"], Is.EqualTo(-1))

        let reopened = change original (WorkCompositionMutation.Reopen "child")
        Assert.That(currentAcceptance reopened "system" |> Option.isNone, Is.True)
        Assert.That(currentAcceptance reopened "project" |> Option.isNone, Is.True)
        let recancelled = change reopened (WorkCompositionMutation.Cancel("child", reason))
        Assert.That(recancelled.Units.["child"].SpecVersion, Is.EqualTo(2))
        for id in [ "system"; "project" ] do
            Assert.That(currentAcceptance recancelled id |> Option.isNone, Is.True,
                        sprintf "Historical %s acceptance resurrected after re-cancellation: %s" id reason)
        Assert.That(recancelled.Units.["system"].Acceptances.[string systemProof.GraphVersion],
                    Is.EqualTo(systemProof))
        Assert.That(recancelled.Units.["project"].Acceptances.[string projectProof.GraphVersion],
                    Is.EqualTo(projectProof))

        // An explicit new proof is required at each ancestor after changing the child.
        let reaccepted = recancelled |> fun g -> accepted g "system"
                                     |> fun g -> accepted g "project"
        Assert.That((currentAcceptance reaccepted "system").Value.ChildAcceptances.["child"],
                    Is.EqualTo(-2))
        Assert.That(currentAcceptance reaccepted "project" |> Option.isSome, Is.True)
        Assert.That(reaccepted.Units.["system"].Acceptances.Count, Is.EqualTo(2))

[<Test>]
let ``revising a still-cancelled child invalidates historical parent acceptance`` () =
    let original =
        initial ()
        |> fun g -> feature g "child" "project" None
        |> fun g -> change g (WorkCompositionMutation.Cancel("child", "Excluded"))
        |> fun g -> accepted g "project"
    let originalProof = (currentAcceptance original "project").Value
    let revisedSpec = { original.Units.["child"].Spec with Scope = "Updated excluded scope" }
    let revised = change original (WorkCompositionMutation.Revise("child", 1, revisedSpec))
    Assert.That(revised.Units.["child"].CancellationReason, Is.EqualTo(Some "Excluded"))
    Assert.That(revised.Units.["child"].SpecVersion, Is.EqualTo(2))
    Assert.That(currentAcceptance revised "project" |> Option.isNone, Is.True)
    Assert.That(revised.Units.["project"].Acceptances.[string originalProof.GraphVersion],
                Is.EqualTo(originalProof))
    let reaccepted = accepted revised "project"
    Assert.That((currentAcceptance reaccepted "project").Value.ChildAcceptances.["child"],
                Is.EqualTo(-2))

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


let private queueStage id subject state dependencies : WorkStage =
    { Id = id
      ProjectId = "Jarvis"
      SubjectId = subject
      Kind = WorkKind.Implementation
      QueueId = "implementation"
      StageGeneration = 1
      State = state
      Domain = DomainState.Undecided
      Gate = GateState.Clear
      ClaimGeneration = 0
      Dependencies = dependencies
      WaivedDependencies = Map.empty
      AvailableAt = 1L
      AvailableSequence = 1L
      SourceLineage = None
      Push = None
      IntegrationAuthorized = false }

let private queueWith stages =
    let project = Jarvis.WorkQueues.Contracts.emptyProject Map.empty
    { project with Works = stages |> List.map (fun (stage: WorkStage) -> stage.Id, stage) |> Map.ofList }

[<Test>]
let ``available stages are ready for project admission only when WQ selector accepts them`` () =
    let graph =
        initial ()
        |> fun g -> feature g "feature" "project" (Some "canonical-task")
    let stage = queueStage "stage" "canonical-task" WorkState.Available Set.empty
    let queue = queueWith [ stage ]
    Assert.That(phase graph (Some queue) "feature", Is.EqualTo(Some WorkUnitPhase.ReadyForAdmission))
    let status = (overview graph (Some queue) "project").Value
    Assert.That(status.Progress.ReadyForAdmission, Is.EqualTo(1))
    Assert.That(status.Progress.Blocked, Is.EqualTo(0))

[<Test>]
let ``available stage under non-clear gate is not locally ready`` () =
    let graph =
        initial ()
        |> fun g -> feature g "feature" "project" (Some "canonical-task")
    let stage = queueStage "stage" "canonical-task" WorkState.Available Set.empty
    for gate in [ GateState.DependencyGatePending 3
                  GateState.IntegrationGateHold(3, "push")
                  GateState.SourceRecoveryBlocked "source must be reconciled" ] do
        let queue = queueWith [ { stage with Gate = gate } ]
        Assert.That(phase graph (Some queue) "feature", Is.EqualTo(Some WorkUnitPhase.Blocked),
                    sprintf "Gate %A must exclude admission" gate)

[<Test>]
let ``incomplete or unsatisfied hard stage graph does not imply readiness`` () =
    let graph =
        initial ()
        |> fun g -> feature g "feature" "project" (Some "canonical-task")
    let upstream = queueStage "upstream" "different-task" WorkState.Available Set.empty
    let dependent = queueStage "stage" "canonical-task" WorkState.Available (Set.singleton "upstream")
    let unfinished = queueWith [ upstream; dependent ]
    Assert.That(phase graph (Some unfinished) "feature", Is.EqualTo(Some WorkUnitPhase.Blocked))
    let completed = queueWith [ { upstream with State = WorkState.Completed }; dependent ]
    Assert.That(phase graph (Some completed) "feature", Is.EqualTo(Some WorkUnitPhase.ReadyForAdmission))
    let missing = queueWith [ dependent ]
    Assert.That(phase graph (Some missing) "feature", Is.EqualTo(Some WorkUnitPhase.Unverified))
    let incomplete = { completed with GraphCovered = false }
    Assert.That(phase graph (Some incomplete) "feature", Is.EqualTo(Some WorkUnitPhase.Unverified))

[<Test>]
let ``frozen project migration never admits an available work stage`` () =
    let graph =
        initial ()
        |> fun g -> feature g "feature" "project" (Some "canonical-task")
    let stage = queueStage "stage" "canonical-task" WorkState.Available Set.empty
    let frozen = { queueWith [ stage ] with Migration = MigrationEpoch.Frozen }
    Assert.That(phase graph (Some frozen) "feature", Is.EqualTo(Some WorkUnitPhase.Blocked))
