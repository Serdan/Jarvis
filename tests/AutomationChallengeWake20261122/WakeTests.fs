module AutomationChallengeWake20261122.WakeTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.WorkQueueReactiveWake
open Client.AutomationChallengeWakeAdapter

[<TestFixture>]
type CanonicalChallengeWake() =
    let ok = function
        | Ok value -> value
        | Error issue -> failwithf "Real Git ledger rejected fixture: %A" issue

    let git cwd args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- cwd
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        for arg in args do p.StartInfo.ArgumentList.Add arg
        if not (p.Start()) then failwith "Git unavailable"
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode <> 0 then failwithf "Disposable git failed: %s" error
        output.Trim()

    let withClones action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-w17-wake-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare, a, b = Path.Combine(root, "remote.git"), Path.Combine(root, "a"), Path.Combine(root, "b")
            git root ["init"; "--bare"; bare] |> ignore
            git root ["clone"; bare; a] |> ignore
            git a ["config"; "user.name"; "Wake Fixture"] |> ignore
            git a ["config"; "user.email"; "wake@example.test"] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "disposable")
            git a ["add"; "source.txt"] |> ignore
            git a ["commit"; "-m"; "disposable baseline"] |> ignore
            git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; bare; b] |> ignore
            git b ["config"; "user.name"; "Wake Fixture"] |> ignore
            git b ["config"; "user.email"; "wake@example.test"] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"; ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare; ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> ok |> ignore
            for agent in ["Agent 3"; "Lead"; "Agent 8"; "Intruder"] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) agent |> ok |> ignore
            action first second
        finally
            Directory.Delete(root, true)

    let startBlocked (first:LedgerBinding) (second:LedgerBinding) =
        let task = "w17-canonical-wake"
        ReviewWorkflow.createTask first (Guid.NewGuid()) task task |> ok |> ignore
        let attempt = Guid.NewGuid().ToString("N")
        ReviewWorkflow.assignTask first (Guid.NewGuid()) task (Guid.Parse attempt) "Agent 3" |> ok |> ignore
        ReviewWorkflow.beginAttempt first (Guid.NewGuid()) task "Agent 3" 1 1 |> ok |> ignore
        let _, taskRecord, maybeAttempt = ReviewWorkflow.getTask first task |> ok
        let agent = (ReviewWorkflow.getAgentInbox first "Agent 3" |> ok).Agent
        let challenge = ChallengeWorkflow.create first (Guid.NewGuid()) "Agent 3" "Wake test" task
                            [{ OwnerKind = ""; OwnerAgentId = "Lead"; SuggestedAgentId = ""; Description = "Unblock only on evidence" }] |> ok
        ChallengeWorkflow.blockAttempt second (Guid.NewGuid()) "Agent 3" task attempt
            taskRecord.Version maybeAttempt.Value.Version agent.Version 1 "Awaiting canonical challenge"
            [challenge.ChallengeId] |> ok |> ignore
        task, attempt, challenge

    let candidate (binding:LedgerBinding) commit operation (challenge:ChallengeCreated) task attempt : ChallengeWakeCandidate =
        { ProjectId = binding.ProjectId; LedgerId = binding.LedgerId; Epoch = binding.Epoch
          Commit = commit; OperationId = operation
          ChallengeId = challenge.ChallengeId; ConditionId = challenge.ConditionIds.Head
          TaskId = task; AttemptId = attempt; OwnerAgentId = "Agent 3"; BlockedGeneration = 1 }

    let expectHold expected = function
        | WakeDecision.Hold code when code = expected -> ()
        | other -> Assert.Fail(sprintf "Expected HOLD %s; got %A" expected other)

    [<Test>]
    member _.DefaultOffDoesNotReadOrSignalAnyGitOrScheduler() =
        let unbound : LedgerBinding = Unchecked.defaultof<_>
        let unboundCandidate : ChallengeWakeCandidate = Unchecked.defaultof<_>
        Assert.That(inspect false unbound unboundCandidate, Is.EqualTo(WakeDecision.Off))

    [<Test>]
    member _.ActualBlockResolveReverseIndexAndReadyOnTwoClones() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let previous = LedgerStore.fetch b |> ok
            let operationId = Guid.NewGuid()
            let resolved = ChallengeWorkflow.resolve a operationId "Lead" challenge.ChallengeId
                               challenge.ConditionIds.Head 1 "signed:disposable" "" |> ok
            let c = candidate a resolved.CurrentHead operationId challenge task attempt
            Assert.That(inspect true a c, Is.EqualTo(WakeDecision.Ready(resolved.CurrentHead, 2)))
            Assert.That(inspect true b c, Is.EqualTo(WakeDecision.Ready(resolved.CurrentHead, 2)))
            let inbox = ReviewWorkflow.getAgentInbox b "Agent 3" |> ok
            Assert.That(inbox.Agent.State, Is.EqualTo("Ready"))
            Assert.That(inbox.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(inbox.RunnableQueue.Head.Attempt.IssuedSequence, Is.EqualTo(resolved.Operation.Sequence))
            let current = LedgerStore.fetch b |> ok
            Assert.That(current.Commit, Is.Not.EqualTo(previous.Commit))
            let repeat = ChallengeWorkflow.resolve b operationId "Lead" challenge.ChallengeId
                             challenge.ConditionIds.Head 1 "signed:disposable" "" |> ok
            Assert.That(repeat.AlreadyApplied, Is.True)
            Assert.That((LedgerStore.fetch a |> ok).Commit, Is.EqualTo(current.Commit))
        )

    [<Test>]
    member _.UnknownUnresolvedDependencyNeverSignals() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let c = candidate a (LedgerStore.fetch b |> ok).Commit (Guid.NewGuid()) challenge task attempt
            match inspect true b c with
            | WakeDecision.Hold _ -> ()
            | other -> Assert.Fail(sprintf "Unresolved challenge treated as wake: %A" other)
            let blocked = ReviewWorkflow.getAgentInbox a "Agent 3" |> ok
            Assert.That(blocked.Agent.State, Is.EqualTo("NeedsWork"))
        )

    [<Test>]
    member _.ForeignProjectLedgerAndGenerationFailClosed() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let op = Guid.NewGuid()
            let resolved = ChallengeWorkflow.resolve a op "Lead" challenge.ChallengeId
                               challenge.ConditionIds.Head 1 "approved" "" |> ok
            let c = candidate a resolved.CurrentHead op challenge task attempt
            expectHold "ForeignProject" (inspect true b { c with ProjectId = Guid.NewGuid() })
            expectHold "ForeignProject" (inspect true b { c with LedgerId = Guid.NewGuid() })
            expectHold "StaleGeneration" (inspect true b { c with BlockedGeneration = 2 })
            expectHold "ResolutionMismatch" (inspect true b { c with OperationId = Guid.NewGuid() })
            expectHold "ResolutionMismatch" (inspect true b { c with ConditionId = Guid.NewGuid().ToString("N") })
            expectHold "ResolutionMismatch" (inspect true b { c with OwnerAgentId = "Intruder" })
        )

    [<Test>]
    member _.OldHeadAndDelayedHintAreNotDispatchAuthority() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let op = Guid.NewGuid()
            let resolved = ChallengeWorkflow.resolve a op "Lead" challenge.ChallengeId
                               challenge.ConditionIds.Head 1 "approved" "" |> ok
            let c = candidate a resolved.CurrentHead op challenge task attempt
            let other = ChallengeWorkflow.create b (Guid.NewGuid()) "Agent 3" "unrelated" ""
                            [{ OwnerKind=""; OwnerAgentId="Agent 8"; SuggestedAgentId=""; Description="unrelated" }] |> ok
            Assert.That(other.ChallengeId, Is.Not.Empty)
            expectHold "StaleHead" (inspect true a c)
            expectHold "StaleHead" (inspect true b c)
        )

    [<Test>]
    member _.VerifiedHintStillUsesRealDefaultOffCoordinator() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let op = Guid.NewGuid()
            let resolved = ChallengeWorkflow.resolve a op "Lead" challenge.ChallengeId
                               challenge.ConditionIds.Head 1 "approved" "" |> ok
            let c = candidate a resolved.CurrentHead op challenge task attempt
            let mutable reads = 0
            let mutable executions = 0
            let ports : WorkWakePorts =
                { ReadVerified = fun () -> reads <- reads + 1; failwith "no approved account/role plan"
                  Plan = fun _ -> failwith "not authorized"
                  Execute = fun _ -> executions <- executions + 1; WorkExecutorResult.Applied }
            let coordinator = WorkQueueReactiveWake.Coordinator(ports)
            Assert.That(deliver true b c coordinator, Is.EqualTo(WakeDelivery.Emitted WorkWakeResult.Disabled))
            Assert.That(reads, Is.Zero)
            Assert.That(executions, Is.Zero)
        )

    [<Test>]
    member _.CompetingCASLoserAndForgedOperationDoNotWake() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let winner, loser = Guid.NewGuid(), Guid.NewGuid()
            let accepted =
                ChallengeWorkflow.resolve a winner "Lead" challenge.ChallengeId
                    challenge.ConditionIds.Head 1 "accepted" "" |> ok
            match ChallengeWorkflow.resolve b loser "Lead" challenge.ChallengeId
                    challenge.ConditionIds.Head 1 "loser" "" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Competing CAS must lose: %A" other)
            let currentHead = (LedgerStore.fetch b |> ok).Commit
            Assert.That(currentHead, Is.EqualTo(accepted.CurrentHead))
            let loserCandidate = candidate b currentHead loser challenge task attempt
            expectHold "ResolutionMismatch" (inspect true a loserCandidate)
            let winnerCandidate = { loserCandidate with OperationId = winner }
            Assert.That(inspect true b winnerCandidate, Is.EqualTo(WakeDecision.Ready(currentHead, 2)))
        )

    [<Test>]
    member _.ExternallyDeletedReverseIndexFailsClosedEvenAtCurrentGitHead() =
        withClones (fun a b ->
            let task, attempt, challenge = startBlocked a b
            let winner = Guid.NewGuid()
            let accepted = ChallengeWorkflow.resolve a winner "Lead" challenge.ChallengeId
                               challenge.ConditionIds.Head 1 "accepted" "" |> ok
            let good = candidate a accepted.CurrentHead winner challenge task attempt
            Assert.That(inspect true b good, Is.EqualTo(WakeDecision.Ready(accepted.CurrentHead, 2)))
            // External fixture tamper: no application API is allowed to remove
            // an issued reverse edge. The read-side must detect this poison.
            let checkout = Path.Combine(Path.GetDirectoryName(b.RepositoryPath),
                                        "poison-" + Guid.NewGuid().ToString("N"))
            let snapshot = LedgerStore.fetch b |> ok
            git b.RepositoryPath ["worktree"; "add"; "--detach"; checkout; snapshot.Commit] |> ignore
            try
                git checkout ["config"; "user.name"; "Wake Mutant"] |> ignore
                git checkout ["config"; "user.email"; "mutant@example.test"] |> ignore
                let edge = sprintf "challenge-waits/by-challenge/%s/%s/%s/1.json"
                               challenge.ChallengeId task attempt
                git checkout ["rm"; "--"; edge] |> ignore
                git checkout ["commit"; "-m"; "untrusted external reverse-index deletion"] |> ignore
                git checkout ["push"; "origin"; "HEAD:refs/heads/jarvis-orchestration"] |> ignore
            finally
                git b.RepositoryPath ["worktree"; "remove"; "--force"; checkout] |> ignore
            let altered = (LedgerStore.fetch a |> ok).Commit
            Assert.That(altered, Is.Not.EqualTo(accepted.CurrentHead))
            expectHold "IndexMismatch" (inspect true a { good with Commit = altered })
        )
