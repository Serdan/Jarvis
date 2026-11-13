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
        [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Integrate accepted changes" }
          { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Run native benchmark" } ]

    let create binding =
        ChallengeWorkflow.create binding (Guid.NewGuid()) "Agent 3" "Release gate" "" definitions |> ok

    let get binding actor id = ChallengeWorkflow.get binding actor id |> ok

    // Inspect raw canonical paths, not just pending inbox items, so a bad
    // challenge emitter cannot hide notifications by targeting another agent,
    // acknowledging them, or interacting with notification block/mute state.
    let notices binding =
        let snapshot = LedgerStore.fetch binding |> ok
        LedgerStore.listAt binding snapshot "notifications" |> ok

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
    member _.TwoOwnersCompleteOnlyAfterAllConditionsWithoutNotifications() =
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
            Assert.That(notices a, Is.Empty)
            Assert.That(final.Challenge.Conditions.Head.Evidence, Is.EqualTo("integrated:commit-123"))
            Assert.That(final.Challenge.Conditions.Tail.Head.Evidence, Is.EqualTo("benchmark:pass"))
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
            Assert.That(notices a, Is.Empty)
            Assert.That((ChallengeWorkflow.resolve a blockedOperation "Lead" id target 1
                            "blocked-by-review" "review has not finished" |> ok).AlreadyApplied, Is.True)
            Assert.That(notices b, Is.Empty)
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
            Assert.That(notices a, Is.Empty)
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
            Assert.That(notices a, Is.Empty)
        )


    [<Test>]
    member _.TwoDependentChallengesRequireExplicitRequeueWithoutLosingAttemptOrOwnership() =
        withFixture (fun a b ->
            let task = "blocked-by-challenges"
            let taskVersion, attemptVersion, agentVersion, attemptId = assignment a task
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Integrate" task
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Review accepted" } ] |> ok
            let second =
                ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Benchmark" task
                    [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Native pass" } ] |> ok
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
            let resolved =
                ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" second.ChallengeId
                    second.ConditionIds.Head 1 "benchmark:pass" "" |> ok
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.Agent.Execution, Is.EqualTo("Stopped"))
            Assert.That(ready.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptId))
            Assert.That(ready.Blocked, Is.Empty)
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            Assert.That(ready.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(ready.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(ready.RunnableQueue.Head.Attempt.IssuedSequence, Is.EqualTo(resolved.Operation.Sequence))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory b task |> ok
            Assert.That(history |> List.exists (fun item -> item.Action = "WaitOnChallenges"), Is.True)
            Assert.That(history |> List.exists (fun item -> item.Action = "RequeueChallengeBlockedAttempt"), Is.True)
            ReviewWorkflow.beginAttempt b (Guid.NewGuid()) task "Agent 3"
                ready.RunnableQueue.Head.Attempt.Version 2 |> ok |> ignore
            let started = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(started.Agent.State, Is.EqualTo("Working"))
            Assert.That(started.Focused.Value.Attempt.AttemptId, Is.EqualTo(attemptId))
            Assert.That(notices a, Is.Empty,
                "Challenge dependencies, completion, requeue and activation never create notifications.")
        )

    [<Test>]
    member _.CancelledOrPartiallySatisfiedDependencyCannotRequeueUntilEveryChallengeSucceeds() =
        withFixture (fun a b ->
            let task = "cancelled-challenge-gate"
            let tv, pv, av, attemptId = assignment a task
            let first = ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Needed" task
                            [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Approval" } ] |> ok
            let second = ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Might cancel" task
                             [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Native" } ] |> ok
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
            Assert.That(notices a, Is.Empty,
                "Cancelled and partially satisfied dependencies have no notification side effects.")
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
            let foreign =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Lead" "Private" task
                    [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Not Agent 3's condition" } ] |> ok
            match ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                      tv pv av 1 "Unrelated private challenge"
                      [foreign.ChallengeId] with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Unrelated challenge reference must be inaccessible: %A" other)
            match ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                      tv pv av 1 "Mixed-case duplicate"
                      [foreign.ChallengeId; foreign.ChallengeId.ToUpperInvariant()] with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Canonical duplicate challenge IDs must be rejected: %A" other)
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
            Assert.That(notices a, Is.Empty,
                "Even explicit manual block/requeue produces no notifications.")
        )


    [<Test>]
    member _.LongEvidenceIsPreservedWithoutAnyChallengeNotifications() =
        withFixture (fun a b ->
            let title = String('T', 2048)
            let description = String('D', 2048)
            let evidence = String('E', 8192)
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = description }
                      { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = description } ] |> ok
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 evidence "" |> ok |> ignore
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" first.ChallengeId
                first.ConditionIds.Tail.Head 2 evidence "" |> ok |> ignore
            Assert.That(notices b, Is.Empty,
                "Even maximum-size evidence must never produce a notification.")
            let details = get b "Agent 3" first.ChallengeId
            Assert.That(details.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(details.Challenge.Conditions |> List.forall (fun c -> c.Evidence.Length = 8192), Is.True)
            let blocked =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = description } ] |> ok
            let reason = String('R', 4096)
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" blocked.ChallengeId
                blocked.ConditionIds.Head 1 evidence reason |> ok |> ignore
            Assert.That(notices a, Is.Empty,
                "Blocking a condition must not create a notification.")
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
                        [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Check stage" } ] ]
            // Max title size is 2048 bytes: use a valid bounded title.
            Assert.That(created |> List.forall Result.isError, Is.True)
            let ids =
                [ for index in 1 .. 3 ->
                    (ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" title ""
                         [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = $"Step {index}" } ] |> ok).ChallengeId ]
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
                [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Extra" } ] |> ok |> ignore
            match ChallengeWorkflow.listPage b "Agent 3" 1 first.NextCursor first.SnapshotCommit with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail($"Expected snapshot-pinned page failure after mutation: {other}")
        )


    [<Test>]
    member _.EventHistoryPagesAreSnapshotPinnedAndBoundedAfterRepeatedReopen() =
        withFixture (fun a b ->
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Repeated block/reopen" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Reopenable condition" } ] |> ok
            let id = first.ChallengeId
            let condition = first.ConditionIds.Head
            for round in 0 .. 9 do
                let version = 1 + round * 2
                ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" id
                    condition version "blocked evidence" $"blocked round {round}" |> ok |> ignore
                ChallengeWorkflow.reopen a (Guid.NewGuid()) "Agent 3" id
                    condition (version + 1) "retry" |> ok |> ignore
            let page1 = ChallengeWorkflow.getPage b "Agent 3" id 8 0L "" |> ok
            Assert.That(page1.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(page1.History.Length, Is.EqualTo(8))
            Assert.That(page1.NextEventSequence, Is.GreaterThan(0L))
            let page2 =
                ChallengeWorkflow.getPage a "Agent 3" id 8
                    page1.NextEventSequence page1.SnapshotCommit |> ok
            Assert.That(page2.History.Length, Is.EqualTo(8))
            Assert.That(page2.NextEventSequence, Is.GreaterThan(page1.NextEventSequence))
            let page3 =
                ChallengeWorkflow.getPage b "Agent 3" id 8
                    page2.NextEventSequence page1.SnapshotCommit |> ok
            Assert.That(page3.History.Length, Is.EqualTo(5))
            Assert.That(page3.NextEventSequence, Is.EqualTo(0L))
            let all = [page1; page2; page3] |> List.collect _.History
            Assert.That(all.Length, Is.EqualTo(21))
            let sequences = all |> List.map _.Sequence
            Assert.That(sequences, Is.EqualTo((sequences |> List.sort) :> obj))
            Assert.That((sequences |> List.distinct).Length, Is.EqualTo(21))
            let fromOldSnapshot = page1.SnapshotCommit
            ChallengeWorkflow.cancel a (Guid.NewGuid()) "Agent 3" id 21 "end" |> ok |> ignore
            failStale (ChallengeWorkflow.getPage b "Agent 3" id 8
                            page1.NextEventSequence fromOldSnapshot)
            let last = ChallengeWorkflow.get b "Agent 3" id |> ok
            Assert.That(last.Challenge.Status, Is.EqualTo("Cancelled"))
            Assert.That(last.History.Length, Is.EqualTo(16))
            Assert.That(last.NextEventSequence, Is.GreaterThan(0L))
        )


    [<Test>]
    member _.SatisfyingAnotherConditionWhileBlockedNeverCreatesNotifications() =
        withFixture (fun a b ->
            let created = create a
            let leadId, benchmarkId = created.ConditionIds.Head, created.ConditionIds.Tail.Head
            let blockedOperation = Guid.NewGuid()
            ChallengeWorkflow.resolve a blockedOperation "Lead" created.ChallengeId
                leadId 1 "review requested" "Awaiting approval" |> ok |> ignore
            Assert.That(notices b, Is.Empty)
            Assert.That((get b "Agent 3" created.ChallengeId).Challenge.Conditions.Head.Reason,
                        Is.EqualTo("Awaiting approval"))
            let satisfiedOperation = Guid.NewGuid()
            ChallengeWorkflow.resolve b satisfiedOperation "Agent 8" created.ChallengeId
                benchmarkId 2 "benchmark:passed" "" |> ok |> ignore
            // Replaying a successful transition does not duplicate state or history.
            Assert.That((ChallengeWorkflow.resolve a satisfiedOperation "Agent 8"
                            created.ChallengeId benchmarkId 2 "benchmark:passed" "" |> ok).AlreadyApplied,
                        Is.True)
            let after = get a "Agent 3" created.ChallengeId
            Assert.That(after.Challenge.Status, Is.EqualTo("Blocked"))
            Assert.That(after.Challenge.Conditions.Head.Status, Is.EqualTo("Blocked"))
            Assert.That(after.Challenge.Conditions.Tail.Head.Status, Is.EqualTo("Satisfied"))
            Assert.That(after.History.Length, Is.EqualTo(3))
            Assert.That(notices a, Is.Empty,
                "Satisfying a different condition while aggregate remains Blocked cannot send any notification.")
            ChallengeWorkflow.reopen b (Guid.NewGuid()) "Agent 3" created.ChallengeId
                leadId 3 "Review finished" |> ok |> ignore
            Assert.That((get a "Agent 3" created.ChallengeId).Challenge.Status, Is.EqualTo("Pending"))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" created.ChallengeId
                leadId 4 "accepted" "" |> ok |> ignore
            let final = get b "Agent 3" created.ChallengeId
            Assert.That(final.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(notices b, Is.Empty)
            Assert.That(final.Challenge.Conditions.Head.Evidence, Is.EqualTo("accepted"))
            Assert.That(final.Challenge.Conditions.Tail.Head.Evidence, Is.EqualTo("benchmark:passed"))
        )

    [<Test>]
    member _.DistinctBlockReopenAndReblockTransitionsNeverSendNotifications() =
        withFixture (fun a b ->
            let created = create a
            let leadId, benchmarkId = created.ConditionIds.Head, created.ConditionIds.Tail.Head
            let firstOperation = Guid.NewGuid()
            ChallengeWorkflow.resolve a firstOperation "Lead" created.ChallengeId
                leadId 1 "review evidence" "Blocked by review" |> ok |> ignore
            Assert.That(notices b, Is.Empty)
            Assert.That((get b "Agent 3" created.ChallengeId).Challenge.Conditions.Head.Reason,
                        Is.EqualTo("Blocked by review"))
            let secondOperation = Guid.NewGuid()
            ChallengeWorkflow.resolve b secondOperation "Agent 8" created.ChallengeId
                benchmarkId 2 "benchmark failed" "Blocked by benchmark" |> ok |> ignore
            Assert.That(notices a, Is.Empty,
                "Even multiple independent Blocked conditions must produce no notifications.")
            Assert.That((get a "Agent 3" created.ChallengeId).Challenge.Conditions.Tail.Head.Reason,
                        Is.EqualTo("Blocked by benchmark"))
            Assert.That((ChallengeWorkflow.resolve a secondOperation "Agent 8" created.ChallengeId
                            benchmarkId 2 "benchmark failed" "Blocked by benchmark" |> ok).AlreadyApplied, Is.True)
            Assert.That(notices b, Is.Empty)
            ChallengeWorkflow.reopen a (Guid.NewGuid()) "Agent 3" created.ChallengeId
                benchmarkId 3 "Retry benchmark" |> ok |> ignore
            Assert.That((get b "Agent 3" created.ChallengeId).Challenge.Status, Is.EqualTo("Blocked"))
            Assert.That(notices a, Is.Empty,
                "Reopening a blocked condition never emits a notification.")
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" created.ChallengeId
                benchmarkId 4 "retry failed" "New benchmark blocker" |> ok |> ignore
            let afterReblock = get a "Agent 3" created.ChallengeId
            Assert.That(afterReblock.Challenge.Status, Is.EqualTo("Blocked"))
            Assert.That(afterReblock.Challenge.Conditions.Tail.Head.Reason,
                        Is.EqualTo("New benchmark blocker"))
            Assert.That(afterReblock.History.Length, Is.EqualTo(5))
            Assert.That(notices a, Is.Empty,
                "Re-blocking after reopening must update durable state but never notify.")
        )


    [<Test>]
    member _.OptionalAssigneeSubscriptionAndCASDispatchWithoutClaimOrNotifications() =
        withFixture (fun a b ->
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Benchmark" ""
                    [ { OwnerKind = ""; OwnerAgentId = ""; SuggestedAgentId = "Agent 8"; Description = "Run native benchmark" } ]
                |> ok
            let conditionId = created.ConditionIds.Head
            let issuer = get a "Agent 3" created.ChallengeId
            Assert.That(issuer.Challenge.Conditions.Head.OwnerAgentId, Is.Empty)
            Assert.That(issuer.Challenge.Conditions.Head.SuggestedAgentId, Is.EqualTo("Agent 8"))
            Assert.That((ChallengeWorkflow.list a "Lead" |> ok).Challenges, Is.Empty)
            let before = LedgerStore.fetch a |> ok
            let unsubscribed =
                ReviewWorkflow.getAgentInboxAt a before "Lead" |> ok
                |> ChallengeWorkflow.enrichInboxAt a before "Lead" |> ok
            Assert.That(unsubscribed.ChallengeTriage, Is.Empty)
            failDenied (ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead"
                            created.ChallengeId conditionId 1 "spoof" "")
            let subscribeOp = Guid.NewGuid()
            let firstSub = ChallengeWorkflow.setSubscription b subscribeOp "Lead" true |> ok
            Assert.That(firstSub.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.setSubscription a subscribeOp "Lead" true |> ok).AlreadyApplied, Is.True)
            let after = LedgerStore.fetch a |> ok
            let inbox =
                ReviewWorkflow.getAgentInboxAt a after "Lead" |> ok
                |> ChallengeWorkflow.enrichInboxAt a after "Lead" |> ok
            Assert.That(inbox.ChallengeSubscribed, Is.True)
            Assert.That(inbox.ChallengeTriage.Length, Is.EqualTo(1))
            Assert.That(inbox.ChallengeTriage.Head.ConditionId, Is.EqualTo(conditionId))
            Assert.That(inbox.ChallengeTriage.Head.SuggestedAgentId, Is.EqualTo("Agent 8"))
            Assert.That((ChallengeWorkflow.list a "Lead" |> ok).Challenges.Length, Is.EqualTo(1))
            match ChallengeWorkflow.assignCondition b (Guid.NewGuid()) "Intruder"
                    created.ChallengeId conditionId 1 "Lead" with
            | Error(StaleEntityVersion _) | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail($"Only issuer or subscribed dispatcher can assign: {other}")
            ChallengeWorkflow.setSubscription a (Guid.NewGuid()) "Intruder" true |> ok |> ignore
            let assignOp = Guid.NewGuid()
            let assigned =
                ChallengeWorkflow.assignCondition b assignOp "Lead"
                    created.ChallengeId conditionId 1 "Agent 8" |> ok
            Assert.That(assigned.AlreadyApplied, Is.False)
            Assert.That((ChallengeWorkflow.assignCondition a assignOp "Lead"
                            created.ChallengeId conditionId 1 "Agent 8" |> ok).AlreadyApplied, Is.True)
            let assignment = get a "Agent 8" created.ChallengeId
            Assert.That(assignment.Challenge.Version, Is.EqualTo(2))
            Assert.That(assignment.Challenge.Conditions.Head.OwnerAgentId, Is.EqualTo("Agent 8"))
            Assert.That(assignment.Challenge.Conditions.Head.ConditionId, Is.EqualTo(conditionId))
            Assert.That(assignment.History.Length, Is.EqualTo(2))
            Assert.That(assignment.History.Tail.Head.BeforeOwnerAgentId, Is.Empty)
            Assert.That(assignment.History.Tail.Head.AfterOwnerAgentId, Is.EqualTo("Agent 8"))
            failStale (ChallengeWorkflow.assignCondition a (Guid.NewGuid()) "Lead"
                           created.ChallengeId conditionId 1 "Intruder")
            let assignedInbox = LedgerStore.fetch b |> ok
            let receiver =
                ReviewWorkflow.getAgentInboxAt b assignedInbox "Agent 8" |> ok
                |> ChallengeWorkflow.enrichInboxAt b assignedInbox "Agent 8" |> ok
            Assert.That(receiver.ChallengeAssigned.Length, Is.EqualTo(1))
            Assert.That(receiver.ChallengeAssigned.Head.ConditionId, Is.EqualTo(conditionId))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" created.ChallengeId
                conditionId 2 "benchmark-pass" "" |> ok |> ignore
            let completed = LedgerStore.fetch b |> ok
            let issuerInbox =
                ReviewWorkflow.getAgentInboxAt b completed "Agent 3" |> ok
                |> ChallengeWorkflow.enrichInboxAt b completed "Agent 3" |> ok
            Assert.That(issuerInbox.ChallengeRecent.Length, Is.EqualTo(1))
            Assert.That(issuerInbox.ChallengeRecent.Head.EvidencePreview, Is.EqualTo("benchmark-pass"))
            Assert.That(issuerInbox.ChallengeRecent.Head.ChallengeStatus, Is.EqualTo("Satisfied"))
            Assert.That(notices b, Is.Empty)
        )


    [<Test>]
    member _.SatisfiedDependenciesRemainEligibleUntilBusyOwnerReleasesTheirSlot() =
        withFixture (fun a b ->
            let task = "pending-challenge"
            let tv, pv, av, attemptId = assignment a task
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Benchmark gate" task
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Complete benchmark" } ] |> ok
            ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                tv pv av 1 "Awaiting benchmark" [ created.ChallengeId ] |> ok |> ignore
            let busyTask = "another-urgent-task"
            let _, _, _, busyAttemptId = assignment b busyTask
            let busy = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(busy.Agent.State, Is.EqualTo("Working"))
            Assert.That(busy.Agent.CurrentAssignment.AttemptId, Is.EqualTo(busyAttemptId))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" created.ChallengeId
                created.ConditionIds.Head 1 "benchmark:pass" "" |> ok |> ignore
            let eligible = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(eligible.Agent.State, Is.EqualTo("Working"))
            Assert.That(eligible.Agent.CurrentAssignment.AttemptId, Is.EqualTo(busyAttemptId))
            Assert.That(eligible.BlockedChallengeWaits.Head.Status, Is.EqualTo("Eligible"))
            Assert.That(eligible.Blocked.Head.Attempt.AttemptId, Is.EqualTo(attemptId))
            ReviewWorkflow.completeAgentWork a (Guid.NewGuid()) busyTask "Agent 3" 0 0 0
                "Urgent task complete" |> ok |> ignore
            let awakened = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(awakened.Agent.State, Is.EqualTo("Ready"))
            Assert.That(awakened.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptId))
            Assert.That(awakened.Blocked, Is.Empty)
            Assert.That(awakened.BlockedChallengeWaits, Is.Empty)
            Assert.That(awakened.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(awakened.RunnableQueue.Head.Attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(notices b, Is.Empty)
        )


    [<Test>]
    member _.SubscribedDispatcherCanSplitUnassignedWorkWithStableAuditedIds() =
        withFixture (fun a b ->
            let original =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Split benchmark" ""
                    [ { OwnerKind = ""; OwnerAgentId = ""; SuggestedAgentId = "Agent 8"
                        Description = "Benchmark and integration" } ] |> ok
            ChallengeWorkflow.setSubscription a (Guid.NewGuid()) "Lead" true |> ok |> ignore
            let parts =
                [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Run benchmark" }
                  { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Integrate result" } ]
            failDenied (ChallengeWorkflow.splitCondition b (Guid.NewGuid()) "Intruder"
                            original.ChallengeId original.ConditionIds.Head 1 parts)
            let operationId = Guid.NewGuid()
            let split = ChallengeWorkflow.splitCondition b operationId "Lead"
                            original.ChallengeId original.ConditionIds.Head 1 parts |> ok
            Assert.That(split.ConditionIds.Length, Is.EqualTo(2))
            Assert.That(split.ConditionIds.Head, Is.EqualTo(original.ConditionIds.Head))
            Assert.That((ChallengeWorkflow.splitCondition a operationId "Lead"
                            original.ChallengeId original.ConditionIds.Head 1 parts |> ok).Receipt.AlreadyApplied, Is.True)
            failStale (ChallengeWorkflow.splitCondition a (Guid.NewGuid()) "Lead"
                           original.ChallengeId original.ConditionIds.Head 1 parts)
            let details = get a "Agent 3" original.ChallengeId
            Assert.That(details.Challenge.Version, Is.EqualTo(2))
            Assert.That(details.Challenge.Conditions.Length, Is.EqualTo(2))
            Assert.That(details.Challenge.Conditions.Head.OwnerAgentId, Is.EqualTo("Agent 8"))
            Assert.That(details.Challenge.Conditions.Tail.Head.OwnerAgentId, Is.EqualTo("Lead"))
            Assert.That(details.History.Tail.Head.Action, Is.EqualTo("SplitChallengeCondition"))
            Assert.That(details.History.Tail.Head.Evidence, Does.Contain("Benchmark and integration"))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" original.ChallengeId
                split.ConditionIds.Head 2 "benchmark-pass" "" |> ok |> ignore
            Assert.That((get b "Agent 3" original.ChallengeId).Challenge.Status, Is.EqualTo("Pending"))
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" original.ChallengeId
                split.ConditionIds.Tail.Head 3 "integration-pass" "" |> ok |> ignore
            Assert.That((get a "Agent 3" original.ChallengeId).Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(notices a, Is.Empty)
        )


    [<Test>]
    member _.ParkedOwnerKeepsDurableWakeUntilExplicitUnpark() =
        withFixture (fun a b ->
            let task = "paused-owner"
            let tv, pv, av, attemptId = assignment a task
            let challenge =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Approval" task
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Approve change" } ] |> ok
            ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                tv pv av 1 "Awaiting approval" [ challenge.ChallengeId ] |> ok |> ignore
            let unassigned = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            ReviewWorkflow.parkAgent a (Guid.NewGuid()) "Agent 3" unassigned.Agent.Version
                "Intentional pause" |> ok |> ignore
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" challenge.ChallengeId
                challenge.ConditionIds.Head 1 "approved" "" |> ok |> ignore
            let parked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
            Assert.That(parked.Agent.CurrentAssignment, Is.Null)
            Assert.That(parked.BlockedChallengeWaits.Head.Status, Is.EqualTo("Eligible"))
            ReviewWorkflow.setAgentAdministrativeStateAs b "Orchestrator" (Guid.NewGuid())
                "Agent 3" "NeedsWork" parked.Agent.Version "Unpark" |> ok |> ignore
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptId))
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            Assert.That(ready.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(notices a, Is.Empty)
        )


    [<Test>]
    member _.FinalChallengeResolutionRequeuesEveryDistinctDependentOwner() =
        withFixture (fun a b ->
            let task3 = "dependent-agent-3"
            let tv3, pv3, av3, attempt3 = assignment a task3
            let challenge =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Shared gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Integration" }
                      { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Verification" } ] |> ok
            ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task3 attempt3
                tv3 pv3 av3 1 "Shared dependency" [ challenge.ChallengeId ] |> ok |> ignore

            let task8 = "dependent-agent-8"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task8 task8 |> ok |> ignore
            let attempt8 = Guid.NewGuid().ToString("N")
            ReviewWorkflow.assignTask b (Guid.NewGuid()) task8 (Guid.Parse attempt8) "Agent 8"
                |> ok |> ignore
            ReviewWorkflow.beginAttempt a (Guid.NewGuid()) task8 "Agent 8" 1 1
                |> ok |> ignore
            let _, task8State, attempt8State = ReviewWorkflow.getTask b task8 |> ok
            let inbox8 = ReviewWorkflow.getAgentInbox a "Agent 8" |> ok
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 8" task8 attempt8
                task8State.Version attempt8State.Value.Version inbox8.Agent.Version 1
                "Shared dependency" [ challenge.ChallengeId ] |> ok |> ignore

            let pinned = LedgerStore.fetch a |> ok
            let links = LedgerStore.listAt a pinned ("challenge-waits/by-challenge/" + challenge.ChallengeId) |> ok
            Assert.That(links.Length, Is.EqualTo(2), "Both dependent attempts must have reverse-index entries.")
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" challenge.ChallengeId
                challenge.ConditionIds.Head 1 "integrated" "" |> ok |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox a "Agent 3" |> ok).Blocked.Length, Is.EqualTo(1))
            Assert.That((ReviewWorkflow.getAgentInbox a "Agent 8" |> ok).Blocked.Length, Is.EqualTo(1))
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" challenge.ChallengeId
                challenge.ConditionIds.Tail.Head 2 "verified" "" |> ok |> ignore
            let ready3 = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            let ready8 = ReviewWorkflow.getAgentInbox b "Agent 8" |> ok
            Assert.That(ready3.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready8.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready3.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attempt3))
            Assert.That(ready8.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attempt8))
            Assert.That(ready3.BlockedChallengeWaits, Is.Empty)
            Assert.That(ready8.BlockedChallengeWaits, Is.Empty)
            Assert.That(notices b, Is.Empty)
        )


    [<Test>]
    member _.AlreadySatisfiedDependenciesCannotCreateAnUnwakeableWait() =
        withFixture (fun a b ->
            let gate =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Completed gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Complete work" } ] |> ok
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" gate.ChallengeId
                gate.ConditionIds.Head 1 "completed" "" |> ok |> ignore
            let task = "all-done-before-block"
            let tv, pv, av, attemptId = assignment a task
            failStale (ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                           tv pv av 1 "Wait for completed gate" [ gate.ChallengeId ])
            let inbox = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.Blocked, Is.Empty)
            Assert.That(inbox.BlockedChallengeWaits, Is.Empty)
            Assert.That(notices b, Is.Empty)
        )

    [<Test>]
    member _.OnePreviouslySatisfiedDependencyStillAllowsBlockingOnRemainingWork() =
        withFixture (fun a b ->
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Earlier gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Approve first" } ] |> ok
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 "approved" "" |> ok |> ignore
            let second =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Remaining gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Verify second" } ] |> ok
            let task = "partly-complete-before-block"
            let tv, pv, av, attemptId = assignment b task
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                tv pv av 1 "Remaining gate must finish"
                [ first.ChallengeId; second.ChallengeId ] |> ok |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox b "Agent 3" |> ok).Blocked.Length, Is.EqualTo(1))
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" second.ChallengeId
                second.ConditionIds.Head 1 "verified" "" |> ok |> ignore
            let ready = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptId))
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            Assert.That(notices a, Is.Empty)
        )


    [<Test>]
    member _.BlockingAnotherAssignmentWakesPreviouslyEligibleWork() =
        withFixture (fun a b ->
            let taskA = "eligible-after-release"
            let tvA, pvA, avA, attemptA = assignment a taskA
            let gateA =
                ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Previous gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Finish prior dependency" } ] |> ok
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" taskA attemptA
                tvA pvA avA 1 "Wait for prior gate" [ gateA.ChallengeId ] |> ok |> ignore
            let taskB = "blocking-the-current-assignment"
            let tvB, pvB, avB, attemptB = assignment b taskB
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" gateA.ChallengeId
                gateA.ConditionIds.Head 1 "finished" "" |> ok |> ignore
            let pending = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(pending.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptB))
            Assert.That(pending.BlockedChallengeWaits.Head.Status, Is.EqualTo("Eligible"))
            let gateB =
                ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "New gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "New unfinished dependency" } ] |> ok
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" taskB attemptB
                tvB pvB pending.Agent.Version 1 "Wait for new gate" [ gateB.ChallengeId ] |> ok |> ignore
            let awakened = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(awakened.Agent.State, Is.EqualTo("Ready"))
            Assert.That(awakened.Agent.CurrentAssignment.AttemptId, Is.EqualTo(attemptA))
            Assert.That(awakened.BlockedChallengeWaits.Length, Is.EqualTo(1))
            Assert.That(awakened.BlockedChallengeWaits.Head.TaskId, Is.EqualTo(taskB))
            Assert.That(awakened.BlockedChallengeWaits.Head.Status, Is.EqualTo("Waiting"))
            Assert.That(awakened.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(notices a, Is.Empty)
        )


    [<Test>]
    member _.ReblockingAnAttemptUsesNewGenerationWithoutReusingStaleIndex() =
        withFixture (fun a b ->
            let task = "reblocked-index-generation"
            let tv, pv, av, attemptId = assignment a task
            let first =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "First gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "First resolution" } ] |> ok
            ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                tv pv av 1 "First wait" [ first.ChallengeId ] |> ok |> ignore
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Lead" first.ChallengeId
                first.ConditionIds.Head 1 "done" "" |> ok |> ignore
            let _, _, queued = ReviewWorkflow.getTask b task |> ok
            Assert.That(queued.Value.AssignmentGeneration, Is.EqualTo(2))
            ReviewWorkflow.beginAttempt a (Guid.NewGuid()) task "Agent 3" queued.Value.Version 2
                |> ok |> ignore
            let _, nextTask, nextAttempt = ReviewWorkflow.getTask b task |> ok
            let working = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            let second =
                ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "Second gate" ""
                    [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Second resolution" } ] |> ok
            ChallengeWorkflow.blockAttempt a (Guid.NewGuid()) "Agent 3" task attemptId
                nextTask.Version nextAttempt.Value.Version working.Agent.Version 2
                "Second wait" [ second.ChallengeId ] |> ok |> ignore
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" second.ChallengeId
                second.ConditionIds.Head 1 "done again" "" |> ok |> ignore
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(3))
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            let snapshot = LedgerStore.fetch b |> ok
            let oldLinks =
                LedgerStore.listAt b snapshot ("challenge-waits/by-challenge/" + first.ChallengeId) |> ok
            let newLinks =
                LedgerStore.listAt b snapshot ("challenge-waits/by-challenge/" + second.ChallengeId) |> ok
            Assert.That(oldLinks.Length, Is.EqualTo(1))
            Assert.That(newLinks.Length, Is.EqualTo(1))
            Assert.That(notices b, Is.Empty)
        )


    [<Test>]
    member _.OperatorInputIsOneShotDurableEvidenceAndNeverAgentWork() =
        withFixture (fun a b ->
            let question = "Evaluate the harbor water effect"
            let answer = "Waves are still too large near the harbor wall."
            let spec = [ { OwnerKind = "Operator"; OwnerAgentId = ""
                           SuggestedAgentId = ""; Description = question } ]
            let created = ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3"
                            "Harbor feedback" "" spec |> ok
            let cid, condition = created.ChallengeId, created.ConditionIds.Head
            let pending = get b "Agent 3" cid
            Assert.That(pending.Challenge.Conditions.Head.OwnerKind, Is.EqualTo("Operator"))
            Assert.That(pending.Challenge.Conditions.Head.OwnerAgentId, Is.Empty)
            Assert.That((ChallengeWorkflow.listOperator b "Wayfold" |> ok).Head.Question, Is.EqualTo(question))
            let opView = ChallengeWorkflow.getOperatorPage b cid 16 0L "" |> ok
            Assert.That(opView.Challenge.ChallengeId, Is.EqualTo(cid))
            failDenied (ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 3" cid condition 1 "spoof" "")
            failDenied (ChallengeWorkflow.assignCondition b (Guid.NewGuid()) "Agent 3" cid condition 1 "Agent 8")
            failDenied (ChallengeWorkflow.splitCondition b (Guid.NewGuid()) "Agent 3" cid condition 1
                            [ { OwnerKind = ""; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "one" }
                              { OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "two" } ])
            ChallengeWorkflow.setSubscription b (Guid.NewGuid()) "Lead" true |> ok |> ignore
            let before = LedgerStore.fetch b |> ok
            let triage = ReviewWorkflow.getAgentInboxAt b before "Lead" |> ok
                         |> ChallengeWorkflow.enrichInboxAt b before "Lead" |> ok
            Assert.That(triage.ChallengeTriage, Is.Empty)
            let operation = Guid.NewGuid()
            let first = ChallengeWorkflow.respondOperator b operation "operator:authenticated-subject"
                            cid condition 1 answer |> ok
            Assert.That(first.AlreadyApplied, Is.False)
            let retry = ChallengeWorkflow.respondOperator a operation "operator:authenticated-subject"
                            cid condition 1 answer |> ok
            Assert.That(retry.AlreadyApplied, Is.True)
            let completed = get a "Agent 3" cid
            Assert.That(completed.Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(completed.Challenge.Version, Is.EqualTo(2))
            Assert.That(completed.Challenge.Conditions.Head.Evidence, Is.EqualTo(answer))
            Assert.That(completed.History.Length, Is.EqualTo(2))
            Assert.That(completed.History.Tail.Head.Action, Is.EqualTo("RespondOperatorCondition"))
            Assert.That(completed.History.Tail.Head.ActorId, Is.EqualTo("operator:authenticated-subject"))
            Assert.That(completed.History.Tail.Head.Evidence, Is.EqualTo(answer))
            let after = LedgerStore.fetch b |> ok
            let inbox = ReviewWorkflow.getAgentInboxAt b after "Agent 3" |> ok
                        |> ChallengeWorkflow.enrichInboxAt b after "Agent 3" |> ok
            Assert.That(inbox.ChallengeRecent.Head.EvidencePreview, Is.EqualTo(answer))
            Assert.That(inbox.ChallengeRecent.Head.ChallengeStatus, Is.EqualTo("Satisfied"))
            let operatorView = ChallengeWorkflow.listOperator b "Wayfold" |> ok
            Assert.That(operatorView.Head.ConditionStatus, Is.EqualTo("Satisfied"))
            Assert.That(operatorView.Head.Evidence, Is.EqualTo(answer))
            Assert.That(operatorView.Head.RespondedBy, Is.EqualTo("operator:authenticated-subject"))
            Assert.That(operatorView.Head.RespondedAtUtc, Is.EqualTo(completed.History.Tail.Head.OccurredAtUtc))
            Assert.That(operatorView.Head.ResponseEventSequence, Is.EqualTo(completed.History.Tail.Head.Sequence))
            let provenance = Client.OperatorChallengePresentation.provenanceLine operatorView.Head
            Assert.That(provenance.IsSome, Is.True)
            Assert.That(provenance.Value, Does.Contain("operator:authenticated-subject"))
            Assert.That(provenance.Value, Does.Contain(completed.History.Tail.Head.OccurredAtUtc))
            failStale (ChallengeWorkflow.respondOperator a (Guid.NewGuid()) "operator:other"
                           cid condition 1 "conflicting answer")
            failStale (ChallengeWorkflow.respondOperator b (Guid.NewGuid()) "operator:other"
                           cid condition 2 "second answer")
            Assert.That(notices a, Is.Empty)
        )

    [<Test>]
    member _.OperatorResponseCompletesOnlyItsConditionOfAllPolicyChallenge() =
        withFixture (fun a b ->
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Release review" ""
                    [ { OwnerKind = "Operator"; OwnerAgentId = ""; SuggestedAgentId = ""; Description = "Do you approve?" }
                      { OwnerKind = "Agent"; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""; Description = "Run tests" } ] |> ok
            let cid = created.ChallengeId
            ChallengeWorkflow.respondOperator b (Guid.NewGuid()) "operator:human" cid
                created.ConditionIds.Head 1 "Not yet, inspect water first." |> ok |> ignore
            let partial = get a "Agent 3" cid
            Assert.That(partial.Challenge.Status, Is.EqualTo("Pending"))
            Assert.That(partial.Challenge.Conditions.Head.Status, Is.EqualTo("Satisfied"))
            Assert.That(partial.Challenge.Conditions.Tail.Head.Status, Is.EqualTo("Pending"))
            Assert.That(notices b, Is.Empty)
            ChallengeWorkflow.resolve a (Guid.NewGuid()) "Agent 8" cid
                created.ConditionIds.Tail.Head 2 "tests pass" "" |> ok |> ignore
            Assert.That((get b "Agent 3" cid).Challenge.Status, Is.EqualTo("Satisfied"))
            Assert.That(notices a, Is.Empty)
        )

    [<Test>]
    member _.OperatorReplyWakesExplicitBlockedAttemptToReadyWithoutAnyYield() =
        withFixture (fun a b ->
            let task = "operator-water-feedback"
            let tv, pv, av, attemptId = assignment a task
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Water feedback" task
                    [ { OwnerKind = "Operator"; OwnerAgentId = ""; SuggestedAgentId = ""
                        Description = "Evaluate the harbor water effect" } ] |> ok
            ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                tv pv av 1 "Waiting for operator feedback" [ created.ChallengeId ] |> ok |> ignore
            let waiting = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(waiting.Agent.State, Is.EqualTo("NeedsWork"))
            ChallengeWorkflow.respondOperator b (Guid.NewGuid()) "operator:human" created.ChallengeId
                created.ConditionIds.Head 1 "Waves are still too large" |> ok |> ignore
            let ready = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.RunnableQueue.Head.Attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(ready.BlockedChallengeWaits, Is.Empty)
            Assert.That(notices a, Is.Empty)
            failStale (ChallengeWorkflow.blockAttempt b (Guid.NewGuid()) "Agent 3" task attemptId
                           tv pv av 1 "Too late" [ created.ChallengeId ])
        )

    [<Test>]
    member _.CancelledOperatorRequestRemainsInHistoryButIsNotActionableInClient() =
        withFixture (fun a b ->
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Cancelled operator request" ""
                    [ { OwnerKind = "Operator"; OwnerAgentId = ""; SuggestedAgentId = ""
                        Description = "Review the new layout" } ] |> ok
            let pending = ChallengeWorkflow.listOperator b "Jarvis" |> ok
            Assert.That(Client.OperatorChallengePresentation.pendingCount pending, Is.EqualTo(1))
            ChallengeWorkflow.cancel b (Guid.NewGuid()) "Agent 3"
                created.ChallengeId 1 "No longer required" |> ok |> ignore
            let cancelled = ChallengeWorkflow.listOperator a "Jarvis" |> ok
            Assert.That(cancelled.Length, Is.EqualTo(1), "Cancelled requests remain readable history.")
            Assert.That(cancelled.Head.ChallengeStatus, Is.EqualTo("Cancelled"))
            Assert.That(cancelled.Head.ConditionStatus, Is.EqualTo("Pending"),
                        "Historical condition status must not be rewritten for presentation.")
            Assert.That(Client.OperatorChallengePresentation.pendingCount cancelled, Is.Zero)
            Assert.That(Client.OperatorChallengePresentation.isActionable cancelled.Head, Is.False)
            failStale (ChallengeWorkflow.respondOperator a (Guid.NewGuid()) "operator:human"
                           created.ChallengeId created.ConditionIds.Head 2 "Too late")
            Assert.That(notices b, Is.Empty)
        )

    [<Test>]
    member _.UnrelatedConditionResolutionRebasesOperatorDraftOnVerifiedNewVersion() =
        withFixture (fun a b ->
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Mixed review" ""
                    [ { OwnerKind = "Operator"; OwnerAgentId = ""; SuggestedAgentId = ""
                        Description = "Provide one-shot opinion" }
                      { OwnerKind = "Agent"; OwnerAgentId = "Agent 8"; SuggestedAgentId = ""
                        Description = "Perform validation" } ] |> ok
            let original = (ChallengeWorkflow.listOperator b "Jarvis" |> ok).Head
            Assert.That(original.Version, Is.EqualTo(1))
            ChallengeWorkflow.resolve b (Guid.NewGuid()) "Agent 8" created.ChallengeId
                created.ConditionIds.Tail.Head 1 "validated" "" |> ok |> ignore
            let draft = "The UI layout is clear, but preserve scroll state."
            failStale (ChallengeWorkflow.respondOperator a (Guid.NewGuid()) "operator:human"
                           created.ChallengeId created.ConditionIds.Head original.Version draft)
            let fresh = (ChallengeWorkflow.listOperator a "Jarvis" |> ok).Head
            let rebase =
                Client.OperatorChallengePresentation.updatedActionableVersion original (Some fresh)
            Assert.That(rebase.IsSome, Is.True)
            Assert.That(rebase.Value.Version, Is.EqualTo(2))
            Assert.That(Client.OperatorChallengePresentation.updatedActionableVersion fresh (Some original),
                        Is.EqualTo(None))
            let priorOperation = Guid.NewGuid().ToString("D")
            let nextOperation = Guid.NewGuid().ToString("D")
            let typedDraft: Client.OperatorChallengeDraft =
                { Target = original; Text = draft; OperationId = priorOperation }
            let recovered =
                Client.OperatorChallengePresentation.rebaseAfterDefinitiveStale
                    typedDraft (Some fresh) nextOperation
            Assert.That(recovered.IsSome, Is.True)
            Assert.That(recovered.Value.Text, Is.EqualTo(draft))
            Assert.That(recovered.Value.OperationId, Is.EqualTo(nextOperation))
            Assert.That(recovered.Value.OperationId, Is.Not.EqualTo(priorOperation))
            Assert.That(recovered.Value.Target.Version, Is.EqualTo(2))
            Assert.That(Client.OperatorChallengePresentation.rebaseAfterDefinitiveStale
                            typedDraft (Some original) nextOperation,
                        Is.EqualTo(None), "No new canonical version means no new intent.")
            Assert.That(Client.OperatorChallengePresentation.rebaseAfterDefinitiveStale
                            typedDraft (Some fresh) priorOperation,
                        Is.EqualTo(None), "An idempotent retry must keep the prior operation ID.")
            let operation = Guid.NewGuid()
            ChallengeWorkflow.respondOperator b operation "operator:human"
                created.ChallengeId created.ConditionIds.Head rebase.Value.Version draft
                |> ok |> ignore
            let final = (ChallengeWorkflow.listOperator a "Jarvis" |> ok).Head
            Assert.That(final.ChallengeStatus, Is.EqualTo("Satisfied"))
            Assert.That(final.Evidence, Is.EqualTo(draft))
            Assert.That(final.RespondedBy, Is.EqualTo("operator:human"))
            Assert.That(final.ResponseEventSequence, Is.GreaterThan(0L))
            Assert.That(Client.OperatorChallengePresentation.updatedActionableVersion original (Some final),
                        Is.EqualTo(None), "An answered condition must not be re-armed by a stale draft.")
            Assert.That(Client.OperatorChallengePresentation.rebaseAfterDefinitiveStale
                            typedDraft (Some final) (Guid.NewGuid().ToString("D")),
                        Is.EqualTo(None), "Answered input cannot reuse the unsent draft.")
            Assert.That(notices a, Is.Empty)
        )

    [<Test>]
    member _.CancelledChallengeNeverCreatesAgentInboxWorkAndIssuerSeesOneTerminalOutcome() =
        withFixture (fun a b ->
            ChallengeWorkflow.setSubscription a (Guid.NewGuid()) "Lead" true |> ok |> ignore
            let created =
                ChallengeWorkflow.create a (Guid.NewGuid()) "Agent 3" "Withdrawn request" ""
                    [ { OwnerKind = "Agent"; OwnerAgentId = "Lead"
                        SuggestedAgentId = ""; Description = "Review code" }
                      { OwnerKind = "Unassigned"; OwnerAgentId = ""
                        SuggestedAgentId = ""; Description = "Run benchmark" } ] |> ok
            ChallengeWorkflow.cancel b (Guid.NewGuid()) "Agent 3"
                created.ChallengeId 1 "Cancelled by issuer" |> ok |> ignore
            let snapshot = LedgerStore.fetch a |> ok
            let assigned =
                ReviewWorkflow.getAgentInboxAt a snapshot "Lead" |> ok
                |> ChallengeWorkflow.enrichInboxAt a snapshot "Lead" |> ok
            Assert.That(assigned.ChallengeAssigned, Is.Empty)
            Assert.That(assigned.ChallengeTriage, Is.Empty)
            let issuer =
                ReviewWorkflow.getAgentInboxAt a snapshot "Agent 3" |> ok
                |> ChallengeWorkflow.enrichInboxAt a snapshot "Agent 3" |> ok
            Assert.That(issuer.ChallengeRecent.Length, Is.EqualTo(1))
            Assert.That(issuer.ChallengeRecent.Head.ChallengeStatus, Is.EqualTo("Cancelled"))
            Assert.That(notices b, Is.Empty)
        )
