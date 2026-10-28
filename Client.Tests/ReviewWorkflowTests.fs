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
        finally Directory.Delete(root, true)

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
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "315" "Owner" 6 req
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
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "101" (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            let c = newCommit "C" "C"
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "100" "Owner" 3 1
                (Guid.NewGuid()) sourceRef c "Snapshot" |> success |> ignore
            match ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "101" "Owner" 1 1 with
            | Error(AgentBusy _) -> ()
            | other -> Assert.Fail(sprintf "Submission should retain working slot: %A" other))

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
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            ReviewWorkflow.createTask first (Guid.NewGuid()) "102" "Queued task" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "102" (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            let fresh = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(fresh.SlotAvailable, Is.True)
            Assert.That(fresh.Working.IsNone, Is.True)
            Assert.That(fresh.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(fresh.RunnableQueue.Head.Task.TaskId, Is.EqualTo("102"))
            Assert.That(fresh.Unassigned, Is.False)
            initTask "101"
            let working = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(working.SlotAvailable, Is.False)
            Assert.That(working.Working.Value.Task.TaskId, Is.EqualTo("101"))
            Assert.That(working.RunnableQueue.Length, Is.EqualTo(1))
            let c = newCommit "Needs review" "C"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "101" "Owner" 3 1
                requestId sourceRef c "Review C" |> success |> ignore
            let submitting = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(submitting.PendingReviews.Length, Is.EqualTo(1))
            Assert.That(submitting.Working.IsSome, Is.True)
            let reviewer = ReviewWorkflow.getAgentInbox second "Reviewer" |> success
            Assert.That(reviewer.AvailableReviews.Length, Is.EqualTo(1))
            Assert.That(reviewer.PendingReviews.Length, Is.EqualTo(0))
            Assert.That(reviewer.SlotAvailable, Is.True)
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "101" requestId "Reviewer" 1 1
            |> success |> ignore
            let claimed = ReviewWorkflow.getAgentInbox first "Reviewer" |> success
            Assert.That(claimed.AvailableReviews.Length, Is.EqualTo(0))
            Assert.That(claimed.PendingReviews.Length, Is.EqualTo(1))
            ReviewWorkflow.decide second (Guid.NewGuid()) "101" requestId "Reviewer" 2 Accept "Passed"
            |> success |> ignore
            let decided = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(decided.Working.IsSome, Is.True)
            Assert.That(decided.PendingReviews.Length, Is.EqualTo(0))
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "101" "Owner" 5 requestId
            |> success |> ignore
            let finalized = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finalized.SlotAvailable, Is.True)
            Assert.That(finalized.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(finalized.RunnableQueue.Length, Is.EqualTo(1))
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
                  ApprovedPushUrl = first.ApprovedPushUrl }
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
                      AssignmentGeneration = 0
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
                let head = newCommit "Task 751 implementation" "code for 751"
                let requestId = Guid.NewGuid()
                let reviewCmd : Common.V2ReviewWriteCommand =
                    { ProjectName = "owner"; Action = "Submit"
                      OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId; RequestId = requestId.ToString("D")
                      ActorId = "Owner"; ReviewerId = ""
                      ExpectedVersion = 3; Generation = 1
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
                Assert.That(after.PendingReviews.Length, Is.EqualTo(1))
                Assert.That(after.PendingReviews.Head.RequestId, Is.EqualTo(requestId.ToString("N")))
            finally
                if File.Exists configPath then File.Delete configPath)
