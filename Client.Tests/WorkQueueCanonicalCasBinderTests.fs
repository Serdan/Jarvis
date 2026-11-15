module Client.Tests.WorkQueueCanonicalCasBinderTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalSourceEvidence
open Client.WorkQueueCanonicalCasBinder

[<TestFixture>]
type WorkQueueCanonicalCasBinderTests() =
    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        args |> List.iter runner.StartInfo.ArgumentList.Add
        if not (runner.Start()) then failwith "Git fixture did not start."
        let output = runner.StandardOutput.ReadToEnd()
        let stderr = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Git fixture timed out."
        if runner.ExitCode <> 0 then failwithf "Git fixture command failed: %s" stderr
        output.Trim()

    let ok = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected fixture error: %A" error

    let denied = function
        | Error error ->
            Assert.That(string error, Does.Contain("IncompleteCoverage / RecoveryBlocked"))
        | Ok _ -> Assert.Fail("An inert WQ04 binder published a Git operation.")

    let gaps assessment =
        match assessment with
        | RecoveryBlocked reasons -> reasons

    let hasSourceGap issues =
        issues |> List.exists (function SourceEffectsUncovered _ -> true | _ -> false)
    let hasRoleGap issues =
        issues |> List.exists (function RoleAdmissionUncovered _ -> true | _ -> false)
    let hasEventRejection issues =
        issues |> List.exists (function CanonicalEventRejected _ -> true | _ -> false)
    let hasStaleHead issues =
        issues |> List.exists (function CanonicalHeadChanged _ -> true | _ -> false)
    let hasCasGap issues =
        issues |> List.exists (function SameCasProducerUnproved _ -> true | _ -> false)

    let withClones work =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq04-binder-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
            git root ["clone"; bare; first] |> ignore
            git first ["config"; "user.name"; "Fixture"] |> ignore
            git first ["config"; "user.email"; "fixture@example.test"] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "pinned")
            git first ["add"; "source.txt"] |> ignore
            git first ["commit"; "-m"; "source fixture"] |> ignore
            git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; second] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Lead" |> ok |> ignore
            WorkQueueGitLedger.initialize a "Lead" (Guid.NewGuid()) Map.empty |> ok |> ignore
            let taskId = "binder-original"
            ReviewWorkflow.createTask a (Guid.NewGuid()) taskId taskId |> ok |> ignore
            let _, taskRecord, _ = ReviewWorkflow.getTask a taskId |> ok
            let closure = ReviewWorkflow.closeTaskAs a "Lead" (Guid.NewGuid()) taskId taskRecord.Version |> ok
            let snapshot = LedgerStore.fetch b |> ok
            let _, project = WorkQueueGitLedger.load b |> ok
            let original =
                { SnapshotHead = snapshot.Commit
                  SnapshotHighWater = snapshot.Header.NextSequence - 1L
                  OriginalOperationId = Guid.Parse closure.Operation.OperationId
                  OriginalRequestHash = closure.Operation.RequestHash }
            let request =
                { SourceAnchor = original
                  SourceEvent = TaskTransition(taskId, null, "CloseTask", 0)
                  ExpectedProjectVersion = project.Version
                  RoleScope = NoRoleTransition }
            work a b request
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Test>]
    member _.ValidOriginalCanonicalEventStillCannotProduceOrClaim() =
        withClones (fun _ b request ->
            let original = LedgerStore.fetch b |> ok
            let evidence =
                WorkQueueCanonicalSourceEvidence.read b request.SourceAnchor request.SourceEvent |> ok
            match evidence.Coverage with
            | IncompleteCoverage explanation ->
                Assert.That(explanation, Does.Contain("No atomic source Git"))
            let missing = WorkQueueCanonicalCasBinder.inspect b request |> gaps
            Assert.That(hasSourceGap missing, Is.True)
            Assert.That(hasCasGap missing, Is.True)
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) request)
            let after = LedgerStore.fetch b |> ok
            Assert.That(after.Commit, Is.EqualTo(original.Commit))
            Assert.That(after.Header.NextSequence, Is.EqualTo(original.Header.NextSequence))
        )

    [<Test>]
    member _.ForgedOperationAndMatchingCallerHighWaterRemainDenied() =
        withClones (fun _ b request ->
            let all = [
                { request with SourceAnchor = { request.SourceAnchor with OriginalOperationId = Guid.NewGuid() } }
                { request with SourceAnchor = { request.SourceAnchor with OriginalRequestHash = String.replicate 64 "0" } }
                { request with SourceEvent = TaskTransition("forged-task", null, "CloseTask", 0) }
                { request with SourceEvent = TaskTransition("binder-original", null, "ReopenTask", 0) }
            ]
            for forged in all do
                let missing = WorkQueueCanonicalCasBinder.inspect b forged |> gaps
                Assert.That(hasEventRejection missing, Is.True)
                Assert.That(hasCasGap missing, Is.True)
                denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) forged)
            let snapshot = LedgerStore.fetch b |> ok
            Assert.That(snapshot.Commit, Is.EqualTo(request.SourceAnchor.SnapshotHead))
        )

    [<Test>]
    member _.TwoClonesCannotReuseStaleOriginalHighWaterOrPretendTheirRoleRevisionIsAuthority() =
        withClones (fun a b request ->
            let forgedRole = { request with RoleScope = NewRoleDemand("Reviewer", 999) }
            let pinnedRole = { request with RoleScope = PinnedLaunchRole("fake-launch", "Reviewer", 1) }
            for attempted in [ forgedRole; pinnedRole ] do
                let missing = WorkQueueCanonicalCasBinder.inspect b attempted |> gaps
                Assert.That(hasRoleGap missing, Is.True)
                denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) attempted)
            // The other clone commits a real canonical operation; even a
            // previously valid original event and asserted role revision do
            // not carry authority across its changed Git CAS snapshot.
            ReviewWorkflow.createTask a (Guid.NewGuid()) "independent-advance" "Independent" |> ok |> ignore
            let missing = WorkQueueCanonicalCasBinder.inspect b pinnedRole |> gaps
            Assert.That(hasStaleHead missing, Is.True)
            Assert.That(hasRoleGap missing, Is.True)
            denied (WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" (Guid.NewGuid()) pinnedRole)
        )

    [<Test>]
    member _.RacedTwoCloneBinderAttemptsNeverPublishReceiptOrProjectMutation() =
        withClones (fun a b request ->
            let before = LedgerStore.fetch a |> ok
            let operationA, operationB = Guid.NewGuid(), Guid.NewGuid()
            let first = Task.Run(fun () ->
                WorkQueueCanonicalCasBinder.attemptUnderCas a "Lead" operationA request)
            let second = Task.Run(fun () ->
                WorkQueueCanonicalCasBinder.attemptUnderCas b "Lead" operationB request)
            Task.WaitAll [| first :> Task; second :> Task |]
            denied first.Result
            denied second.Result
            let snapshot = LedgerStore.fetch b |> ok
            Assert.That(snapshot.Commit, Is.EqualTo(before.Commit))
            Assert.That(snapshot.Header.NextSequence, Is.EqualTo(before.Header.NextSequence))
            let storedA = LedgerStore.getOperationAt b snapshot operationA |> ok
            let storedB = LedgerStore.getOperationAt b snapshot operationB |> ok
            Assert.That(Option.isNone storedA, Is.True)
            Assert.That(Option.isNone storedB, Is.True)
        )
