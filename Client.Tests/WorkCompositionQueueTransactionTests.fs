module Client.Tests.WorkCompositionQueueTransactionTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueReceiptProducer
open Client.WorkQueueRoleAuthority
open Client.WorkCompositionGitLedger
open Client.WorkCompositionAcceptanceAdapter
open Client.WorkCompositionQueueTransaction
open Jarvis.WorkQueues

[<TestFixture>]
type WorkCompositionQueueTransactionTests() =
    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected original project-ledger failure: %A" error

    let git dir args =
        use command = new Process()
        command.StartInfo.FileName <- "git"
        command.StartInfo.WorkingDirectory <- dir
        command.StartInfo.RedirectStandardOutput <- true
        command.StartInfo.RedirectStandardError <- true
        for arg in args do command.StartInfo.ArgumentList.Add arg
        Assert.That(command.Start(), Is.True)
        let output = command.StandardOutput.ReadToEnd()
        let error = command.StandardError.ReadToEnd()
        if not (command.WaitForExit 20000) then
            command.Kill true
            Assert.Fail("Disposable fixture Git timed out")
        Assert.That(command.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let spec title linked =
        { Title = title
          Goal = "Verify " + title
          Scope = "Offline same-head composition and queue rehearsal"
          AcceptanceCriteria = Set.singleton "Independent original source receipt"
          Constraints = Set.empty
          LinkedTaskId = linked }

    let withClones action =
        let root = Path.Combine(Path.GetTempPath(),
            "jarvis-composition-positive-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote, first, second =
                Path.Combine(root, "remote.git"),
                Path.Combine(root, "first"), Path.Combine(root, "second")
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; first ] |> ignore
            git first [ "config"; "user.name"; "Fixture" ] |> ignore
            git first [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "offline baseline")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "commit"; "-m"; "bootstrap" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; second ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding directory =
                { RepositoryPath = directory
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> ok |> ignore
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Owner", set [ "planning" ]
                              "Reviewer", set [ "review" ] ])
            |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Owner" |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Reviewer" |> ok |> ignore
            WorkQueueRoleAuthority.initializeCatalog a "Fixture" (Guid.NewGuid())
            |> ok |> ignore
            let planner: RoleConfiguration =
                { RoleId = "Owner"; Version = 1; Enabled = true
                  Instructions = "Authenticated original planning fixture"
                  QueueSubscriptions = Set.singleton "planning" }
            WorkQueueRoleAuthority.configure a "Fixture" (Guid.NewGuid()) 0 0 planner
            |> ok |> ignore
            ReviewWorkflow.createTask a (Guid.NewGuid()) "child-validation" "Original prerequisite validation"
            |> ok |> ignore
            let dependentTaskOp = Guid.NewGuid()
            ReviewWorkflow.createTask a dependentTaskOp "delivery-task" "Original dependent Work"
            |> ok |> ignore
            [ WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None)
              WorkCompositionMutation.Create("foundation", WorkUnitKind.System, Some "project", spec "Foundation" None)
              WorkCompositionMutation.Create("delivery", WorkUnitKind.System, Some "project", spec "Delivery" None)
              WorkCompositionMutation.Create("prerequisite", WorkUnitKind.Feature, Some "foundation",
                                             spec "Prerequisite" (Some "child-validation"))
              WorkCompositionMutation.Create("dependent", WorkUnitKind.Feature, Some "delivery",
                                             spec "Dependent" (Some "delivery-task"))
              WorkCompositionMutation.Require("dependent", "prerequisite") ]
            |> List.iteri (fun expected mutation ->
                WorkCompositionGitLedger.execute a "Fixture" (Guid.NewGuid()) expected mutation
                |> ok |> ignore)
            let producerOperationId = Guid.NewGuid()
            WorkQueueReceiptProducer.produce b "Fixture" producerOperationId dependentTaskOp
            |> ok |> ignore
            let _, queue = WorkQueueGitLedger.load a |> ok
            let stage =
                queue.Works |> Map.toList |> List.map snd
                |> List.filter (fun work -> work.SubjectId = "delivery-task")
                |> List.exactlyOne
            Assert.That(stage.Kind, Is.EqualTo(WorkKind.Planning))
            let snapshot = LedgerStore.fetch a |> ok
            let origin, _ =
                WorkQueueGitLedger.readOriginAt a snapshot stage.Id |> ok |> Option.get
            Assert.That(origin.RoleId, Is.EqualTo("Owner"))
            Assert.That(origin.RoleVersion, Is.EqualTo(1))
            Assert.That(origin.SourceRef, Is.Empty)
            Assert.That(origin.SourceCommit, Is.Empty)
            Assert.That(origin.SourceOperationId, Is.EqualTo(dependentTaskOp.ToString("D")))
            let originalProducer = LedgerStore.getOperationAt a snapshot producerOperationId
                                   |> ok |> Option.get
            Assert.That(originalProducer.Paths,
                        Does.Contain("records/work-composition/stage-links/" + stage.Id + ".json"))
            Assert.That(originalProducer.Paths, Does.Contain(WorkQueueGitLedger.RecordPath))
            let link = WorkCompositionGitLedger.readStageLinkAt a snapshot stage.Id |> ok |> Option.get
            Assert.That(link.UnitId, Is.EqualTo("dependent"))
            Assert.That(link.SubjectId, Is.EqualTo("delivery-task"))
            Assert.That(link.SpecVersion, Is.EqualTo(2))
            action a b stage.Id
        finally
            try Directory.Delete(root, true) with _ -> ()

    let request binding workId receipt =
        let snapshot = LedgerStore.fetch binding |> ok
        let graph, _ = WorkCompositionGitLedger.loadAt binding snapshot |> ok
        let queue = WorkQueueGitLedger.loadAt binding snapshot |> ok
        let _, originOid = WorkQueueGitLedger.readOriginAt binding snapshot workId
                           |> ok |> Option.get
        { ProjectId = binding.ProjectId
          SnapshotHead = snapshot.Commit
          GraphVersion = graph.Version
          ExpectedProjectVersion = queue.Version
          AcceptedUnitId = "prerequisite"
          AcceptedSpecVersion = graph.Units.["prerequisite"].SpecVersion
          DependentUnitId = "dependent"
          DependentSpecVersion = graph.Units.["dependent"].SpecVersion
          WorkId = workId
          StageGeneration = queue.Works.[workId].StageGeneration
          OriginOid = originOid
          IntegrationSubmissionId = receipt }

    let approvedChild a b =
        let graph = WorkCompositionGitLedger.load a |> ok |> snd
        let kind = WorkCompositionAcceptanceAdapter.reviewPurpose graph graph.Units.["prerequisite"]
        git a.RepositoryPath [ "checkout"; "-b"; "feature/reviewed-prerequisite" ] |> ignore
        File.WriteAllText(Path.Combine(a.RepositoryPath, "child-proof.txt"),
                          "Independent source evidence")
        git a.RepositoryPath [ "add"; "child-proof.txt" ] |> ignore
        git a.RepositoryPath [ "commit"; "-m"; "Verified original prerequisite source" ] |> ignore
        let sha = git a.RepositoryPath [ "rev-parse"; "HEAD" ]
        let reviewId = Guid.NewGuid()
        let branch = "refs/heads/feature/reviewed-prerequisite"
        let target = "refs/heads/main"
        ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) "child-validation" "Owner"
            reviewId branch sha "Independent prerequisite acceptance" kind target
        |> ok |> ignore
        ReviewWorkflow.claimBranchReview b (Guid.NewGuid()) "child-validation"
            reviewId "Reviewer" 1 |> ok |> ignore
        ReviewWorkflow.decideBranchReview b (Guid.NewGuid()) "child-validation"
            reviewId "Reviewer" 2 Accept |> ok |> ignore
        let submission = Guid.NewGuid()
        ReviewWorkflow.submitIntegration b (Guid.NewGuid()) "child-validation" "Owner"
            submission "" "" (reviewId.ToString("N")) target
        |> ok |> ignore
        git a.RepositoryPath [ "checkout"; "main" ] |> ignore
        git a.RepositoryPath [ "merge"; "--ff-only"; sha ] |> ignore
        git a.RepositoryPath [ "push"; "origin"; "main" ] |> ignore
        ReviewWorkflow.completeIntegration b (Guid.NewGuid()) "child-validation"
            "Reviewer" submission sha |> ok |> ignore
        submission

    let failWithoutCommit binding op action =
        let before = LedgerStore.fetch binding |> ok
        let originalSeq = before.Header.NextSequence
        match action () with
        | Error(InvalidBinding _)
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected fail-closed original CAS: %A" other)
        let after = LedgerStore.fetch binding |> ok
        Assert.That(after.Commit, Is.EqualTo(before.Commit))
        Assert.That(after.Header.NextSequence, Is.EqualTo(originalSeq))
        Assert.That(LedgerStore.getOperationAt binding after op |> ok |> Option.isNone, Is.True)

    [<Test>]
    member _.OriginalWq04BlockedThenAcceptedGraphAndWq02ReleaseShareOneCASAndRevokeOnCancel() =
        withClones (fun a b workId ->
            let initial = request b workId Guid.Empty
            let graph = WorkCompositionGitLedger.load b |> ok |> snd
            Assert.That(WorkComposition.phase graph None "dependent",
                        Is.EqualTo(Some WorkUnitPhase.Blocked))
            let initialQueue = WorkQueueGitLedger.load b |> ok |> snd
            Assert.That(initialQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            // WQ04 producer blocked the stage IN its creation CAS; there is
            // no intervening Available head and no optional second CAS hold.
            let heldQueue = WorkQueueGitLedger.load a |> ok |> snd
            Assert.That(heldQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(heldQueue.Migration, Is.EqualTo(MigrationEpoch.Frozen))
            let integratedId = approvedChild a b
            let original = request a workId integratedId
            let before = LedgerStore.fetch a |> ok
            let releaseId = Guid.NewGuid()
            let result = WorkCompositionQueueTransaction.acceptAndRelease
                             b "Fixture" releaseId original |> ok
            Assert.That(result.AlreadyApplied, Is.False)
            let reread = WorkCompositionQueueTransaction.acceptAndRelease
                             a "Fixture" releaseId original |> ok
            Assert.That(reread.AlreadyApplied, Is.True)
            let after = LedgerStore.fetch a |> ok
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence + 1L))
            let written = LedgerStore.getOperationAt a after releaseId |> ok |> Option.get
            Assert.That(written.Paths |> Array.sort |> String.concat "|",
                        Is.EqualTo([| WorkCompositionGitLedger.RecordPath
                                      WorkQueueGitLedger.RecordPath |] |> Array.sort |> String.concat "|"))
            let acceptedGraph, _ = WorkCompositionGitLedger.loadAt a after |> ok
            let releasedQueue = WorkQueueGitLedger.loadAt a after |> ok
            Assert.That(WorkComposition.currentAcceptance acceptedGraph "prerequisite" |> Option.isSome, Is.True)
            let overview = WorkComposition.overview acceptedGraph (Some releasedQueue) "dependent"
                           |> Option.get
            Assert.That(overview.UnmetPrerequisites, Is.Empty)
            // Frozen migration still presents no launch/readiness capability.
            Assert.That(overview.Phase, Is.EqualTo(WorkUnitPhase.Blocked))
            Assert.That(releasedQueue.Works.[workId].State, Is.EqualTo(WorkState.Available))
            Assert.That(releasedQueue.Migration, Is.EqualTo(MigrationEpoch.Frozen))
            let changed = request b workId integratedId
            let revokeId = Guid.NewGuid()
            WorkCompositionGitLedger.execute a "Fixture" revokeId changed.GraphVersion
                (WorkCompositionMutation.Cancel("prerequisite", "New objection"))
            |> ok |> ignore
            let atomic = LedgerStore.fetch a |> ok
            let receipt = LedgerStore.getOperationAt a atomic revokeId |> ok |> Option.get
            Assert.That(receipt.Paths.Length, Is.EqualTo(2))
            Assert.That(receipt.Paths |> Array.contains WorkQueueGitLedger.RecordPath, Is.True)
            let revertedHead = LedgerStore.fetch b |> ok
            let revertedGraph, _ = WorkCompositionGitLedger.loadAt b revertedHead |> ok
            let revertedQueue = WorkQueueGitLedger.loadAt b revertedHead |> ok
            Assert.That(WorkComposition.currentAcceptance revertedGraph "prerequisite" |> Option.isNone, Is.True)
            Assert.That(revertedQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(Contracts.orderAvailable revertedQueue |> ok
                        |> List.exists (fun stage -> stage.Id = workId), Is.False)
            let reopening = request b workId integratedId
            WorkCompositionQueueTransaction.revokeOnGraphMutation b "Fixture"
                (Guid.NewGuid()) reopening (WorkCompositionMutation.Reopen "prerequisite")
            |> ok |> ignore
            let stillHeld = WorkQueueGitLedger.load a |> ok |> snd
            Assert.That(stillHeld.Works.[workId].State, Is.EqualTo(WorkState.Blocked)))

    [<Test>]
    member _.TwoIndependentClonesRacingAcceptanceProduceOneWinnerWithExactUuidReplay() =
        withClones (fun a b workId ->
            Assert.That((WorkQueueGitLedger.load a |> ok |> snd).Works.[workId].State,
                        Is.EqualTo(WorkState.Blocked))
            let approved = approvedChild a b
            let input = request a workId approved
            let firstId, secondId = Guid.NewGuid(), Guid.NewGuid()
            let before = LedgerStore.fetch a |> ok
            let first = Task.Run(fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" firstId input)
            let second = Task.Run(fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" secondId input)
            Task.WaitAll [| first :> Task; second :> Task |]
            let outcomes = [ firstId, first.Result; secondId, second.Result ]
            let accepted = outcomes |> List.choose (function id, Ok _ -> Some id | _ -> None)
            Assert.That(accepted.Length, Is.EqualTo(1))
            let loser = outcomes |> List.find (fun (id, _) -> id <> accepted.Head)
            match snd loser with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale-head loser was not fenced: %A" other)
            let after = LedgerStore.fetch b |> ok
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence + 1L))
            Assert.That(LedgerStore.getOperationAt b after (fst loser) |> ok |> Option.isNone, Is.True)
            let winner = accepted.Head
            let replay = WorkCompositionQueueTransaction.acceptAndRelease
                            a "Fixture" winner input |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            let loaded = WorkQueueGitLedger.load b |> ok |> snd
            Assert.That(loaded.Works.[workId].State, Is.EqualTo(WorkState.Available))
            Assert.That(loaded.Receipts.ContainsKey(winner.ToString("D")), Is.True))

    [<Test>]
    member _.WrongOriginStaleHeadAndUnreviewedApprovalNeverMintOriginalReceipt() =
        withClones (fun a b workId ->
            let source = request b workId (Guid.NewGuid())
            let forgery = { source with OriginOid = String.replicate 40 "a" }
            let forgedId = Guid.NewGuid()
            failWithoutCommit b forgedId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" forgedId forgery)
            let failedApproval = request a workId (Guid.NewGuid())
            let denialId = Guid.NewGuid()
            failWithoutCommit a denialId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" denialId failedApproval)
            let graph = WorkCompositionGitLedger.load a |> ok |> snd
            WorkCompositionGitLedger.execute a "Fixture" (Guid.NewGuid()) graph.Version
                (WorkCompositionMutation.Revise("foundation",
                    graph.Units.["foundation"].SpecVersion,
                    spec "Unrelated foundation revision" None))
            |> ok |> ignore
            let staleId = Guid.NewGuid()
            failWithoutCommit a staleId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" staleId source)
            let head = LedgerStore.fetch a |> ok
            let queue = WorkQueueGitLedger.loadAt a head |> ok
            Assert.That(queue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(queue.Migration, Is.EqualTo(MigrationEpoch.Frozen)))


    [<TestCase("linked-task")>]
    [<TestCase("spec")>]
    [<TestCase("parent")>]
    [<TestCase("hard-edge")>]
    [<TestCase("ancestor-spec")>]
    member _.RelevantMutationAtomicallyRevokesOldStageAndReplaysExactOriginalUuid(kind: string) =
        withClones (fun a b workId ->
            let accepted = approvedChild a b
            let releaseId = Guid.NewGuid()
            WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" releaseId
                (request b workId accepted) |> ok |> ignore
            Assert.That((WorkQueueGitLedger.load a |> ok |> snd).Works.[workId].State,
                        Is.EqualTo(WorkState.Available))
            let graph = WorkCompositionGitLedger.load a |> ok |> snd
            let dependent = graph.Units.["dependent"]
            let mutation =
                match kind with
                | "linked-task" ->
                    WorkCompositionMutation.Revise("dependent", dependent.SpecVersion,
                        { dependent.Spec with LinkedTaskId = Some "replacement-task" })
                | "spec" ->
                    WorkCompositionMutation.Revise("dependent", dependent.SpecVersion,
                        { dependent.Spec with Goal = "Revised success criteria" })
                | "parent" ->
                    WorkCompositionMutation.Move("dependent", Some "foundation")
                | "hard-edge" ->
                    WorkCompositionMutation.Unrequire("dependent", "prerequisite")
                | "ancestor-spec" ->
                    let parent = graph.Units.["delivery"]
                    WorkCompositionMutation.Revise("delivery", parent.SpecVersion,
                        { parent.Spec with Scope = "Altered ancestor specification" })
                | _ -> failwith "Unknown mutation fixture"
            let mutationId = Guid.NewGuid()
            let before = LedgerStore.fetch a |> ok
            let change = WorkCompositionGitLedger.execute b "Fixture" mutationId
                            graph.Version mutation |> ok
            Assert.That(change.AlreadyApplied, Is.False)
            let after = LedgerStore.fetch a |> ok
            Assert.That(after.Header.NextSequence, Is.EqualTo(before.Header.NextSequence + 1L))
            let original = LedgerStore.getOperationAt a after mutationId |> ok |> Option.get
            Assert.That(original.Paths,
                        Is.EquivalentTo([| WorkCompositionGitLedger.RecordPath;
                                          WorkQueueGitLedger.RecordPath |]))
            let graphAfter, _ = WorkCompositionGitLedger.loadAt a after |> ok
            let queueAfter = WorkQueueGitLedger.loadAt a after |> ok
            Assert.That(queueAfter.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(queueAfter.Works.[workId].AvailableSequence, Is.Zero)
            Assert.That(Contracts.orderAvailable queueAfter |> ok
                        |> List.exists (fun work -> work.Id = workId), Is.False)
            // Two-clone original UUID replay never reopens the old stage.
            let replay = WorkCompositionGitLedger.execute a "Fixture" mutationId
                            graph.Version mutation |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That((WorkQueueGitLedger.load b |> ok |> snd).Works.[workId].State,
                        Is.EqualTo(WorkState.Blocked))
            let oldLink = WorkCompositionGitLedger.readStageLinkAt b after workId |> ok |> Option.get
            Assert.That(oldLink.SpecVersion, Is.EqualTo(2))
            Assert.That(WorkCompositionGitLedger.stageLinkStillCurrent oldLink
                            queueAfter.Works.[workId] graphAfter, Is.False))

    [<Test>]
    member _.DuplicateTaskLinkAfterReleaseIsRejectedOnCreateAndReviseAcrossTwoClones() =
        withClones (fun a b workId ->
            let accepted = approvedChild a b
            WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" (Guid.NewGuid())
                (request a workId accepted) |> ok |> ignore
            let released = LedgerStore.fetch b |> ok
            let originalGraph, _ = WorkCompositionGitLedger.loadAt b released |> ok
            let originalQueue = WorkQueueGitLedger.loadAt b released |> ok
            Assert.That(originalQueue.Works.[workId].State, Is.EqualTo(WorkState.Available))

            // Clone B cannot create a second owner of an already released
            // original WQ04 task. Neither graph nor queue may commit.
            let duplicateCreate =
                WorkCompositionMutation.Create("duplicate-link", WorkUnitKind.Feature,
                    Some "foundation", spec "Duplicate link" (Some "delivery-task"))
            let rejectedCreateId = Guid.NewGuid()
            failWithoutCommit b rejectedCreateId (fun () ->
                WorkCompositionGitLedger.execute b "Fixture" rejectedCreateId
                    originalGraph.Version duplicateCreate)

            // An unrelated feature can still be created. Clone A advances
            // the original head; clone B must fence a stale competing UUID.
            let unlinkedCreate =
                WorkCompositionMutation.Create("duplicate-link", WorkUnitKind.Feature,
                    Some "foundation", spec "Duplicate link" None)
            let createId = Guid.NewGuid()
            WorkCompositionGitLedger.execute a "Fixture" createId
                originalGraph.Version unlinkedCreate |> ok |> ignore
            let staleId = Guid.NewGuid()
            let priorStale = LedgerStore.fetch b |> ok
            match WorkCompositionGitLedger.execute b "Fixture" staleId
                    originalGraph.Version duplicateCreate with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale-head duplicate was not fenced: %A" other)
            let afterStale = LedgerStore.fetch b |> ok
            Assert.That(afterStale.Commit, Is.EqualTo(priorStale.Commit))
            Assert.That(LedgerStore.getOperationAt b afterStale staleId |> ok |> Option.isNone,
                        Is.True)

            // Require an unaccepted system in the other branch: this second
            // feature is now graph-Blocked, but is NOT linked to the old task.
            let withNewUnit = WorkCompositionGitLedger.load b |> ok |> snd
            let blockedMutation = WorkCompositionMutation.Require("duplicate-link", "delivery")
            let requireId = Guid.NewGuid()
            let required = WorkCompositionGitLedger.execute b "Fixture" requireId
                               withNewUnit.Version blockedMutation |> ok
            Assert.That(required.AlreadyApplied, Is.False)
            let replay = WorkCompositionGitLedger.execute a "Fixture" requireId
                             withNewUnit.Version blockedMutation |> ok
            Assert.That(replay.AlreadyApplied, Is.True)

            // Revise is the other entry point for introducing an owner:
            // neither independent clone can smuggle a duplicate link into
            // the graph after an ordinary Require.
            let blockedGraph = WorkCompositionGitLedger.load a |> ok |> snd
            let secondUnit = blockedGraph.Units.["duplicate-link"]
            let duplicateRevision =
                WorkCompositionMutation.Revise("duplicate-link", secondUnit.SpecVersion,
                    { secondUnit.Spec with LinkedTaskId = Some "delivery-task" })
            let rejectedRevisionId = Guid.NewGuid()
            failWithoutCommit a rejectedRevisionId (fun () ->
                WorkCompositionGitLedger.execute a "Fixture" rejectedRevisionId
                    blockedGraph.Version duplicateRevision)
            failWithoutCommit b rejectedRevisionId (fun () ->
                WorkCompositionGitLedger.execute b "Fixture" rejectedRevisionId
                    blockedGraph.Version duplicateRevision)

            let canonical = LedgerStore.fetch b |> ok
            let graph, _ = WorkCompositionGitLedger.loadAt b canonical |> ok
            let queue = WorkQueueGitLedger.loadAt b canonical |> ok
            Assert.That(canonical.Header.NextSequence,
                        Is.EqualTo(released.Header.NextSequence + 2L))
            Assert.That(graph.Units.["duplicate-link"].Spec.LinkedTaskId, Is.EqualTo(None))
            Assert.That(WorkComposition.phase graph (Some queue) "duplicate-link",
                        Is.EqualTo(Some WorkUnitPhase.Blocked))
            Assert.That(graph.Units.["dependent"].Spec.LinkedTaskId,
                        Is.EqualTo(Some "delivery-task"))
            Assert.That(queue.Works.[workId].State, Is.EqualTo(WorkState.Available))
            Assert.That(LedgerStore.getOperationAt b canonical rejectedCreateId |> ok |> Option.isNone,
                        Is.True)
            Assert.That(LedgerStore.getOperationAt b canonical staleId |> ok |> Option.isNone,
                        Is.True)
            Assert.That(LedgerStore.getOperationAt b canonical rejectedRevisionId |> ok |> Option.isNone,
                        Is.True))

    [<Test>]
    member _.UnrelatedGraphEditDoesNotRevokeVerifiedOriginalBinding() =
        withClones (fun a b workId ->
            let accepted = approvedChild a b
            WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" (Guid.NewGuid())
                (request a workId accepted) |> ok |> ignore
            let graph = WorkCompositionGitLedger.load b |> ok |> snd
            let unaffectedId = Guid.NewGuid()
            WorkCompositionGitLedger.execute b "Fixture" unaffectedId graph.Version
                (WorkCompositionMutation.Create("unrelated-feature", WorkUnitKind.Feature,
                    Some "foundation", spec "Unrelated feature" None))
            |> ok |> ignore
            let final = LedgerStore.fetch a |> ok
            let op = LedgerStore.getOperationAt a final unaffectedId |> ok |> Option.get
            Assert.That(op.Paths.Length, Is.EqualTo(1))
            Assert.That(op.Paths.[0], Is.EqualTo(WorkCompositionGitLedger.RecordPath))
            Assert.That((WorkQueueGitLedger.load a |> ok |> snd).Works.[workId].State,
                        Is.EqualTo(WorkState.Available)))


    [<Test>]
    member _.DependencyBlockedStageGainsNonWakingGraphHoldInGraphCas() =
        withClones (fun a b workId ->
            let accepted = approvedChild a b
            WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" (Guid.NewGuid())
                (request a workId accepted) |> ok |> ignore
            let first = WorkQueueGitLedger.load b |> ok |> snd
            let synthetic =
                { first.Works.[workId] with
                    Id = "fixture-unlinked-prerequisite"
                    SubjectId = "fixture-offline-source"
                    StageGeneration = 1
                    State = WorkState.Available
                    Gate = GateState.Clear
                    Dependencies = Set.empty
                    AvailableAt = 0L
                    AvailableSequence = 0L }
            WorkQueueGitLedger.executeInertFixture b "Fixture" (Guid.NewGuid())
                first.Version (WorkMutation.PublishStage synthetic) |> ok |> ignore
            let second = WorkQueueGitLedger.load a |> ok |> snd
            WorkQueueGitLedger.executeInertFixture a "Fixture" (Guid.NewGuid())
                second.Version
                (WorkMutation.SetHardPrerequisite(workId, synthetic.Id)) |> ok |> ignore
            let heldByQueue = WorkQueueGitLedger.load b |> ok |> snd
            Assert.That(heldByQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(heldByQueue.Works.[workId].Gate, Is.EqualTo(GateState.Clear))
            let graph = WorkCompositionGitLedger.load a |> ok |> snd
            let dependent = graph.Units.["dependent"]
            let invalidationId = Guid.NewGuid()
            WorkCompositionGitLedger.execute b "Fixture" invalidationId graph.Version
                (WorkCompositionMutation.Revise("dependent", dependent.SpecVersion,
                    { dependent.Spec with LinkedTaskId = Some "replacement-task" }))
            |> ok |> ignore
            let pinned = LedgerStore.fetch a |> ok
            let committed = LedgerStore.getOperationAt a pinned invalidationId |> ok |> Option.get
            Assert.That(committed.Paths, Does.Contain(WorkQueueGitLedger.RecordPath))
            let fenced = WorkQueueGitLedger.loadAt a pinned |> ok
            Assert.That(fenced.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(fenced.Works.[workId].Gate,
                        Is.EqualTo(GateState.CompositionHold("dependent", dependent.SpecVersion)))
            // Removing WQ02's final dependency would normally wake a Blocked
            // stage. This immutable graph hold must still prevent resurrection.
            WorkQueueGitLedger.executeInertFixture b "Fixture" (Guid.NewGuid())
                fenced.Version
                (WorkMutation.RemoveHardPrerequisite(workId, synthetic.Id)) |> ok |> ignore
            let after = WorkQueueGitLedger.load a |> ok |> snd
            Assert.That(after.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(after.Works.[workId].Gate,
                        Is.EqualTo(GateState.CompositionHold("dependent", dependent.SpecVersion))))

    [<Test>]
    member _.CancelledParentMustNotReleaseHeldOriginalWq04StageWhenPrerequisiteBecomesAccepted() =
        withClones (fun a b workId ->
            let approved = approvedChild a b
            let graph = WorkCompositionGitLedger.load a |> ok |> snd
            WorkCompositionGitLedger.execute a "Fixture" (Guid.NewGuid()) graph.Version
                (WorkCompositionMutation.Cancel("delivery", "Cancel parent"))
            |> ok |> ignore
            let before = LedgerStore.fetch b |> ok
            let held = WorkQueueGitLedger.loadAt b before |> ok
            Assert.That(held.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(held.Works.[workId].Gate,
                        Is.EqualTo(GateState.CompositionHold("dependent", 2)))
            let attemptId = Guid.NewGuid()
            let frozen = request b workId approved
            failWithoutCommit b attemptId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" attemptId frozen)
            let after = LedgerStore.fetch a |> ok
            let finalGraph, _ = WorkCompositionGitLedger.loadAt a after |> ok
            let finalQueue = WorkQueueGitLedger.loadAt a after |> ok
            Assert.That(WorkComposition.currentAcceptance finalGraph "prerequisite", Is.EqualTo(None))
            Assert.That(finalQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(finalQueue.Works.[workId].Gate,
                        Is.EqualTo(GateState.CompositionHold("dependent", 2)))
            Assert.That(Contracts.orderAvailable finalQueue |> ok
                        |> List.exists (fun stage -> stage.Id = workId), Is.False))

    [<Test>]
    member _.ReopenedAndRecancelledParentCannotResurrectStaleOriginalWq04Binding() =
        withClones (fun a b workId ->
            let approved = approvedChild a b
            let oldHead = request a workId approved
            let graph = WorkCompositionGitLedger.load a |> ok |> snd
            WorkCompositionGitLedger.execute a "Fixture" (Guid.NewGuid()) graph.Version
                (WorkCompositionMutation.Cancel("delivery", "Cancel parent"))
            |> ok |> ignore
            let staleId = Guid.NewGuid()
            failWithoutCommit b staleId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" staleId oldHead)
            let cancelled = WorkCompositionGitLedger.load b |> ok |> snd
            WorkCompositionGitLedger.execute b "Fixture" (Guid.NewGuid()) cancelled.Version
                (WorkCompositionMutation.Reopen "delivery")
            |> ok |> ignore
            // Reopening does not restore original immutable source-context
            // provenance. Only a new WQ04 stage birth can update that binding.
            let reopened = request a workId approved
            let releaseId = Guid.NewGuid()
            failWithoutCommit b releaseId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture" releaseId reopened)
            let reopenedHead = LedgerStore.fetch b |> ok
            let reopenedGraph, _ = WorkCompositionGitLedger.loadAt b reopenedHead |> ok
            let reopenedQueue = WorkQueueGitLedger.loadAt b reopenedHead |> ok
            let link = WorkCompositionGitLedger.readStageLinkAt b reopenedHead workId
                       |> ok |> Option.get
            Assert.That(WorkCompositionGitLedger.stageLinkStillCurrent
                            link reopenedQueue.Works.[workId] reopenedGraph, Is.False)
            Assert.That(reopenedQueue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            WorkCompositionGitLedger.execute a "Fixture" (Guid.NewGuid()) reopenedGraph.Version
                (WorkCompositionMutation.Cancel("delivery", "Recancel parent"))
            |> ok |> ignore
            let recancelledId = Guid.NewGuid()
            failWithoutCommit b recancelledId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease b "Fixture"
                    recancelledId (request b workId approved))
            let final = LedgerStore.fetch b |> ok
            let blocked = WorkQueueGitLedger.loadAt b final |> ok
            Assert.That(blocked.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(blocked.Works.[workId].Gate,
                        Is.EqualTo(GateState.CompositionHold("dependent", 2)))
            Assert.That(Contracts.orderAvailable blocked |> ok
                        |> List.exists (fun stage -> stage.Id = workId), Is.False))
