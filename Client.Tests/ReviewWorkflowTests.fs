module Client.Tests.ReviewWorkflowTests

open System
open System.Diagnostics
open System.IO
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

[<TestFixture>]
type ReviewWorkflowTests() =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Cannot launch Git test fixture"
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "Git fixture failed: %s" stderr
        stdout.Trim()

    let success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected ledger error: %A" error

    let expectStale = function
        | Error(StaleEntityVersion _) -> ()
        | value -> Assert.Fail(sprintf "Expected stale/invalid transition, got %A" value)

    let expectBusy = function
        | Error(AgentBusy _) -> ()
        | value -> Assert.Fail(sprintf "Expected AgentBusy, got %A" value)

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-review-v2-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "one")
            let two = Path.Combine(root, "two")
            git root [ "init"; "--bare"; remote ] |> ignore
            git root [ "clone"; remote; one ] |> ignore
            git one [ "config"; "user.name"; "Review Fixture" ] |> ignore
            git one [ "config"; "user.email"; "review@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "README.txt"), "initial")
            git one [ "add"; "README.txt" ] |> ignore
            git one [ "commit"; "-m"; "initial source" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; two ] |> ignore
            git two [ "config"; "user.name"; "Review Fixture" ] |> ignore
            git two [ "config"; "user.email"; "review@example.test" ] |> ignore
            let binding path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = Guid.Parse("a5127b1f-4810-4f88-89e0-81370da8c5af")
                  LedgerId = Guid.Parse("17424f6b-b62e-4407-9671-59a1f10d1d36")
                  Epoch = 1 }
            action one two (binding one) (binding two)
        finally Directory.Delete(root, true)

    [<Test>]
    member _.SubmitRequestChangesResubmitAndAccept() =
        withFixture (fun one two first second ->
            LedgerStore.initialize first true |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Agent 1" |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer" |> success |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "315" "Reviewable task" |> success |> ignore
            let _, openTask, noAttempt = ReviewWorkflow.getTask first "315" |> success
            Assert.That(openTask.Lifecycle, Is.EqualTo("Open"))
            Assert.That(noAttempt.IsNone, Is.True)
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "315" attemptId "Agent 1" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "315" "Agent 1" 1 1 |> success |> ignore
            let sourceRef = "refs/heads/" + git one [ "branch"; "--show-current" ]
            let submittedCommit = git one [ "rev-parse"; "HEAD" ]
            let firstReview = Guid.NewGuid()
            let submissionOperation = Guid.NewGuid()
            let submit binding =
                ReviewWorkflow.submitCommit binding submissionOperation "315" "Agent 1" 2 1 firstReview
                    sourceRef submittedCommit "Please review exact source commit"
            Assert.That((submit first |> success).AlreadyApplied, Is.False)
            Assert.That((submit second |> success).AlreadyApplied, Is.True)
            let _, queue = ReviewWorkflow.reviewerQueue second "Reviewer" |> success
            Assert.That(queue.Length, Is.EqualTo(1))
            Assert.That(queue.Head.CommitOid, Is.EqualTo(submittedCommit))
            Assert.That(queue.Head.Status, Is.EqualTo("Pending"))
            match ReviewWorkflow.decide first (Guid.NewGuid()) "315" firstReview "Agent 1" 1 Accept "looks fine" with
            | Error(ReviewAccessDenied _) -> ()
            | result -> Assert.Fail(sprintf "Owner must not review own submission: %A" result)
            let returnOperation = Guid.NewGuid()
            let returnChanges binding =
                ReviewWorkflow.decide binding returnOperation "315" firstReview "Reviewer" 1 RequestChanges "Needs a test"
            Assert.That((returnChanges second |> success).AlreadyApplied, Is.False)
            Assert.That((returnChanges first |> success).AlreadyApplied, Is.True)
            let _, taskAfterReturn, attemptAfterReturnOption = ReviewWorkflow.getTask first "315" |> success
            let attemptAfterReturn = Option.get attemptAfterReturnOption
            Assert.That(taskAfterReturn.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That(attemptAfterReturn.Phase, Is.EqualTo("Queued"))
            Assert.That(attemptAfterReturn.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(attemptAfterReturn.ReviewFeedback, Is.EqualTo("Needs a test"))
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "315" "Agent 1" 2 1)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "315" "Agent 1" 4 2 |> success |> ignore
            File.WriteAllText(Path.Combine(one, "README.txt"), "initial\nnew test")
            git one [ "add"; "README.txt" ] |> ignore
            git one [ "commit"; "-m"; "address review" ] |> ignore
            let revisedCommit = git one [ "rev-parse"; "HEAD" ]
            let secondReview = Guid.NewGuid()
            ReviewWorkflow.submitCommit first (Guid.NewGuid()) "315" "Agent 1" 5 2 secondReview sourceRef revisedCommit "Addressed review" |> success |> ignore
            let _, secondQueue = ReviewWorkflow.reviewerQueue second "Reviewer" |> success
            Assert.That(secondQueue.Length, Is.EqualTo(1))
            Assert.That(secondQueue.Head.CommitOid, Is.EqualTo(revisedCommit))
            match ReviewWorkflow.decide second (Guid.NewGuid()) "315" secondReview "Reviewer" 1 Accept "Tests accepted" with
            | Error(SourceUnavailable _) -> ()
            | result -> Assert.Fail(sprintf "Reviewer cannot accept an unavailable source commit: %A" result)
            git one [ "push"; "origin"; "HEAD" ] |> ignore
            git two [ "fetch"; "origin" ] |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "315" secondReview "Reviewer" 1 Accept "Tests accepted" |> success |> ignore
            let _, finalTask, finalAttemptOption = ReviewWorkflow.getTask first "315" |> success
            let finalAttempt = Option.get finalAttemptOption
            Assert.That(finalTask.Lifecycle, Is.EqualTo("Implemented"))
            Assert.That(finalAttempt.Phase, Is.EqualTo("Accepted"))
            let _, archivedReview = ReviewWorkflow.getReview second "315" firstReview |> success
            Assert.That(archivedReview.Status, Is.EqualTo("ChangesRequested"))
            Assert.That(archivedReview.Feedback, Is.EqualTo("Needs a test"))
            Assert.That((ReviewWorkflow.reviewerQueue first "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            // The review action changes only the ledger; the source push above was explicit.
            Assert.That(git one [ "rev-parse"; sourceRef ], Is.EqualTo(revisedCommit))
            Assert.That(git one [ "rev-parse"; "refs/remotes/origin/" + git one [ "branch"; "--show-current" ] ], Is.EqualTo(revisedCommit)))

    [<Test>]
    member _.SubmissionReleasesSlotAndReturnedReviewDoesNotPreempt() =
        withFixture (fun one _ first _ ->
            LedgerStore.initialize first true |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Agent 1" |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer" |> success |> ignore
            for taskId in [ "100"; "101" ] do
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId taskId |> success |> ignore
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Agent 1" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "100" "Agent 1" 1 1 |> success |> ignore
            expectBusy (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "101" "Agent 1" 1 1)
            let sourceRef = "refs/heads/" + git one [ "branch"; "--show-current" ]
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitCommit first (Guid.NewGuid()) "100" "Agent 1" 2 1 reviewId sourceRef
                (git one [ "rev-parse"; "HEAD" ]) "First review" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "101" "Agent 1" 1 1 |> success |> ignore
            ReviewWorkflow.decide first (Guid.NewGuid()) "100" reviewId "Reviewer" 1 RequestChanges "Fix later" |> success |> ignore
            let _, _, returnedOption = ReviewWorkflow.getTask first "100" |> success
            let returned = Option.get returnedOption
            Assert.That(returned.Phase, Is.EqualTo("Queued"))
            expectBusy (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "100" "Agent 1" 4 2))
