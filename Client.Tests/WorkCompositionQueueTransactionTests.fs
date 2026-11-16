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
            WorkQueueReceiptProducer.produce b "Fixture" (Guid.NewGuid()) dependentTaskOp
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
            Assert.That(origin.RoleId, Is.Empty)
            Assert.That(origin.RoleVersion, Is.Zero)
            Assert.That(origin.SourceRef, Is.Empty)
            Assert.That(origin.SourceCommit, Is.Empty)
            Assert.That(origin.SourceOperationId, Is.EqualTo(dependentTaskOp.ToString("D")))
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
            Assert.That(initialQueue.Works.[workId].State, Is.EqualTo(WorkState.Available))
            WorkCompositionQueueTransaction.holdBlocked b "Fixture"
                (Guid.NewGuid()) initial |> ok |> ignore
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
            WorkCompositionQueueTransaction.holdBlocked a "Fixture"
                (Guid.NewGuid()) (request a workId Guid.Empty) |> ok |> ignore
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
                WorkCompositionQueueTransaction.holdBlocked b "Fixture" forgedId forgery)
            WorkCompositionQueueTransaction.holdBlocked b "Fixture"
                (Guid.NewGuid()) source |> ok |> ignore
            let failedApproval = request a workId (Guid.NewGuid())
            let denialId = Guid.NewGuid()
            failWithoutCommit a denialId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" denialId failedApproval)
            let staleId = Guid.NewGuid()
            failWithoutCommit a staleId (fun () ->
                WorkCompositionQueueTransaction.acceptAndRelease a "Fixture" staleId source)
            let head = LedgerStore.fetch a |> ok
            let queue = WorkQueueGitLedger.loadAt a head |> ok
            Assert.That(queue.Works.[workId].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(queue.Migration, Is.EqualTo(MigrationEpoch.Frozen)))
