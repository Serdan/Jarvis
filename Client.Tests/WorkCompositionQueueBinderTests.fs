module Client.Tests.WorkCompositionQueueBinderTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkCompositionGitLedger
open Client.WorkCompositionQueueBinder
open Client.ReviewWorkflow
open Client.WorkQueueReceiptProducer
open Jarvis.WorkQueues

[<TestFixture>]
type WorkCompositionQueueBinderTests() =
    let ok = function
        | Ok item -> item
        | Error reason -> failwithf "Fixture failed: %A" reason

    let git cwd args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- cwd
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        for arg in args do runner.StartInfo.ArgumentList.Add arg
        if not (runner.Start()) then failwith "Fixture git did not start."
        let output = runner.StandardOutput.ReadToEnd()
        let error = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit 20000) then
            runner.Kill true
            failwith "Fixture git timed out."
        Assert.That(runner.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let spec title linked =
        { Title = title
          Goal = "Verify " + title
          Scope = "Isolated fake-only acceptance"
          AcceptanceCriteria = Set.singleton "Independent evidence"
          Constraints = Set.empty
          LinkedTaskId = linked }

    let withClones run =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-composition-wq02-held-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let a = Path.Combine(root, "a")
            let b = Path.Combine(root, "b")
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; a ] |> ignore
            git a [ "config"; "user.name"; "Fixture" ] |> ignore
            git a [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "only offline fixture source")
            git a [ "add"; "source.txt" ] |> ignore
            git a [ "commit"; "-m"; "fixture" ] |> ignore
            git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; b ] |> ignore
            let project, ledger = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = project
                  LedgerId = ledger
                  Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> ok |> ignore
            WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Reviewer", Set.singleton "review" ])
            |> ok |> ignore

            // Canonical WQ02 sees the stage Available. The separately
            // reviewed composition graph must deny it until the hard
            // cross-System/Feature prerequisite has genuinely completed.
            let state = WorkQueueGitLedger.load first |> ok |> snd
            let stage: WorkStage =
                { Id = "fixture-reviewed-work"
                  ProjectId = project.ToString("D")
                  SubjectId = "delivery-task"
                  Kind = WorkKind.FrozenReview
                  QueueId = "review"
                  StageGeneration = 1
                  State = WorkState.Available
                  Domain = DomainState.Undecided
                  Gate = GateState.Clear
                  ClaimGeneration = 0
                  Dependencies = Set.empty
                  WaivedDependencies = Map.empty
                  AvailableAt = 0L
                  AvailableSequence = 0L
                  SourceLineage = None
                  Push = None
                  IntegrationAuthorized = false }
            WorkQueueGitLedger.executeInertFixture first "Fixture" (Guid.NewGuid())
                state.Version (PublishStage stage) |> ok |> ignore

            [ WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None)
              WorkCompositionMutation.Create("foundation", WorkUnitKind.System, Some "project", spec "Foundation" None)
              WorkCompositionMutation.Create("delivery", WorkUnitKind.System, Some "project", spec "Delivery" None)
              WorkCompositionMutation.Create("prerequisite", WorkUnitKind.Feature, Some "foundation", spec "Prerequisite" None)
              WorkCompositionMutation.Create("dependent", WorkUnitKind.Feature, Some "delivery", spec "Dependent" (Some "delivery-task"))
              WorkCompositionMutation.Require("dependent", "prerequisite") ]
            |> List.iteri (fun version mutation ->
                WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) version mutation
                |> ok |> ignore)

            let snapshot = LedgerStore.fetch second |> ok
            let queue = WorkQueueGitLedger.loadAt second snapshot |> ok
            let request: CompositionQueueBindRequest =
                { ProjectId = project
                  SnapshotHead = snapshot.Commit
                  UnitId = "dependent"
                  UnitSpecVersion = 2 // Require bumps specification generation
                  GraphVersion = 6
                  ExpectedProjectVersion = queue.Version
                  WorkId = stage.Id
                  StageGeneration = 1
                  SameHeadWorkOriginOid = String.replicate 40 "a"
                  IntegrationSubmissionId = Guid.NewGuid() }
            run first second snapshot request
        finally
            try Directory.Delete(root, true) with _ -> ()

    let issues = function
        | RecoveryBlocked (_, blockers) -> blockers

    let assertDenied result =
        match result with
        | Error(InvalidBinding message) ->
            Assert.That(message, Does.Contain("RecoveryBlocked"))
            Assert.That(message, Does.Contain("atomicity unproven"))
        | other -> Assert.Fail(sprintf "A missing atomic writer must never publish: %A" other)

    let hasAtomic issue =
        issue |> List.exists (function SameHeadAtomicWriterUnavailable _ -> true | _ -> false)

    [<Test>]
    member _.CrossSystemPrerequisiteMismatchIsVisibleAndCannotBecomeWorkReadiness() =
        withClones (fun first second before request ->
            let a = WorkCompositionQueueBinder.inspect first request |> issues
            let b = WorkCompositionQueueBinder.inspect second request |> issues
            for problems in [ a; b ] do
                Assert.That(hasAtomic problems, Is.True)
                Assert.That(problems |> List.exists (function UnacceptedDependency "prerequisite" -> true | _ -> false), Is.True)
                Assert.That(problems |> List.exists (function ContradictoryReadiness "fixture-reviewed-work" -> true | _ -> false), Is.True)
                Assert.That(problems |> List.exists (function MissingOrChangedOrigin _ -> true | _ -> false), Is.True)
                Assert.That(problems |> List.exists (function UnverifiedIntegration _ -> true | _ -> false), Is.True)
            let original = Guid.NewGuid()
            assertDenied (WorkCompositionQueueBinder.attemptUnderCas second "Fixture" original request)
            assertDenied (WorkCompositionQueueBinder.attemptUnderCas first "Fixture" original request)
            let after = LedgerStore.fetch second |> ok
            Assert.That(after.Commit, Is.EqualTo(before.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence))
            Assert.That(LedgerStore.getOperationAt second after original |> ok, Is.EqualTo(None)))

    [<Test>]
    member _.ForgedIdentityStaleGenerationAndRandomReceiptCannotBypassClosedWriter() =
        withClones (fun _ second before request ->
            let changed =
                [ { request with ProjectId = Guid.NewGuid() }
                  { request with SnapshotHead = String.replicate 40 "b" }
                  { request with GraphVersion = request.GraphVersion - 1 }
                  { request with UnitSpecVersion = 1 }
                  { request with StageGeneration = 99 }
                  { request with SameHeadWorkOriginOid = String.replicate 40 "f" }
                  { request with IntegrationSubmissionId = Guid.Empty } ]
            for attempted in changed do
                let gaps = WorkCompositionQueueBinder.inspect second attempted |> issues
                Assert.That(hasAtomic gaps, Is.True)
                assertDenied (WorkCompositionQueueBinder.attemptUnderCas second "Fixture" (Guid.NewGuid()) attempted)
            let after = LedgerStore.fetch second |> ok
            Assert.That(after.Commit, Is.EqualTo(before.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence)))

    [<Test>]
    member _.CompetingDisposableClonesCannotCreatePartialGraphOrQueueReceipt() =
        withClones (fun first second before request ->
            let aId, bId = Guid.NewGuid(), Guid.NewGuid()
            let a = Task.Run(fun () -> WorkCompositionQueueBinder.attemptUnderCas first "Fixture" aId request)
            let b = Task.Run(fun () -> WorkCompositionQueueBinder.attemptUnderCas second "Fixture" bId request)
            Task.WaitAll [| a :> Task; b :> Task |]
            assertDenied a.Result
            assertDenied b.Result
            let after = LedgerStore.fetch first |> ok
            Assert.That(after.Commit, Is.EqualTo(before.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence))
            for original in [ aId; bId ] do
                Assert.That(LedgerStore.getOperationAt first after original |> ok, Is.EqualTo(None))
            let graph = WorkCompositionGitLedger.load second |> ok |> snd
            let queue = WorkQueueGitLedger.load second |> ok |> snd
            Assert.That(graph.Version, Is.EqualTo(6))
            Assert.That(queue.Works.[request.WorkId].State, Is.EqualTo(WorkState.Available))
            // It remains a diagnostic inconsistency, never an authorized repair.
            Assert.That(hasAtomic (WorkCompositionQueueBinder.inspect first request |> issues), Is.True))

    [<Test>]
    member _.ConcurrentCanonicalGraphAdvanceInvalidatesPinnedHeadWithoutPublishingWork() =
        withClones (fun first second before request ->
            WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 6
                (WorkCompositionMutation.Unrequire("dependent", "prerequisite"))
            |> ok |> ignore
            let gaps = WorkCompositionQueueBinder.inspect second request |> issues
            Assert.That(hasAtomic gaps, Is.True)
            Assert.That(gaps |> List.exists (function ChangedHead _ -> true | _ -> false), Is.True)
            Assert.That(gaps |> List.exists (function StaleGraph _ -> true | _ -> false), Is.True)
            let fresh = LedgerStore.fetch second |> ok
            Assert.That(fresh.Commit, Is.Not.EqualTo(before.Commit))
            let bindId = Guid.NewGuid()
            assertDenied (WorkCompositionQueueBinder.attemptUnderCas second "Fixture" bindId request)
            let after = LedgerStore.fetch second |> ok
            Assert.That(after.Commit, Is.EqualTo(fresh.Commit))
            Assert.That(LedgerStore.getOperationAt second after bindId |> ok, Is.EqualTo(None)))


    [<Test>]
    member _.OriginalWq04ProducerCannotSatisfySameHeadRoleAndSourceLineageContract() =
        withClones (fun first second _ request ->
            // Exercise the real WQ04 canonical producer and its immutable
            // original CreateTask receipt rather than an invented stage origin.
            let taskOperation = Guid.NewGuid()
            ReviewWorkflow.createTask first taskOperation "delivery-task" "Original WQ04 task"
            |> ok |> ignore
            let producerOperation = Guid.NewGuid()
            WorkQueueReceiptProducer.produce second "Fixture" producerOperation taskOperation
            |> ok |> ignore

            let head, project = WorkQueueGitLedger.load first |> ok
            let stage =
                project.Works
                |> Map.toList
                |> List.map snd
                |> List.filter (fun work -> work.SubjectId = "delivery-task"
                                             && work.Id <> request.WorkId)
                |> List.exactlyOne
            let origin, originOid =
                WorkQueueGitLedger.readOriginAt first head stage.Id
                |> ok |> Option.get

            // WQ04.PracticalProducer deliberately uses NoRoleTransition and
            // does not carry original source/role lineage for TaskRequested.
            Assert.That(origin.SourceOperationId, Is.EqualTo(taskOperation.ToString("D")))
            Assert.That(origin.SourceOperationKind, Is.EqualTo("CreateTask"))
            Assert.That(origin.RoleId, Is.Empty)
            Assert.That(origin.RoleVersion, Is.Zero)
            Assert.That(origin.SourceRef, Is.Empty)
            Assert.That(origin.SourceCommit, Is.Empty)
            // Original WQ04 now makes the graph hold permanent in the
            // publication CAS; this does not manufacture role/source authority.
            Assert.That(stage.State, Is.EqualTo(WorkState.Blocked))
            Assert.That(stage.Gate, Is.EqualTo(GateState.CompositionHold("dependent", 2)))

            let original: CompositionQueueBindRequest =
                { request with
                    SnapshotHead = head.Commit
                    ExpectedProjectVersion = project.Version
                    WorkId = stage.Id
                    StageGeneration = stage.StageGeneration
                    SameHeadWorkOriginOid = originOid }
            let blockers = WorkCompositionQueueBinder.inspect second original |> issues
            Assert.That(blockers |> List.exists (function
                | UnprovenRoleOrSource _ -> true
                | _ -> false), Is.True)
            Assert.That(blockers |> List.exists (function
                | UnacceptedDependency "prerequisite" -> true
                | _ -> false), Is.True)
            Assert.That(blockers |> List.exists (function
                | ContradictoryReadiness id when id = stage.Id -> true
                | _ -> false), Is.False)
            Assert.That(hasAtomic blockers, Is.True)

            let attemptId = Guid.NewGuid()
            assertDenied (WorkCompositionQueueBinder.attemptUnderCas
                second "Fixture" attemptId original)
            let after = LedgerStore.fetch first |> ok
            Assert.That(after.Commit, Is.EqualTo(head.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(head.Header.NextSequence))
            Assert.That(LedgerStore.getOperationAt first after attemptId |> ok, Is.EqualTo(None)))
