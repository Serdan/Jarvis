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
