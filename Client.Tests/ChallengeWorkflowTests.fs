module Client.Tests.ChallengeWorkflowTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open NUnit.Framework

[<TestFixture>]
type ChallengeTests() =
    let git dir args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- dir
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        args |> List.iter p.StartInfo.ArgumentList.Add
        if not (p.Start()) then failwith "Could not start challenge Git fixture."
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 then failwith ("Git fixture failed: " + stderr)
        stdout.Trim()

    let ok = function
        | Ok result -> result
        | Error error -> failwithf "Unexpected challenge ledger error: %A" error

    let failStale = function
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale challenge version: %A" other)
    let failDenied = function
        | Error(ReviewAccessDenied _) -> ()
        | other -> Assert.Fail(sprintf "Expected challenge access denied: %A" other)

    let withFixture (run: LedgerBinding -> LedgerBinding -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-challenges-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "one")
            let two = Path.Combine(root, "two")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            git one [ "config"; "user.name"; "Fixture" ] |> ignore
            git one [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "baseline")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "commit"; "-m"; "baseline" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            git two [ "config"; "user.name"; "Fixture" ] |> ignore
            git two [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let binding location =
                { RepositoryPath = location
                  RemoteName = "origin"; ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = project; LedgerId = ledger; Epoch = 1 }
            let a, b = binding one, binding two
            LedgerStore.initialize a true |> ok |> ignore
            for actor in [ "Agent 3"; "Lead"; "Agent 8"; "Intruder" ] do
                ReviewWorkflow.registerAgent a (Guid.NewGuid()) actor |> ok |> ignore
            run a b
        finally
            Directory.Delete(root, true)

    let definitions =
        [ { OwnerAgentId = "Lead"; Description = "Integrate accepted changes" }
          { OwnerAgentId = "Agent 8"; Description = "Run native benchmark" } ]

    let create binding =
        ChallengeWorkflow.create binding (Guid.NewGuid()) "Agent 3" "Release gate" "" definitions |> ok

    let get binding actor id = ChallengeWorkflow.get binding actor id |> ok

    let notices binding =
        let snapshot = LedgerStore.fetch binding |> ok
        ReviewWorkflow.pendingNotifications binding snapshot "Agent 3" |> ok

    let assignment binding task =
        ReviewWorkflow.createTask binding (Guid.NewGuid()) task task |> ok |> ignore
        let attemptId = Guid.NewGuid().ToString("N")
        ReviewWorkflow.assignTask binding (Guid.NewGuid()) task (Guid.Parse attemptId) "Agent 3"
        |> ok |> ignore
        ReviewWorkflow.beginAttempt binding (Guid.NewGuid()) task "Agent 3" 1 1
        |> ok |> ignore
        let _, taskRecord, maybeAttempt = ReviewWorkflow.getTask binding task |> ok
        let attempt = maybeAttempt.Value
        let inbox = ReviewWorkflow.getAgentInbox binding "Agent 3" |> ok
        Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
        taskRecord.Version, attempt.Version, inbox.Agent.Version, attemptId


    [<Test>]
    member _.TwoOwnersCompleteOnlyAfterAllConditionsAndNotifyExactlyOnce() =
        withFixture (fun a b ->
            let operation = Guid.NewGuid()
            let first = ChallengeWorkflow.create a operation "Agent 3" "Release gate" "" definitions |> ok
            let replay = ChallengeWorkflow.create b operation "Agent 3" "Release gate" "" definitions |> ok
            Assert.That(replay.ChallengeId, Is.EqualTo(first.ChallengeId))
            Assert.That(replay.ConditionIds, Is.EqualTo(first.ConditionIds :> obj))
            Assert.That(replay.Receipt.AlreadyApplied, Is.True)
            let details = get a "Agent 3" first.ChallengeId
            Assert.That(details.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(details.Challenge.CompletionPolicy, Is.EqualTo("All"))
            Assert.That(details.Challenge.OnSatisfied, Is.EqualTo("Notify"))
            Assert.That(details.History.Length, Is.EqualTo(1))
            Assert.That((ChallengeWorkflow.list b "Lead" |> ok).Challenges.Length, Is.EqualTo(1))
            Assert.That((ChallengeWorkflow.list b "Agent 8" |> ok).Challenges.Length, Is.EqualTo(1))
            Assert.That((ChallengeWorkflow.list b "Intruder" |> ok).Challenges.Length, Is.Zero)
            failDenied (ChallengeWorkflow.get b "Intruder" first.ChallengeId)
            let leadCondition, agentCondition = first.ConditionIds.Head, first.ConditionIds.Tail.Head
            let leadOperation = Guid.NewGuid()
            let leadReceipt =
                ChallengeWorkflow.resolve b leadOperation "Lead" first.ChallengeId
                    leadCondition 1 "integrated:commit-123" "" |> ok
            Assert.That(leadReceipt.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.resolve a leadOperation "Lead" first.ChallengeId
                            leadCondition 1 "integrated:commit-123" "" |> ok).AlreadyApplied, Is.True)
            let partial = get a "Agent 3" first.ChallengeId
            Assert.That(partial.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(partial.Challenge.Version, Is.EqualTo(2))
            Assert.That(notices a, Is.Empty)
            failDenied (ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 3" first.ChallengeId
                            agentCondition 2 "fabricated" "")
            failStale (ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" first.ChallengeId
                            agentCondition 1 "stale" "")
            let completed = ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8"
                                first.ChallengeId agentCondition 2 "benchmark:pass" "" |> ok
            Assert.That(completed.AlreadyApplied, Is.False)
            let final = get b "Agent 3" first.ChallengeId
            Assert.That(final.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(final.Challenge.Version, Is.EqualTo(3))
            Assert.That(final.History.Length, Is.EqualTo(3))
            Assert.That(final.Challenge.Conditions |> List.forall (fun c -> c.Status = "Satisfied"), Is.True)
            let doneNotices = notices a
            Assert.That(doneNotices.Length, Is.EqualTo(1))
            Assert.That(doneNotices.Head.Message, Does.Contain("integrated:commit-123"))
            Assert.That(doneNotices.Head.Message, Does.Contain("benchmark:pass"))
            failStale (ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8"
                            first.ChallengeId agentCondition 3 "repeat" "")
        )

    [<Test>]
    member _.BlockedConditionRequiresIssuerReopeningAndKeepsImmutableHistory() =
        withFixture (fun a b ->
            let id = (create a).ChallengeId
            let first = get b "Lead" id
            let target = first.Challenge.Conditions.Head.ConditionId
            let blockedOperation = Guid.NewGuid()
            ChallengeWorkflow.resolve b blockedOperation "Lead" id target 1
                "blocked-by-review" "review has not finished" |> ok |> ignore
            let blocked = get a "Agent 3" id
            Assert.That(blocked.Challenge.Status, Is.EqualTo("Blocked"))
            Assert.That(blocked.Challenge.Conditions.Head.Reason, Is.EqualTo("review has not finished"))
            Assert.That(notices a |> List.length, Is.EqualTo(1))
            Assert.That((ChallengeWorkflow.resolve a blockedOperation "Lead" id target 1
                            "blocked-by-review" "review has not finished" |> ok).AlreadyApplied, Is.True)
            Assert.That(notices b |> List.length, Is.EqualTo(1))
            failDenied (ChallengeWorkflow.reopen a (Guid.NewGuid()) "Agent 8" id target 2 "ignore")
            ChallengeWorkflow.reopen b (Guid.NewGuid()) "Agent 3" id target 2 "review complete"
            |> ok |> ignore
            let reopened = get a "Agent 3" id
            Assert.That(reopened.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(reopened.Challenge.Conditions.Head.Status, Is.EqualTo("Pending"))
            Assert.That(reopened.History.Length, Is.EqualTo(3))
            Assert.That(reopened.History[1].Reason, Is.EqualTo("review has not finished"))
            let remaining = reopened.Challenge.Conditions.Tail.Head.ConditionId
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" id remaining 3 "benchmark:pass" ""
            |> ok |> ignore
            Assert.That((get a "Agent 3" id).Challenge.Status, Is.EqualTo("Pending"))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" id target 4 "review accepted" ""
            |> ok |> ignore
            Assert.That((get a "Agent 3" id).Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(notices a |> List.length, Is.EqualTo(2))
        )

    [<Test>]
    member _.CancellationIsTerminalAndDoesNotSendFalseCompletion() =
        withFixture (fun a b ->
            let id = (create a).ChallengeId
            let target = (get a "Agent 3" id).Challenge.Conditions.Head.ConditionId
            failDenied (ChallengeWorkflow.cancel b (Guid.NewGuid()) "Lead" id 1 "bad"))
            // Two-stage test below runs in a separate fixture, ensuring failure did not alter the ledger.
        withFixture (fun a b ->
            let id = (create a).ChallengeId
            let target = (get a "Agent 3" id).Challenge.Conditions.Head.ConditionId
            let op = Guid.NewGuid()
            let cancel = ChallengeWorkflow.cancel a op "Agent 3" id 1 "no longer required" |> ok
            Assert.That(cancel.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.cancel b op "Agent 3" id 1 "no longer required" |> ok).AlreadyApplied, Is.True)
            let now = get b "Lead" id
            Assert.That(now.Challenge.Status, Is.EqualTo("Cancelled"))
            Assert.That(now.History.Length, Is.EqualTo(2))
            Assert.That(now.History[1].Reason, Is.EqualTo("no longer required"))
            Assert.That(notices a, Is.Empty)
            failStale (ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" id target 2 "late" "")
            failStale (ChallengeWorkflow.reopen b (Guid.NewGuid()) "Agent 3" id target 2 "late")
        )

    [<Test>]
    member _.ConcurrentIndependentConditionWritersAreCASProtected() =
        withFixture (fun a b ->
            let created = create a
            let id = created.ChallengeId
            let t1 = Task.Run(fun () ->
                ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" id created.ConditionIds.Head
                    1 "ready" "")
            let t2 = Task.Run(fun () ->
                ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" id created.ConditionIds.Tail.Head
                    1 "benchmarked" "")
            Task.WaitAll(t1 :> Task, t2 :> Task)
            let passed = [t1.Result;t2.Result] |> List.filter Result.isOk
            let stale = [t1.Result;t2.Result] |> List.filter (function Error(StaleEntityVersion _) -> true | _ -> false)
            Assert.That(passed.Length, Is.EqualTo(1))
            Assert.That(stale.Length, Is.EqualTo(1))
            let state = get a "Agent 3" id
            Assert.That(state.Challenge.Version, Is.EqualTo(2))
            let pending = state.Challenge.Conditions |> List.find (fun c -> c.Status = "Pending")
            let actor = pending.OwnerAgentId
            ChallengeWorkflow.resolve b (Guid.NewGuid()) actor id pending.ConditionId
                2 "reconciled" "" |> ok |> ignore
            let finished = get a "Agent 3" id
            Assert.That(finished.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(finished.Challenge.Conditions.Length, Is.EqualTo(2))
            Assert.That(notices a |> List.length, Is.EqualTo(1))
        )


    [<Test>]
    member _.TwoDependentChallengesRequireExplicitRequeueWithoutLosingAttemptOrOwnership() =
        withFixture (fun a b ->
            let task = "blocked-by-challenges"
            let taskVersion, attemptVersion, agentVersion, attemptId = assignment a task
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Integrate" task
                    [ { OwnerAgentId = "Lead"; Description = "Review accepted" } ] |> ok
            let second =
                ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Benchmark" task
                    [ { OwnerAgentId = "Agent 8"; Description = "Native pass" } ] |> ok
            let dependencyIds = [ first.ChallengeId; second.ChallengeId ]
            let blockOp = Guid.NewGuid()
            let blocked =
                ChallengeWorkflow.blockAttempt a blockOp "Agent 3" task attemptId
                    taskVersion attemptVersion agentVersion 1 "Waiting for approvals" dependencyIds |> ok
            Assert.That(blocked.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.blockAttempt b blockOp "Agent 3" task attemptId
                            taskVersion attemptVersion agentVersion 1 "Waiting for approvals" dependencyIds
                         |> ok).AlreadyApplied, Is.True)
            let inbox = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(inbox.Agent.State, Is.EqualTo("NeedsWork"))
            Assert.That(inbox.Agent.CurrentAssignment, Is.Null)
            Assert.That(inbox.Blocked.Length, Is.EqualTo(1))
            Assert.That(inbox.Blocked[0].Attempt.AttemptId, Is.EqualTo(attemptId))
            Assert.That(inbox.BlockedChallengeWaits.Length, Is.EqualTo(1))
            Assert.That(inbox.BlockedChallengeWaits[0].ChallengeIds, Is.EqualTo(dependencyIds :> obj))
            Assert.That(inbox.BlockedChallengeWaits[0].Reason, Is.EqualTo("Waiting for approvals"))
            let _, taskAfter, attemptAfter = ReviewWorkflow.getTask b task |> ok
            Assert.That(taskAfter.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That(attemptAfter.Value.Phase, Is.EqualTo("Blocked"))
            Assert.That(attemptAfter.Value.OwnerAgentId, Is.EqualTo("Agent 3"))
            Assert.That(attemptAfter.Value.AssignmentGeneration, Is.EqualTo(1))
            let av = inbox.Agent.Version
            let tv = taskAfter.Version
            let pv = attemptAfter.Value.Version
            // The old generic administrative resume path must not be able to
            // bypass a challenge dependency fence.
            failStale (ReviewWorkflow.changeAttemptAdministrationAs b "AuthenticatedUser" (Guid.NewGuid())
                        task "ResumeAttempt" tv pv av 1 "ignore dependencies")
            failStale (ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        tv pv av 1 "still waiting")
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 "review:accepted" "" |> ok |> ignore
            failStale (ChallengeWorkflow.requeueAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                        tv pv av 1 "partial")
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" second.ChallengeId
                second.ConditionIds.Head 1 "benchmark:pass" "" |> ok |> ignore
            let stillBlocked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(stillBlocked.Blocked.Length, Is.EqualTo(1))
            Assert.That(stillBlocked.Agent.State, Is.EqualTo("NeedsWork"),
                "Challenge completion may notify, never implicitly requeue or launch the owner.")
            let requeueOp = Guid.NewGuid()
            let requeued = ChallengeWorkflow.requeueAttempt b requeueOp "Agent 3" task attemptId
                                tv pv av 1 "Review and benchmark done" |> ok
            Assert.That(requeued.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.requeueAttempt a requeueOp "Agent 3" task attemptId
                            tv pv av 1 "Review and benchmark done" |> ok).AlreadyApplied, Is.True)
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.Agent.Execution, Is.EqualTo("Stopped"))
            Assert.That(ready.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptId))
            Assert.That(ready.Blocked, Is.Empty)
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            Assert.That(ready.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(ready.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(ready.RunnableQueue.Head.Attempt.IssuedSequence, Is.EqualTo(requeued.Operation.Sequence))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory b task |> ok
            Assert.That(history |> List.exists (fun item -> item.Action = "WaitOnChallenges"), Is.True)
            Assert.That(history |> List.exists (fun item -> item.Action = "RequeueChallengeBlockedAttempt"), Is.True)
            ReviewWorkflow.beginAttempt b (Guid.NewGuid()) task "Agent 3"
                ready.RunnableQueue.Head.Attempt.Version 2 |> ok |> ignore
            let started = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(started.Agent.State, Is.EqualTo("Working"))
            Assert.That(started.Focused.Value.Attempt.AttemptId, Is.EqualTo(attemptId))
        )

    [<Test>]
    member _.CancelledOrPartiallySatisfiedDependencyCannotRequeueUntilEveryChallengeSucceeds() =
        withFixture (fun a b ->
            let task = "cancelled-challenge-gate"
            let tv, pv, av, attemptId = assignment a task
            let first = ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Needed" task
                            [ { OwnerAgentId = "Lead"; Description = "Approval" } ] |> ok
            let second = ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Might cancel" task
                             [ { OwnerAgentId = "Agent 8"; Description = "Native" } ] |> ok
            let deps = [first.ChallengeId;second.ChallengeId]
            let id = Guid.NewGuid()
            ChallengeWorkflow.blockAttempt a id "Agent 3" task attemptId tv pv av 1 "dependencies" deps
            |> ok |> ignore
            let blocked = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            let pv2 = blocked.Blocked.Head.Attempt.Version
            let av2 = blocked.Agent.Version
            failStale (ChallengeWorkflow.requeueAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                        tv pv2 av2 1 "not done")
            ChallengeWorkflow.cancel b (Guid.NewGuid()) "Agent 3" second.ChallengeId 1
                "No longer available" |> ok |> ignore
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 "done" "" |> ok |> ignore
            failStale (ChallengeWorkflow.requeueAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                        tv pv2 av2 1 "cancelled must not count as satisfied")
            let still = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(still.Agent.State, Is.EqualTo("NeedsWork"))
            Assert.That(still.Blocked.Length, Is.EqualTo(1))
            Assert.That(still.BlockedChallengeWaits.Head.ChallengeIds.Length, Is.EqualTo(2))
            // Prevent an unrelated agent from editing the current owner attempt.
            match ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 8" task attemptId
                      tv pv2 1 1 "spoofed requeue" with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Non-owner requeued a blocked task.")
        )

    [<Test>]
    member _.ManualZeroDependencyBlockCanBeRequeuedExplicitlyAndRejectsStaleOrDuplicateIds() =
        withFixture (fun a b ->
            let task = "manual-block"
            let tv, pv, av, attemptId = assignment a task
            let invalid = Guid.NewGuid().ToString("N")
            match ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                      tv pv av 1 "duplicate" [invalid; invalid] with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Expected duplicate dependency rejection: %A" other)
            let op = Guid.NewGuid()
            ChallengeWorkflow.blockAttempt a op "Agent 3" task attemptId
                tv pv av 1 "Waiting for user input" [] |> ok |> ignore
            let blocked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(blocked.Agent.State, Is.EqualTo("NeedsWork"))
            Assert.That(blocked.BlockedChallengeWaits.Head.ChallengeIds, Is.Empty)
            Assert.That(blocked.BlockedChallengeWaits.Head.Reason, Is.EqualTo("Waiting for user input"))
            failStale (ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                        tv pv blocked.Agent.Version 1 "outdated attempt version")
            let nowAttempt = blocked.Blocked.Head.Attempt.Version
            ChallengeWorkflow.requeueAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                tv nowAttempt blocked.Agent.Version 1 "Manual condition cleared" |> ok |> ignore
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.Agent.Execution, Is.EqualTo("Stopped"))
            Assert.That(ready.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
        )


    [<Test>]
    member _.LongEvidenceAndAllConditionsFitDurableNotificationBudget() =
        withFixture (fun a b ->
            let title = String('T', 2048)
            let description = String('D', 2048)
            let evidence = String('E', 8192)
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                    [ { OwnerAgentId = "Lead"; Description = description }
                      { OwnerAgentId = "Agent 8"; Description = description } ] |> ok
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 evidence "" |> ok |> ignore
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" first.ChallengeId
                first.ConditionIds.Tail.Head 2 evidence "" |> ok |> ignore
            let doneNotice = notices b
            Assert.That(doneNotice.Length, Is.EqualTo(1))
            let body = doneNotice.Head.Message
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(body), Is.LessThanOrEqualTo(4096))
            Assert.That(body, Does.Contain("1. Lead:"))
            Assert.That(body, Does.Contain("2. Agent 8:"))
            Assert.That(body, Does.Contain("Full evidence: get_challenge"))
            let details = get b "Agent 3" first.ChallengeId
            Assert.That(details.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(details.Challenge.Conditions |> List.forall (fun c -> c.Evidence.Length = 8192), Is.True)
            let blocked =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                    [ { OwnerAgentId = "Lead"; Description = description } ] |> ok
            let reason = String('R', 4096)
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" blocked.ChallengeId
                blocked.ConditionIds.Head 1 evidence reason |> ok |> ignore
            let notifications = notices a
            Assert.That(notifications.Length, Is.EqualTo(2))
            let blockedNotice = notifications |> List.find (fun n -> n.Message.Contains("blocked"))
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(blockedNotice.Message), Is.LessThanOrEqualTo(4096))
            Assert.That(blockedNotice.Message, Does.Contain("Full history: get_challenge"))
            let blockedEntity = get a "Agent 3" blocked.ChallengeId
            Assert.That(blockedEntity.Challenge.Status, Is.EqualTo("Blocked"))
            Assert.That(blockedEntity.Challenge.Conditions.Head.Reason.Length, Is.EqualTo(4096))
            Assert.That(blockedEntity.Challenge.Conditions.Head.Evidence.Length, Is.EqualTo(8192))
        )


    [<Test>]
    member _.ChallengeListPagesAreBoundedAndSnapshotPinned() =
        withFixture (fun a b ->
            let title = String('T', 2048)
            let created =
                [ for index in 1 .. 3 ->
                    ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" (title + string index) ""
                        [ { OwnerAgentId = "Lead"; Description = "Check stage" } ] ]
            // Max title size is 2048 bytes: use a valid bounded title.
            Assert.That(created |> List.forall Result.isError, Is.True)
            let ids =
                [ for index in 1 .. 3 ->
                    (ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                         [ { OwnerAgentId = "Lead"; Description = $"Step {index}" } ] |> ok).ChallengeId ]
            let first = ChallengeWorkflow.listPage b "Agent 3" 1 "" "" |> ok
            Assert.That(first.Challenges.Length, Is.EqualTo(1))
            Assert.That(first.NextCursor, Is.Not.Empty)
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(first.Challenges.Head.Title), Is.LessThanOrEqualTo(160))
            Assert.That(first.Challenges.Head.OwnedUnresolvedConditionIds, Is.Empty)
            let second = ChallengeWorkflow.listPage a "Agent 3" 1 first.NextCursor first.SnapshotCommit |> ok
            Assert.That(second.Challenges.Length, Is.EqualTo(1))
            Assert.That(second.NextCursor, Is.Not.Empty)
            let third = ChallengeWorkflow.listPage b "Agent 3" 1 second.NextCursor first.SnapshotCommit |> ok
            Assert.That(third.Challenges.Length, Is.EqualTo(1))
            Assert.That(third.NextCursor, Is.Null)
            let returned = [first; second; third] |> List.collect _.Challenges |> List.map _.ChallengeId
            Assert.That(returned |> Set.ofList, Is.EqualTo((ids |> Set.ofList) :> obj))
            match ChallengeWorkflow.listPage a "Agent 3" 51 "" "" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail($"Expected list page limit rejection: {other}")
            ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "new" ""
                [ { OwnerAgentId = "Lead"; Description = "Extra" } ] |> ok |> ignore
            match ChallengeWorkflow.listPage b "Agent 3" 1 first.NextCursor first.SnapshotCommit with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail($"Expected snapshot-pinned page failure after mutation: {other}")
        )
