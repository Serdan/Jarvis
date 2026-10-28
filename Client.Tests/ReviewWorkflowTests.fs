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
        if not (proc.Start()) then failwith "Cannot launch Git fixture."
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "Git fixture error: %s" stderr
        stdout.Trim()

    let success = function
        | Ok value -> value
        | Error failure -> failwithf "Unexpected ledger result: %A" failure

    let expectStale = function
        | Error(StaleEntityVersion _) | Error(ReviewVersionConflict _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale/version conflict: %A" other)

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-review-v2-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "owner")
            let two = Path.Combine(root, "reviewer")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            for clone in [ one ] do
                git clone [ "config"; "user.name"; "V2 Fixture" ] |> ignore
                git clone [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "base")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "commit"; "-m"; "task base" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            git two [ "config"; "user.name"; "V2 Fixture" ] |> ignore
            git two [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first = binding one
            let second = binding two
            LedgerStore.initialize first true |> success |> ignore
            for id in [ "Owner"; "Reviewer" ] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) id |> success |> ignore
            let baseOid = git one [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/" + git one [ "branch"; "--show-current" ]
            let newCommit (title: string) (content: string) =
                File.WriteAllText(Path.Combine(one, "source.txt"), content)
                git one [ "add"; "source.txt" ] |> ignore
                git one [ "commit"; "-m"; title ] |> ignore
                git one [ "rev-parse"; "HEAD" ]
            let initTask taskId =
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId taskId |> success |> ignore
                let attemptId = Guid.NewGuid()
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
                |> success |> ignore
                ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
                ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner" 2 1 baseOid sourceRef
                |> success |> ignore
            action one two first second baseOid sourceRef newCommit initTask
        finally
            for checkout in [ "owner"; "reviewer" ] do
                let pendingConfig =
                    Client.V2ReviewApi.ReviewV2Binding.pathForRoot(
                        Path.Combine(root, checkout))
                let pendingDir =
                    Path.Combine(Path.GetDirectoryName pendingConfig, "pending",
                                 Path.GetFileNameWithoutExtension pendingConfig)
                if Directory.Exists pendingDir then Directory.Delete(pendingDir, true)
            Directory.Delete(root, true)

    [<Test>]
    member _.OwnerUpdatesUntilClaimThenContinuesWorking() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "315"
            let c = newCommit "C" "base\nC"
            let req = Guid.NewGuid()
            let submitOp = Guid.NewGuid()
            let submit binding =
                ReviewWorkflow.submitReview binding submitOp "315" "Owner" 3 1 req sourceRef c "Please review C"
            Assert.That((submit first |> success).AlreadyApplied, Is.False)
            Assert.That((submit second |> success).AlreadyApplied, Is.True)
            let _, _, attemptAtSubmission = ReviewWorkflow.getTask first "315" |> success
            Assert.That((Option.get attemptAtSubmission).Phase, Is.EqualTo("Working"))
            let d = newCommit "D" "base\nC\nD"
            let updateOp = Guid.NewGuid()
            let update binding =
                ReviewWorkflow.updateReview binding updateOp "315" "Owner" 1 req 1 sourceRef d "Review C and D"
            Assert.That((update first |> success).AlreadyApplied, Is.False)
            Assert.That((update second |> success).AlreadyApplied, Is.True)
            let _, changedReq, revision = ReviewWorkflow.getReview second "315" req |> success
            Assert.That(changedReq.CurrentRevision, Is.EqualTo(2))
            Assert.That(revision.BaseCommit, Is.EqualTo(baseOid))
            Assert.That(revision.HeadCommit, Is.EqualTo(d))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(1))
            expectStale (ReviewWorkflow.claimReview second (Guid.NewGuid()) "315" req "Reviewer" 1 1)
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "315" req "Reviewer" 2 2 |> success |> ignore
            Assert.That((update second |> success).AlreadyApplied, Is.True)
            Assert.That(git one [ "ls-remote"; "origin"; $"refs/heads/jarvis-review/315/{req:N}/r3" ], Is.Empty)
            match ReviewWorkflow.updateReview first (Guid.NewGuid()) "315" "Owner" 1 req 3 sourceRef d "new" with
            | Error(ReviewAlreadyClaimed _) -> ()
            | other -> Assert.Fail(sprintf "Claim must freeze owner updates: %A" other)
            ReviewWorkflow.decide second (Guid.NewGuid()) "315" req "Reviewer" 3 Accept "Verified" |> success |> ignore
            let _, task, liveAttempt = ReviewWorkflow.getTask first "315" |> success
            let attempt = Option.get liveAttempt
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That(attempt.Phase, Is.EqualTo("Working"))
            Assert.That(attempt.ActiveReviewRequestId, Is.Null)
            Assert.That(attempt.AcceptedHead, Is.EqualTo(d))
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "315" "Owner" attempt.FeedbackSequence
            |> success |> ignore
            let _, _, acknowledged = ReviewWorkflow.getTask first "315" |> success
            Assert.That((Option.get acknowledged).AcknowledgedFeedbackSequence, Is.EqualTo(attempt.FeedbackSequence))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "315" "Owner" 6 3 1
                "Implementation finished" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "315" "Owner" 7 req
            |> success |> ignore
            let _, completedTask, completedAttempt = ReviewWorkflow.getTask first "315" |> success
            Assert.That(completedTask.Lifecycle, Is.EqualTo("Implemented"))
            Assert.That((Option.get completedAttempt).Phase, Is.EqualTo("Accepted"))
            let expectedReviewRef = $"refs/heads/jarvis-review/315/{req:N}/r2"
            Assert.That(git one [ "ls-remote"; "origin"; expectedReviewRef ], Does.Contain(d))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0)))

    [<Test>]
    member _.RejectedReviewDoesNotChangeCumulativeBase() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "500"
            let c = newCommit "unapproved C" "base\nC"
            let firstId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "500" "Owner" 3 1 firstId sourceRef c "Original"
            |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "500" firstId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "500" firstId "Reviewer" 2 RequestChanges "Fix C"
            |> success |> ignore
            let d = newCommit "fixed D" "base\nC\nD"
            let nextId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "500" "Owner" 5 1 nextId sourceRef d "C and D"
            |> success |> ignore
            let _, _, revision = ReviewWorkflow.getReview second "500" nextId |> success
            Assert.That(revision.BaseCommit, Is.EqualTo(baseOid))
            Assert.That(revision.HeadCommit, Is.EqualTo(d))
            Assert.That(revision.BaseCommit, Is.Not.EqualTo(c))
            let _, _, attemptOption = ReviewWorkflow.getTask first "500" |> success
            Assert.That((Option.get attemptOption).Phase, Is.EqualTo("Working"))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "500" nextId "Reviewer" 1 1 |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "500" nextId "Reviewer" 2 Accept "Now includes all work"
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0)))

    [<Test>]
    member _.SourceRefIsCreateOnlyAndNoDuplicateActiveRequest() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "700"
            let c = newCommit "C" "C"
            let request = Guid.NewGuid()
            let firstOp = Guid.NewGuid()
            let submit id operation =
                ReviewWorkflow.submitReview first operation "700" "Owner" 3 1 id sourceRef c "Review"
            submit request firstOp |> success |> ignore
            let originalRef = $"refs/heads/jarvis-review/700/{request:N}/r1"
            let d = newCommit "D" "D"
            match LedgerStore.publishReviewRef first originalRef d with
            | Error(SourceReviewRefUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Ref must be immutable: %A" other)
            let count = git one [ "ls-remote"; "origin"; originalRef ]
            Assert.That(count, Does.StartWith(c))
            match submit (Guid.NewGuid()) (Guid.NewGuid()) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Second active review must be rejected: %A" other)
            let _, _, current = ReviewWorkflow.getTask first "700" |> success
            Assert.That((Option.get current).ActiveReviewRequestId, Is.EqualTo(request.ToString("N")))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(1)))

    [<Test>]
    member _.SingleOwnerCanWorkOnlyOneTaskWhileReviewIsPending() =
        withFixture (fun one _ first _ _ sourceRef newCommit initTask ->
            initTask "100"
            ReviewWorkflow.createTask first (Guid.NewGuid()) "101" "101" |> success |> ignore
            match ReviewWorkflow.assignTask first (Guid.NewGuid()) "101" (Guid.NewGuid()) "Owner" "Reviewer" with
            | Error(AgentBusy _) -> ()
            | other -> Assert.Fail(sprintf "Working agent must not be assigned a second task: %A" other)
            let c = newCommit "C" "C"
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "100" "Owner" 3 1
                (Guid.NewGuid()) sourceRef c "Snapshot" |> success |> ignore
            let current = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(current.Agent.State, Is.EqualTo("Working"))
            Assert.That(current.SlotAvailable, Is.False))

    [<Test>]
    member _.WithdrawalAndClaimedHandoffPreserveWorkingOwner() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "800"
            let c = newCommit "First patch" "C"
            let original = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "800" "Owner" 3 1
                original sourceRef c "Original patch" |> success |> ignore
            ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "800" original "Owner" 1 "Superseded"
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            let d = newCommit "Second patch" "D"
            let replacement = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "800" "Owner" 5 1
                replacement sourceRef d "Replacement patch" |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer 2" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "800" replacement "Reviewer" 1 1
            |> success |> ignore
            match ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "800" replacement "Owner" 2 "No" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Owner must not withdraw claimed revision: %A" other)
            ReviewWorkflow.reassignReviewer second (Guid.NewGuid()) "800" replacement "Reviewer" "Reviewer 2" 2
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer 2" |> success |> snd).Length, Is.EqualTo(1))
            match ReviewWorkflow.decide second (Guid.NewGuid()) "800" replacement "Reviewer" 3 Accept "Old" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Old reviewer should not decide after handoff: %A" other)
            ReviewWorkflow.decide second (Guid.NewGuid()) "800" replacement "Reviewer 2" 3 Accept "Accepted"
            |> success |> ignore
            let _, task, attemptOption = ReviewWorkflow.getTask first "800" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That((Option.get attemptOption).Phase, Is.EqualTo("Working"))
            Assert.That((Option.get attemptOption).AcceptedHead, Is.EqualTo(d)))

    [<Test>]
    member _.AnyRegisteredReviewerCanClaimWithoutPreassignment() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "900"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer 2" |> success |> ignore
            let c = newCommit "Source" "C"
            let req = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "900" "Owner" 3 1
                req sourceRef c "Needs review" |> success |> ignore
            let _, available = ReviewWorkflow.reviewerQueue second "Reviewer 2" |> success
            Assert.That(available.Length, Is.EqualTo(1))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "900" req "Reviewer 2" 1 1
            |> success |> ignore
            expectStale (ReviewWorkflow.claimReview first (Guid.NewGuid()) "900" req "Reviewer" 1 1)
            ReviewWorkflow.decide second (Guid.NewGuid()) "900" req "Reviewer 2" 2 Accept "Verified"
            |> success |> ignore)

    [<Test>]
    member _.OlderAcceptanceCannotFinalizeAfterNewerRejectedReview() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "901"
            let c = newCommit "First" "C"
            let accepted = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "901" "Owner" 3 1
                accepted sourceRef c "Complete?" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "901" accepted "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "901" accepted "Reviewer" 2 Accept "Fine"
            |> success |> ignore
            let d = newCommit "More changes" "D"
            let later = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "901" "Owner" 5 1
                later sourceRef d "Extra work" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "901" later "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "901" later "Reviewer" 2 RequestChanges "Not ready"
            |> success |> ignore
            match ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "901" "Owner" 7 accepted with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old approval must not finalize newer rejected work: %A" other))

    [<Test>]
    member _.ClaimAndUpdateRaceHasOneAuthoritativeRevision() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "999"
            let c = newCommit "First" "C"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "999" "Owner" 3 1
                requestId sourceRef c "Original" |> success |> ignore
            let d = newCommit "Second" "D"
            let updateOp = Guid.NewGuid()
            let claimOp = Guid.NewGuid()
            let update =
                System.Threading.Tasks.Task.Run(fun () ->
                    ReviewWorkflow.updateReview first updateOp "999" "Owner" 1
                        requestId 1 sourceRef d "Replacement")
            let claim =
                System.Threading.Tasks.Task.Run(fun () ->
                    ReviewWorkflow.claimReview second claimOp "999" requestId "Reviewer" 1 1)
            System.Threading.Tasks.Task.WaitAll(
                [| update :> System.Threading.Tasks.Task; claim :> System.Threading.Tasks.Task |])
            let updated = update.Result
            let claimed = claim.Result
            let winners =
                [ updated; claimed ]
                |> List.filter (function | Ok _ -> true | _ -> false)
                |> List.length
            Assert.That(winners, Is.EqualTo(1))
            let _, request, revision = ReviewWorkflow.getReview first "999" requestId |> success
            if request.Status = "Claimed" then
                Assert.That(request.CurrentRevision, Is.EqualTo(1))
                Assert.That(revision.HeadCommit, Is.EqualTo(c))
                match updated with
                | Error(ReviewAlreadyClaimed _) -> ()
                | other -> Assert.Fail(sprintf "Claim should lock the update: %A" other)
            else
                Assert.That(request.Status, Is.EqualTo("Unclaimed"))
                Assert.That(request.CurrentRevision, Is.EqualTo(2))
                Assert.That(revision.HeadCommit, Is.EqualTo(d))
                expectStale claimed)


    [<Test>]
    member _.AgentInboxSeparatesSlotQueueAndReviewResponsibilities() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit _ ->
            ReviewWorkflow.createTask first (Guid.NewGuid()) "102" "Queued task" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "102" (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            let fresh = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(fresh.SlotAvailable, Is.True)
            Assert.That(fresh.Working.IsNone, Is.True)
            Assert.That(fresh.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(fresh.RunnableQueue.Head.Task.TaskId, Is.EqualTo("102"))
            Assert.That(fresh.Unassigned, Is.False)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "102" "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) "102" "Owner" 2 1 baseOid sourceRef
            |> success |> ignore
            let working = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(working.SlotAvailable, Is.False)
            Assert.That(working.Working.Value.Task.TaskId, Is.EqualTo("102"))
            Assert.That(working.RunnableQueue.Length, Is.EqualTo(0))
            let c = newCommit "Needs review" "C"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "102" "Owner" 3 1
                requestId sourceRef c "Review C" |> success |> ignore
            let submitting = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(submitting.PendingReviews.Length, Is.EqualTo(1))
            Assert.That(submitting.Working.IsSome, Is.True)
            let reviewer = ReviewWorkflow.getAgentInbox second "Reviewer" |> success
            Assert.That(reviewer.AvailableReviews.Length, Is.EqualTo(1))
            Assert.That(reviewer.PendingReviews.Length, Is.EqualTo(0))
            Assert.That(reviewer.SlotAvailable, Is.True)
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "102" requestId "Reviewer" 1 1
            |> success |> ignore
            let claimed = ReviewWorkflow.getAgentInbox first "Reviewer" |> success
            Assert.That(claimed.AvailableReviews.Length, Is.EqualTo(0))
            Assert.That(claimed.PendingReviews.Length, Is.EqualTo(1))
            ReviewWorkflow.decide second (Guid.NewGuid()) "102" requestId "Reviewer" 2 Accept "Passed"
            |> success |> ignore
            let decided = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(decided.Working.IsSome, Is.True)
            Assert.That(decided.PendingReviews.Length, Is.EqualTo(0))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "102" "Owner" 5 3 1
                "Ready for integration" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "102" "Owner" 6 requestId
            |> success |> ignore
            let finalized = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finalized.SlotAvailable, Is.True)
            Assert.That(finalized.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finalized.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(finalized.RunnableQueue.Length, Is.EqualTo(0))
            Assert.That(finalized.Unassigned, Is.False))

    [<Test>]
    member _.AgentInboxRejectsUnknownAgent() =
        withFixture (fun _ _ first _ _ _ _ _ ->
            match ReviewWorkflow.getAgentInbox first "Not registered" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Unknown agent should not appear in inbox: %A" other))


    [<Test>]
    member _.CanonicalSpecIsAtomicVersionedAndPinnedByAssignment() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "600"
            let initialHtml = "<h1>First version</h1><p>Original requirements</p>"
            let revisedHtml = "<h1>Second version</h1><p>Acceptance criteria</p>"
            let initialOp = Guid.NewGuid()
            let create () =
                ReviewWorkflow.createTaskWithSpec first initialOp taskId "First version" initialHtml
            Assert.That((create () |> success).AlreadyApplied, Is.False)
            Assert.That((create () |> success).AlreadyApplied, Is.True)
            let _, taskOne, _, specOne, oidOne =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(taskOne.SpecVersion, Is.EqualTo(1))
            Assert.That(specOne, Is.EqualTo(initialHtml))
            let editOp = Guid.NewGuid()
            ReviewWorkflow.reviseTaskSpec first editOp taskId 1 "Second version" revisedHtml
            |> success |> ignore
            let _, taskTwo, _, specTwo, oidTwo =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(taskTwo.Version, Is.EqualTo(2))
            Assert.That(taskTwo.SpecVersion, Is.EqualTo(2))
            Assert.That(specTwo, Is.EqualTo(revisedHtml))
            Assert.That(oidTwo, Is.Not.EqualTo(oidOne))
            expectStale (
                ReviewWorkflow.reviseTaskSpec second (Guid.NewGuid()) taskId 1
                    "Stale" "<h1>Stale</h1>")
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let _, _, assigned = ReviewWorkflow.getTask second taskId |> success
            Assert.That(assigned.Value.SpecBlobOid, Is.EqualTo(oidTwo))
            expectStale (
                ReviewWorkflow.reviseTaskSpec second (Guid.NewGuid()) taskId 3
                    "Too late" "<h1>Too late</h1>"))


    [<Test>]
    member _.OptedInTaskApiCanCreateAssignBeginBindAndSubmit() =
        withFixture (fun one _ first _ baseOid sourceRef newCommit _ ->
            let configPath = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            let configDirectory = Path.GetDirectoryName configPath
            Directory.CreateDirectory configDirectory |> ignore
            let approved : Client.V2ReviewApi.ReviewV2OptIn =
                { SchemaVersion = 1
                  Enabled = true
                  ProjectRoot = one
                  ProjectId = first.ProjectId.ToString("D")
                  LedgerId = first.LedgerId.ToString("D")
                  Epoch = first.Epoch
                  RemoteName = first.RemoteName
                  ApprovedFetchUrl = first.ApprovedFetchUrl
                  ApprovedPushUrl = first.ApprovedPushUrl
                  HighestTrustedHead = (LedgerStore.fetch first |> success).Commit }
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(approved))
            try
                let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
                let template (action: string) (taskId: string) : Common.V2TaskWriteCommand =
                    { ProjectName = "owner"
                      Action = action
                      OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId
                      AttemptId = ""
                      ActorId = "Owner"
                      OwnerId = ""
                      ReviewerId = ""
                      Title = ""
                      Html = ""
                      ExpectedVersion = 0
                      ExpectedAgentVersion = 0
                      AssignmentGeneration = 0
                      Summary = ""
                      NextAction = ""
                      Reason = ""
                      BaseOid = ""
                      TargetRef = ""
                      DecisionSequence = 0L }
                let run cmd =
                    Client.V2ReviewApi.V2TaskApi.write cmd ctx
                    |> function
                        | Ok value -> value
                        | Error error -> failwithf "V2 task API failed: %A" error
                let taskId = "751"
                let html = "<h1>Full task specification</h1><p>Acceptance: one review</p>"
                run { template "CreateTask" taskId with
                          Title = "Full task"; Html = html } |> ignore
                let attemptId = Guid.NewGuid()
                run { template "AssignTask" taskId with
                          AttemptId = attemptId.ToString("D")
                          OwnerId = "Owner"; ReviewerId = "Reviewer" } |> ignore
                let queued =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner" } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 inbox failed: %A" error
                Assert.That(queued.RunnableQueue.Length, Is.EqualTo(1))
                Assert.That(queued.RunnableQueue.Head.Attempt.SpecBlobOid.Length, Is.EqualTo(40))
                run { template "BeginAttempt" taskId with
                          ExpectedVersion = 1; AssignmentGeneration = 1 } |> ignore
                run { template "BindReviewBase" taskId with
                          ExpectedVersion = 2; AssignmentGeneration = 1
                          BaseOid = baseOid; TargetRef = sourceRef } |> ignore
                let getTask : Common.V2ReviewReadCommand =
                    { ProjectName = "owner"; Action = "GetTask"; TaskId = taskId
                      RequestId = ""; ReviewerId = "" }
                match Client.V2ReviewApi.V2ReviewApi.read getTask ctx with
                | Error error -> Assert.Fail(sprintf "V2 task read failed: %A" error)
                | Ok value ->
                    let resultJson = System.Text.Json.JsonSerializer.Serialize(value)
                    use parsed = System.Text.Json.JsonDocument.Parse(resultJson)
                    Assert.That(parsed.RootElement.GetProperty("specHtml").GetString(), Is.EqualTo(html))
                    Assert.That(parsed.RootElement.GetProperty("specBlobOid").GetString(),
                                Is.EqualTo(queued.RunnableQueue.Head.Attempt.SpecBlobOid))
                run { template "YieldAgent" taskId with
                          ExpectedVersion = 3; ExpectedAgentVersion = 3
                          AssignmentGeneration = 1; Summary = "Checkpoint"
                          NextAction = "Resume work" } |> ignore
                let ready =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner" } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 Ready inbox failed: %A" error
                Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
                Assert.That(ready.OperatorActionRequired, Is.True)
                run { template "ResumeAgent" taskId with
                          ExpectedVersion = 4; ExpectedAgentVersion = 4
                          AssignmentGeneration = 1 } |> ignore
                let head = newCommit "Task 751 implementation" "code for 751"
                let requestId = Guid.NewGuid()
                let reviewCmd : Common.V2ReviewWriteCommand =
                    { ProjectName = "owner"; Action = "Submit"
                      OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId; RequestId = requestId.ToString("D")
                      ActorId = "Owner"; ReviewerId = ""
                      ExpectedVersion = 5; Generation = 1
                      SourceRef = sourceRef; HeadOid = head; Summary = "Implementation ready"
                      Verdict = ""; Feedback = "" }
                match Client.V2ReviewApi.V2ReviewApi.write reviewCmd ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 submit API failed: %A" error)
                let after =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner" } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 inbox failed: %A" error
                Assert.That(after.SlotAvailable, Is.False)
                Assert.That(after.Agent.State, Is.EqualTo("Working"))
                run { template "CompleteAgentWork" taskId with
                          ExpectedVersion = 6; ExpectedAgentVersion = 5
                          AssignmentGeneration = 1; Summary = "Finished the implementation" } |> ignore
                let finished = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(finished.Agent.State, Is.EqualTo("Completed"))
                run { template "ParkAgent" taskId with
                          ExpectedAgentVersion = 6; Reason = "No other work" } |> ignore
                let parked = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
                Assert.That(parked.Focused.IsNone, Is.True)
                Assert.That(parked.PendingReviews.Length, Is.EqualTo(1))
                Assert.That(after.PendingReviews.Length, Is.EqualTo(1))
                Assert.That(after.PendingReviews.Head.RequestId, Is.EqualTo(requestId.ToString("N")))
                Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                            Is.EqualTo(0), "Verified V2 mutations must clear pending local intents.")
            finally
                if File.Exists configPath then File.Delete configPath)


    [<Test>]
    member _.AgentStateLifecycleAndCheckpointAreExplicit() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit _ ->
            let initial = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(initial.Agent.State, Is.EqualTo("Parked"))
            Assert.That(initial.OperatorActionRequired, Is.False)
            let taskId = "state1"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "State test"
            |> success |> ignore
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
            |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Agent.FocusAttemptId, Is.EqualTo(attemptId.ToString("N")))
            Assert.That(assigned.OperatorActionRequired, Is.True)
            Assert.That(assigned.Focused.IsSome, Is.True)
            Assert.That(assigned.Working.IsNone, Is.True)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner" 2 1 baseOid sourceRef
            |> success |> ignore
            let executing = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(executing.Agent.State, Is.EqualTo("Working"))
            Assert.That(executing.OperatorActionRequired, Is.False)
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 3 3 1
                "Implemented initial behavior" "Continue with edge cases" |> success |> ignore
            let ready = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.OperatorActionRequired, Is.True)
            Assert.That(ready.SlotAvailable, Is.True)
            Assert.That(ready.Focused.Value.Attempt.Phase, Is.EqualTo("Paused"))
            let checkpointPath =
                sprintf "tasks/%s/attempts/%s/checkpoints/%d.json"
                    taskId (attemptId.ToString("N")) ready.Agent.StateSequence
            let checkpoint, _ =
                LedgerStore.readAt first (LedgerStore.fetch first |> success) checkpointPath |> success
            Assert.That(checkpoint.Value, Does.Contain("Continue with edge cases"))
            match ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 4 3 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale resume must fail: %A" other)
            ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 4 4 1
            |> success |> ignore
            let resumed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(resumed.Agent.State, Is.EqualTo("Working"))
            let c = newCommit "Finished work" "state implementation"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) taskId "Owner" 5 1 reviewId
                sourceRef c "Full cumulative review" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 6 5 1
                "Submitted implementation for review" |> success |> ignore
            let completed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(completed.Agent.State, Is.EqualTo("Completed"))
            Assert.That(completed.NeedsOrchestratorAssignment, Is.True)
            Assert.That(completed.SlotAvailable, Is.True)
            Assert.That(completed.Focused.Value.Attempt.Phase, Is.EqualTo("Finished"))
            Assert.That(completed.PendingReviews.Length, Is.EqualTo(1))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) taskId reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) taskId reviewId "Reviewer" 2 Accept "Pass"
            |> success |> ignore
            let _, _, attempt = ReviewWorkflow.getTask second taskId |> success
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) taskId "Owner" attempt.Value.Version reviewId
            |> success |> ignore
            let finalized = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finalized.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finalized.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 6 "No more tasks"
            |> success |> ignore
            let parked = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
            Assert.That(parked.Focused.IsNone, Is.True)
            Assert.That(parked.OperatorActionRequired, Is.False)
            ReviewWorkflow.createTask first (Guid.NewGuid()) "state2" "New task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "state2" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Assigned")))

    [<Test>]
    member _.ParkDoesNotStealAssignedOrWorkingAgent() =
        withFixture (fun _ _ first _ _ _ _ _ ->
            let initial = ReviewWorkflow.getAgentInbox first "Owner" |> success
            ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" initial.Agent.Version
                "Still no work" |> success |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "park1" "Park test"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "park1" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            match ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 3 "Interrupt" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Parking assigned work should be rejected: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "park1" "Owner" 1 1
            |> success |> ignore
            match ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 4 "Interrupt" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Parking Working agent should be rejected: %A" other))


    [<Test>]
    member _.ReviewOfCompletedTaskDoesNotInterruptNewAssignment() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "oldTask"
            let head = newCommit "Implementation" "Old task changes"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "oldTask" "Owner" 3 1
                reviewId sourceRef head "Finished source patch" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "oldTask" "Owner" 4 3 1
                "Submitted and finished implementation" |> success |> ignore
            let completed = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(completed.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.createTask first (Guid.NewGuid()) "newTask" "Next task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "newTask" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Focused.Value.Task.TaskId, Is.EqualTo("newTask"))
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "newTask" "Owner" 1 1
            |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "oldTask" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "oldTask" reviewId "Reviewer" 2 Accept "Good"
            |> success |> ignore
            let _, _, oldAttempt = ReviewWorkflow.getTask first "oldTask" |> success
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "oldTask" "Owner"
                oldAttempt.Value.Version reviewId |> success |> ignore
            let after = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(after.Agent.State, Is.EqualTo("Working"))
            Assert.That(after.Working.Value.Task.TaskId, Is.EqualTo("newTask"))
            Assert.That(after.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(after.AcceptedPendingIntegration.Head.Task.TaskId, Is.EqualTo("oldTask")))


    [<Test>]
    member _.LateRequestedChangesQueueSameAttemptWithoutPreemptingNewWork() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit initTask ->
            initTask "late1"
            let firstAttempt = (ReviewWorkflow.getTask first "late1" |> success |> fun (_,_,a) -> a.Value)
            let head = newCommit "Review candidate" "candidate A"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "late1" "Owner" 3 1
                requestId sourceRef head "Initial cumulative patch" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "late1" "Owner" 4 3 1
                "Submitted and finished" |> success |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "late2" "Next assignment" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "late2" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late2" "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "late1" requestId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "late1" requestId "Reviewer"
                2 RequestChanges "Please fix A" |> success |> ignore
            let busy = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(busy.Agent.State, Is.EqualTo("Working"))
            Assert.That(busy.Working.Value.Task.TaskId, Is.EqualTo("late2"))
            Assert.That(busy.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(busy.RunnableQueue.Head.Task.TaskId, Is.EqualTo("late1"))
            Assert.That(busy.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(busy.RunnableQueue.Head.Attempt.AttemptId, Is.EqualTo(firstAttempt.AttemptId))
            Assert.That(busy.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            let notice = busy.UnacknowledgedFeedback.Head
            Assert.That(notice.Status, Is.EqualTo("ChangesRequested"))
            Assert.That(notice.Feedback, Is.EqualTo("Please fix A"))
            Assert.That(notice.DecisionSequence, Is.GreaterThan(0L))
            match ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "late1" "Owner" 6 6 2 "Ready to fix" with
            | Error(AgentBusy _) -> ()
            | other -> Assert.Fail(sprintf "Reissue must not preempt another task: %A" other)
            let acknowledgeOp = Guid.NewGuid()
            let ack binding =
                ReviewWorkflow.acknowledgeReviewFeedback binding acknowledgeOp
                    "late1" "Owner" notice.DecisionSequence
            Assert.That((ack first |> success).AlreadyApplied, Is.False)
            Assert.That((ack second |> success).AlreadyApplied, Is.True)
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).UnacknowledgedFeedback.Length,
                        Is.EqualTo(0))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "late2" "Owner" 2 6 1
                "Other task done" |> success |> ignore
            let idle = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(idle.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "late1" "Owner" 7 7 2
                "Reissue requested changes" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Focused.Value.Task.TaskId, Is.EqualTo("late1"))
            Assert.That(assigned.Focused.Value.Attempt.AttemptId, Is.EqualTo(firstAttempt.AttemptId))
            Assert.That(assigned.Focused.Value.Attempt.AssignmentGeneration, Is.EqualTo(2))
            match ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late1" "Owner" 7 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old assignment generation must be rejected: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late1" "Owner" 8 2
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Working")))

    [<Test>]
    member _.FeedbackDecisionsStayIndividuallyActionableAcrossNewReviews() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "multi"
            let firstHead = newCommit "A" "A"
            let firstId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "multi" "Owner" 3 1 firstId
                sourceRef firstHead "First review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "multi" firstId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "multi" firstId "Reviewer" 2
                RequestChanges "First feedback" |> success |> ignore
            let secondHead = newCommit "B" "A and B"
            let secondId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "multi" "Owner" 5 1 secondId
                sourceRef secondHead "Cumulative revised review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "multi" secondId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "multi" secondId "Reviewer" 2
                Accept "Second approval" |> success |> ignore
            let inbox = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(inbox.UnacknowledgedFeedback.Length, Is.EqualTo(2))
            Assert.That(inbox.UnacknowledgedFeedback.Head.Feedback, Is.EqualTo("First feedback"))
            Assert.That(inbox.UnacknowledgedFeedback.Tail.Head.Feedback, Is.EqualTo("Second approval"))
            let oldest = inbox.UnacknowledgedFeedback.Head
            let latest = inbox.UnacknowledgedFeedback.Tail.Head
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "multi"
                "Owner" latest.DecisionSequence |> success |> ignore
            let remaining = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(remaining.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            Assert.That(remaining.UnacknowledgedFeedback.Head.DecisionSequence,
                        Is.EqualTo(oldest.DecisionSequence))
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "multi"
                "Owner" oldest.DecisionSequence |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).UnacknowledgedFeedback.Length,
                        Is.EqualTo(0)))

    [<Test>]
    member _.ExplicitRecoveryReissuesSameAttemptGenerationWithCheckpoint() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "recover"
            let before = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(before.Agent.State, Is.EqualTo("Working"))
            let attemptId = before.Working.Value.Attempt.AttemptId
            let operationId = Guid.NewGuid()
            let recover binding =
                ReviewWorkflow.recoverAgentAssignment binding operationId
                    "recover" "Owner" 3 3 1 "Explicitly recovering a lost frontend session"
            Assert.That((recover first |> success).AlreadyApplied, Is.False)
            Assert.That((recover second |> success).AlreadyApplied, Is.True)
            let recovered = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(recovered.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(recovered.OperatorActionRequired, Is.True)
            Assert.That(recovered.SlotAvailable, Is.True)
            Assert.That(recovered.Focused.Value.Attempt.AttemptId, Is.EqualTo(attemptId))
            Assert.That(recovered.Focused.Value.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(recovered.Focused.Value.Attempt.Phase, Is.EqualTo("Queued"))
            let cpPath = $"tasks/recover/attempts/{attemptId}/checkpoints/{recovered.Agent.StateSequence}.json"
            let checkpoint, _ =
                LedgerStore.readAt second (LedgerStore.fetch second |> success) cpPath |> success
            Assert.That(checkpoint.Value, Does.Contain("Recovery"))
            match ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "recover" "Owner" 4 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old generation must not start: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "recover" "Owner" 4 2
            |> success |> ignore
            let head = newCommit "Continued implementation" "continued changes"
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "recover" "Owner" 5 2
                (Guid.NewGuid()) sourceRef head "Same task, recovered generation"
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Working")))


    [<Test>]
    member _.ChangesRequestedBeforeCompletionAreAlsoQueued() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "race"
            let head = newCommit "Candidate" "Candidate"
            let request = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "race" "Owner" 3 1
                request sourceRef head "Review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "race" request "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "race" request "Reviewer" 2
                RequestChanges "Needs correction" |> success |> ignore
            let mid = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(mid.Agent.State, Is.EqualTo("Working"))
            Assert.That(mid.Working.Value.Attempt.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(mid.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "race" "Owner" 5 3 1
                "Stopped before correction" |> success |> ignore
            let finished = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finished.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finished.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(finished.RunnableQueue.Head.Task.TaskId, Is.EqualTo("race"))
            Assert.That(finished.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "race" "Owner" 6 4 2
                "Reissue before-completion rejection" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned")))


    [<Test>]
    member _.IntegrationRequiresExactRemoteApprovedHeadAndSeparateClosure() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "integrate"
            let approvedHead = newCommit "Accepted source" "accepted source tree"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "integrate" "Owner" 3 1
                reviewId sourceRef approvedHead "Cumulative patch" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "integrate" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "integrate" reviewId "Reviewer" 2
                Accept "Accepted exact change" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "integrate" "Owner" 5 3 1
                "Completed source" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "integrate" "Owner" 6 reviewId
            |> success |> ignore
            let actionId = Guid.NewGuid()
            let planOp = Guid.NewGuid()
            let plan binding =
                ReviewWorkflow.planIntegration binding planOp "integrate" actionId reviewId 7
                    sourceRef baseOid
            Assert.That((plan first |> success).AlreadyApplied, Is.False)
            Assert.That((plan second |> success).AlreadyApplied, Is.True)
            let pending = ReviewWorkflow.inspectIntegration second "integrate" actionId |> success
            Assert.That(pending.Integration.Status, Is.EqualTo("PendingSourceAction"))
            Assert.That(pending.Integration.ApprovedHead, Is.EqualTo(approvedHead))
            Assert.That(pending.Integration.ExpectedTargetHead, Is.EqualTo(baseOid))
            Assert.That(pending.Integration.VerificationMethod, Is.Null)
            Assert.That(pending.RemoteState, Is.EqualTo("PendingPush"))
            Assert.That(pending.ObservedTargetHead, Is.EqualTo(baseOid))
            Assert.That(git one [ "ls-remote"; "origin"; sourceRef ], Does.StartWith(baseOid))
            match ReviewWorkflow.recordIntegration first (Guid.NewGuid()) "integrate" actionId 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Local source commit must not count as remote integration: %A" other)
            match ReviewWorkflow.closeIntegratedTask first (Guid.NewGuid()) "integrate" actionId 3 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Cannot close before recorded integration: %A" other)
            ReviewWorkflow.markIntegrationPendingPush second (Guid.NewGuid()) "integrate" actionId 1
            |> success |> ignore
            let waiting = ReviewWorkflow.inspectIntegration first "integrate" actionId |> success
            Assert.That(waiting.Integration.Status, Is.EqualTo("PendingPush"))
            Assert.That(waiting.RemoteState, Is.EqualTo("PendingPush"))
            git one [ "push"; "origin"; $"HEAD:{sourceRef}" ] |> ignore
            let ready = ReviewWorkflow.inspectIntegration second "integrate" actionId |> success
            Assert.That(ready.RemoteState, Is.EqualTo("ReadyToRecord"))
            Assert.That(ready.ObservedTargetHead, Is.EqualTo(approvedHead))
            let recordId = Guid.NewGuid()
            let record binding =
                ReviewWorkflow.recordIntegration binding recordId "integrate" actionId 2
            Assert.That((record second |> success).AlreadyApplied, Is.False)
            Assert.That((record first |> success).AlreadyApplied, Is.True)
            let verified = ReviewWorkflow.inspectIntegration first "integrate" actionId |> success
            Assert.That(verified.Integration.Status, Is.EqualTo("Recorded"))
            Assert.That(verified.Integration.Version, Is.EqualTo(3))
            Assert.That(verified.Integration.VerificationMethod, Is.EqualTo("RemoteExactFastForwardCommit"))
            Assert.That(verified.Integration.ObservedTargetHead, Is.EqualTo(approvedHead))
            Assert.That(verified.Integration.VerifiedSequence, Is.GreaterThan(0L))
            Assert.That(verified.Integration.VerifiedAtUtc, Is.Not.Null)
            let _, integratedTask, _ = ReviewWorkflow.getTask second "integrate" |> success
            Assert.That(integratedTask.Lifecycle, Is.EqualTo("Integrated"))
            Assert.That((ReviewWorkflow.getAgentInbox first "Owner" |> success).AcceptedPendingIntegration.Length,
                        Is.EqualTo(0))
            let agentBefore = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(agentBefore.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.closeIntegratedTask first (Guid.NewGuid()) "integrate" actionId 4
            |> success |> ignore
            let _, closed, _ = ReviewWorkflow.getTask second "integrate" |> success
            Assert.That(closed.Lifecycle, Is.EqualTo("Closed"))
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Completed"))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))

    [<Test>]
    member _.IntegrationRejectsUnapprovedRemoteHeadAndStaleTargetPlan() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "unexpected"
            let approvedHead = newCommit "Reviewed patch" "reviewed changes"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "unexpected" "Owner" 3 1
                reviewId sourceRef approvedHead "Pinned review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "unexpected" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "unexpected" reviewId "Reviewer" 2
                Accept "Approved" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "unexpected" "Owner" 5 3 1
                "Completed" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "unexpected" "Owner" 6 reviewId
            |> success |> ignore
            match ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected"
                    (Guid.NewGuid()) reviewId 7 sourceRef approvedHead with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Target OID must match remote at planning: %A" other)
            match ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected"
                    (Guid.NewGuid()) reviewId 7 "refs/heads/jarvis-orchestration" baseOid with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Ledger orphan ref cannot be source target: %A" other)
            let actionId = Guid.NewGuid()
            ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected" actionId reviewId 7
                sourceRef baseOid |> success |> ignore
            let unapproved = newCommit "Unreviewed extra" "reviewed changes plus unapproved extra"
            git one [ "push"; "origin"; $"HEAD:{sourceRef}" ] |> ignore
            let observed = ReviewWorkflow.inspectIntegration second "unexpected" actionId |> success
            Assert.That(observed.RemoteState, Is.EqualTo("TargetChanged"))
            Assert.That(observed.ObservedTargetHead, Is.EqualTo(unapproved))
            match ReviewWorkflow.recordIntegration second (Guid.NewGuid()) "unexpected" actionId 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Unreviewed advanced remote must be rejected: %A" other)
            let _, task, attempt = ReviewWorkflow.getTask first "unexpected" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("Implemented"))
            Assert.That(attempt.Value.IntegrationActionId, Is.EqualTo(actionId.ToString("N")))
            Assert.That((ReviewWorkflow.inspectIntegration first "unexpected" actionId |> success).Integration.Status,
                        Is.EqualTo("PendingSourceAction"))
            Assert.That(git one [ "ls-remote"; "origin"; $"refs/heads/jarvis-review/unexpected/{reviewId:N}/r1" ],
                        Does.StartWith(approvedHead)))


    [<Test>]
    member _.CanonicalSpecAccepts128KiBAndRejectsOversizeWithoutLedgerWrite() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let html = "<article>" + String.replicate (128 * 1024 - 19) "x" + "</article>"
            // The exact UTF-8 ceiling is measured on the committed HTML bytes.
            let exact = "<p>" + String.replicate (128 * 1024 - 7) "x" + "</p>"
            Assert.That(System.Text.Encoding.UTF8.GetByteCount exact, Is.EqualTo(128 * 1024))
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "largehtml"
                "Large canonical task" exact |> success |> ignore
            let _, task, _, received, _ =
                ReviewWorkflow.getTaskWithSpec second "largehtml" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("Open"))
            Assert.That(received, Is.EqualTo(exact))
            let before = (LedgerStore.fetch first |> success).Commit
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "tooLarge"
                    "Oversize" (exact + "X") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Oversized HTML must fail before mutation: %A" other)
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before))
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "activeHtml"
                    "Active HTML" "<article onclick='alert(1)'>unsafe</article>" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Active HTML must not be published: %A" other)
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before)))

    [<Test>]
    member _.EntityReadersRejectUnknownPhaseAndMissingSchema() =
        let mutateDocument transform =
            withFixture (fun _ _ first second _ _ _ _ ->
                ReviewWorkflow.createTask first (Guid.NewGuid()) "broken" "Broken" |> success |> ignore
                let snapshot = LedgerStore.fetch first |> success
                let path = "tasks/broken/task.json"
                let text, blob = LedgerStore.readAt first snapshot path |> success
                let corrupted = transform text.Value
                let intent =
                    { OperationId = Guid.NewGuid(); Actor = "Corruption fixture"
                      Kind = "CorruptTaskForReadTest"; RequestJson = "{}" }
                LedgerStore.transact first intent (fun _ ->
                    Ok [ { Path = path; ExpectedBlobOid = blob; Content = corrupted } ])
                |> success |> ignore
                match ReviewWorkflow.getTaskWithSpec second "broken" with
                | Error(LedgerIntegrityError _) -> ()
                | other -> Assert.Fail(sprintf "Malformed entity must fail closed: %A" other))
        mutateDocument (fun text -> text.Replace("\"lifecycle\":\"Open\"", "\"lifecycle\":\"Unknown\""))
        mutateDocument (fun text -> text.Replace("\"schemaVersion\":2,", ""))
        mutateDocument (fun text -> text.Replace("\"specVersion\":1,", ""))


    [<Test>]
    member _.LedgerBindingNeedsExplicitTrustAndPersistsVerifiedHead() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let current = (LedgerStore.fetch first |> success).Commit
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let bind approved expected =
                Client.V2ReviewApi.ReviewV2Binding.bindExisting
                    "owner" ctx first.ProjectId first.LedgerId first.Epoch
                    first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl expected approved
            match bind false current with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Unapproved bind must refuse: %A" other)
            Assert.That(File.Exists path, Is.False)
            match bind true (String.replicate 40 "a") with
            | Error(Client.V2LedgerFailureError info) when info.Code = "LedgerHistoryUnavailable" -> ()
            | other -> Assert.Fail(sprintf "Wrong trust head must refuse: %A" other)
            Assert.That(File.Exists path, Is.False)
            let stored = bind true current |> function
                | Ok value -> value
                | Error error -> failwithf "Bind failed: %A" error
            Assert.That(stored.HighestTrustedHead, Is.EqualTo(current))
            Assert.That(File.Exists path, Is.True)
            let extra = ReviewWorkflow.createTask first (Guid.NewGuid()) "trustTask" "Trust" |> success
            let fresh = (LedgerStore.fetch first |> success).Commit
            Assert.That(fresh, Is.Not.EqualTo(current))
            let loaded = Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx
            match loaded with
            | Ok binding -> Assert.That(binding.ProjectId, Is.EqualTo(first.ProjectId))
            | Error error -> Assert.Fail(sprintf "Verified load failed: %A" error)
            let config = System.Text.Json.JsonSerializer.Deserialize<Client.V2ReviewApi.ReviewV2OptIn>(
                File.ReadAllText path)
            Assert.That(config.HighestTrustedHead, Is.EqualTo(fresh))
            git one [ "remote"; "set-url"; "origin"; "file:///not-the-approved-remote.git" ] |> ignore
            match Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx with
            | Error(Client.V2LedgerFailureError failure) when failure.Code = "InvalidBinding" -> ()
            | other -> Assert.Fail(sprintf "Changed remote must be rejected: %A" other)
            git one [ "remote"; "set-url"; "origin"; first.ApprovedFetchUrl ] |> ignore
            Assert.That((Client.V2ReviewApi.ReviewV2Binding.health "owner" ctx |> function
                         | Ok value -> value.trustedLedgerHead
                         | Error error -> failwithf "Health: %A" error), Is.EqualTo(fresh))
            File.Delete path)

    [<Test>]
    member _.InitializeAndRebindRequireExplicitOperatorConsent() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let root = Path.GetDirectoryName one
            let newRemote = Path.Combine(root, "fresh-remote.git")
            git root [ "init"; "--bare"; newRemote ] |> ignore
            git one [ "remote"; "add"; "fresh"; newRemote ] |> ignore
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let create approve =
                Client.V2ReviewApi.ReviewV2Binding.initializeNew
                    "owner" ctx project ledger 1 "fresh" newRemote newRemote approve
            match create false with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Unapproved init must fail: %A" other)
            Assert.That(git one [ "ls-remote"; "fresh"; "refs/heads/jarvis-orchestration" ], Is.Empty)
            let created = create true |> function
                | Ok value -> value
                | Error error -> failwithf "Initialization failed: %A" error
            Assert.That(created.ProjectId, Is.EqualTo(project.ToString("D")))
            Assert.That(created.HighestTrustedHead.Length, Is.EqualTo(40))
            let otherRemote = Path.Combine(root, "mirror.git")
            git root [ "clone"; "--bare"; newRemote; otherRemote ] |> ignore
            git one [ "remote"; "add"; "mirror"; otherRemote ] |> ignore
            let rebind approved expected =
                Client.V2ReviewApi.ReviewV2Binding.rebindRemote
                    "owner" ctx "mirror" otherRemote otherRemote expected approved
            match rebind false created.HighestTrustedHead with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Rebind without approval must fail: %A" other)
            match rebind true (String.replicate 40 "f") with
            | Error(Client.V2LedgerFailureError info) when info.Code = "LedgerHistoryUnavailable" -> ()
            | other -> Assert.Fail(sprintf "Rebind to mismatched trust must fail: %A" other)
            let rebound = rebind true created.HighestTrustedHead |> function
                | Ok value -> value
                | Error error -> failwithf "Rebind failed: %A" error
            Assert.That(rebound.RemoteName, Is.EqualTo("mirror"))
            Assert.That((Client.V2ReviewApi.ReviewV2Binding.health "owner" ctx |> function
                         | Ok value -> value.remoteName
                         | Error error -> failwithf "Health: %A" error), Is.EqualTo("mirror"))
            File.Delete path)


    [<Test>]
    member _.PendingV2IntentSurvivesFailedCallAndCanReplayUnchanged() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let operationId = Guid.NewGuid()
            let command = {| action = "CreateTask"; taskId = "restart"; title = "Original" |}
            let fail () : Result<int, Client.EffectError> =
                Error(Client.GenericError "Simulated lost server response")
            let execute f =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first operationId
                    "V2TaskWrite" command f
            match execute fail with
            | Error(Client.GenericError _) -> ()
            | other -> Assert.Fail(sprintf "Simulated unknown outcome should preserve intent: %A" other)
            let pending () =
                Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success
            let saved = pending ()
            Assert.That(saved.Length, Is.EqualTo(1))
            Assert.That(saved.Head.OperationId, Is.EqualTo(operationId.ToString("D")))
            Assert.That(saved.Head.CommandType, Is.EqualTo("V2TaskWrite"))
            Assert.That(saved.Head.CommandJson, Does.Contain("restart"))
            Assert.That(saved.Head.CommandSha256.Length, Is.EqualTo(64))
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let lookup : Common.V2LedgerReadCommand =
                { ProjectName = "owner"; Action = "PendingOperations"; OperationId = "" }
            let listing = Client.V2ReviewApi.V2LedgerApi.read lookup ctx |> success
            let json = System.Text.Json.JsonSerializer.Serialize(listing)
            Assert.That(json, Does.Contain(operationId.ToString("D")))
            Assert.That(json, Does.Contain("Original"))
            let mismatch =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first operationId
                    "V2TaskWrite" {| action = "CreateTask"; taskId = "restart"; title = "Changed" |}
                    (fun () -> Ok 999)
            match mismatch with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "UUID conflict must reject altered intent: %A" other)
            Assert.That((pending ()).Length, Is.EqualTo(1))
            let recovered = execute (fun () -> Ok 17) |> success
            Assert.That(recovered, Is.EqualTo(17))
            Assert.That((pending ()).Length, Is.EqualTo(0))
            let lostId = Guid.NewGuid()
            let retryLost f =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first lostId
                    "V2ReviewWrite" {| action = "Decide"; requestId = "r1" |} f
            match retryLost (fun () ->
                    Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                        (Client.LedgerStore.OutcomeUnknown "Network stopped after push"))) with
            | Error(Client.V2LedgerFailureError info) ->
                Assert.That(info.Code, Is.EqualTo("OutcomeUnknown"))
                Assert.That(info.OperationId, Is.EqualTo(Some(lostId.ToString("D"))))
            | other -> Assert.Fail(sprintf "Unknown result must retain typed UUID: %A" other)
            Assert.That((pending ()).Length, Is.EqualTo(1))
            retryLost (fun () -> Ok 19) |> success |> ignore
            Assert.That((pending ()).Length, Is.EqualTo(0)))


    [<Test>]
    member _.ReconciliationNeedsVerifiedRemoteReceiptAndExplicitAcknowledgment() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let command taskId =
                System.Text.Json.JsonSerializer.Serialize(
                    {| Action = "CreateTask"; TaskId = taskId
                       Title = taskId; Html = "<h1>Task</h1>" |})
            let acceptedId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first acceptedId "V2TaskWrite" (command "accepted") |> success
            ReviewWorkflow.createTaskWithSpec first acceptedId "accepted" "accepted" "<h1>Task</h1>"
            |> success |> ignore

            let pendingId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first pendingId "V2TaskWrite" (command "notpushed") |> success
            let mismatchedId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first mismatchedId "V2TaskWrite" (command "wrongTask") |> success
            ReviewWorkflow.createTaskWithSpec first mismatchedId "realTask" "realTask" "<h1>Task</h1>"
            |> success |> ignore

            let sameTaskDifferentPayloadId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first sameTaskDifferentPayloadId
                "V2TaskWrite" (command "sameTask") |> success
            ReviewWorkflow.createTaskWithSpec first sameTaskDifferentPayloadId
                "sameTask" "sameTask" "<h1>Different body</h1>" |> success |> ignore

            let foreignId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist { first with LedgerId = Guid.NewGuid() } foreignId
                "V2TaskWrite" (command "foreign") |> success

            let before = (LedgerStore.fetch first |> success).Commit
            let inspected = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(inspected.Length, Is.EqualTo(5))
            let status (id: Guid) =
                inspected |> List.find (fun entry -> entry.operationId = id.ToString("D"))
            Assert.That((status acceptedId).disposition, Is.EqualTo("Applied"))
            Assert.That((status pendingId).disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            Assert.That((status mismatchedId).disposition, Is.EqualTo("IdentityMismatch"))
            Assert.That((status sameTaskDifferentPayloadId).disposition, Is.EqualTo("IdentityMismatch"))
            Assert.That((status foreignId).disposition, Is.EqualTo("BindingMismatch"))
            Assert.That(inspected |> List.forall (fun entry -> entry.snapshotCommit = before), Is.True)
            let accepted = status acceptedId
            let remoteHash = accepted.operation.Value.RequestHash
            let acknowledge (id: Guid) requestHash =
                Client.V2ReviewApi.V2PendingIntentJournal.acknowledge first id requestHash
            match acknowledge acceptedId (String.replicate 64 "f") with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Wrong hash must not acknowledge: %A" other)
            match acknowledge pendingId remoteHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Uncommitted operation must remain pending: %A" other)
            match acknowledge mismatchedId (status mismatchedId).operation.Value.RequestHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Mismatched command must remain pending: %A" other)
            match acknowledge sameTaskDifferentPayloadId (status sameTaskDifferentPayloadId).operation.Value.RequestHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Changed payload on same task must remain pending: %A" other)
            match acknowledge foreignId remoteHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Wrong ledger identity must remain pending: %A" other)
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length, Is.EqualTo(5))
            let recorded = acknowledge acceptedId remoteHash |> success
            Assert.That(recorded.acknowledged, Is.True)
            Assert.That(recorded.verifiedLedgerHead, Is.EqualTo(before))
            Assert.That(recorded.originalResult, Does.Contain(acceptedId.ToString("D")))
            Assert.That(recorded.alreadyAcknowledged, Is.False)
            let repeat = acknowledge acceptedId remoteHash |> success
            Assert.That(repeat.alreadyAcknowledged, Is.True)
            Assert.That(repeat.originalResult, Is.EqualTo(recorded.originalResult))
            let remains = Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success
            Assert.That(remains.Length, Is.EqualTo(4))
            Assert.That(remains |> List.exists (fun entry -> entry.OperationId = acceptedId.ToString("D")),
                        Is.False)
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))


    [<Test>]
    member _.LostResponseRecoversAcceptedRemoteOperationButNeverGuessesFailure() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let command name =
                {| Action = "CreateTask"; TaskId = name; Title = name
                   Html = "<h1>Original</h1>" |}
            let operationId = Guid.NewGuid()
            let remoteAccepted () : Result<LedgerWrite, Client.EffectError> =
                ReviewWorkflow.createTaskWithSpec first operationId
                    "alreadyaccepted" "alreadyaccepted" "<h1>Original</h1>"
                |> success |> ignore
                Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                    (OutcomeUnknown "A source push succeeded but the response was lost"))
            let recovered =
                Client.V2ReviewApi.V2PendingIntentJournal.executeLedgerWrite first operationId
                    "V2TaskWrite" (command "alreadyaccepted") remoteAccepted |> success
            Assert.That(recovered.AlreadyApplied, Is.True)
            Assert.That(recovered.Operation.OperationId, Is.EqualTo(operationId.ToString("D")))
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0))
            let onlyPendingId = Guid.NewGuid()
            let before = (LedgerStore.fetch first |> success).Commit
            let neverAccepted () : Result<LedgerWrite, Client.EffectError> =
                Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                    (OutcomeUnknown "No remote confirmation"))
            match Client.V2ReviewApi.V2PendingIntentJournal.executeLedgerWrite first onlyPendingId
                    "V2TaskWrite" (command "notaccepted") neverAccepted with
            | Error(Client.V2LedgerFailureError failure) ->
                Assert.That(failure.Code, Is.EqualTo("OutcomeUnknown"))
                Assert.That(failure.OperationId, Is.EqualTo(Some(onlyPendingId.ToString("D"))))
                Assert.That(failure.VerifiedLedgerHead, Is.EqualTo(Some before))
            | other -> Assert.Fail(sprintf "Unknown outcome must remain uncertain: %A" other)
            let pending = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(pending.Length, Is.EqualTo(1))
            Assert.That(pending.Head.disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before)))


    [<Test>]
    member _.ReviewSubmissionReconciliationMatchesExactPinnedRevision() =
        withFixture (fun one _ first _ _ sourceRef newCommit initTask ->
            initTask "submission"
            let head = newCommit "Review candidate" "candidate"
            let requestId = Guid.NewGuid()
            let operationId = Guid.NewGuid()
            let original =
                {| Action = "Submit"; TaskId = "submission"; ActorId = "Owner"
                   ExpectedVersion = 3; Generation = 1
                   RequestId = requestId.ToString("D")
                   SourceRef = sourceRef; HeadOid = head; Summary = "Review" |}
            Client.V2ReviewApi.V2PendingIntentJournal.persist first operationId
                "V2ReviewWrite" (System.Text.Json.JsonSerializer.Serialize original) |> success
            ReviewWorkflow.submitReview first operationId "submission" "Owner"
                3 1 requestId sourceRef head "Review" |> success |> ignore
            let entries = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            let item = entries |> List.find (fun entry -> entry.operationId = operationId.ToString("D"))
            Assert.That(item.disposition, Is.EqualTo("Applied"))
            let hash = item.operation.Value.RequestHash
            Client.V2ReviewApi.V2PendingIntentJournal.acknowledge first operationId hash
            |> success |> ignore
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0)))
