module Client.Tests.WorkQueueConversationTransportTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueConversationTransport

[<TestFixture>]
type WorkQueueConversationTransportTests() =
    let success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected WQ-06 error: %A" error

    let git dir args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- dir
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        for arg in args do runner.StartInfo.ArgumentList.Add arg
        Assert.That(runner.Start(), Is.True)
        let output = runner.StandardOutput.ReadToEnd()
        let error = runner.StandardError.ReadToEnd()
        runner.WaitForExit()
        Assert.That(runner.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withClones work =
        let dir = Path.Combine(Path.GetTempPath(), "jarvis-wq06-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            let bare = Path.Combine(dir, "remote.git")
            let first = Path.Combine(dir, "first")
            let second = Path.Combine(dir, "second")
            git dir [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
            git dir [ "clone"; bare; first ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "disposable")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                        "commit"; "-m"; "fixture" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git dir [ "clone"; bare; second ] |> ignore
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let binding repo =
                { RepositoryPath = repo
                  RemoteName = "origin"
                  ApprovedFetchUrl = bare
                  ApprovedPushUrl = bare
                  ProjectId = project
                  LedgerId = ledger
                  Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            work a b
        finally
            try Directory.Delete(dir, true) with _ -> ()

    let workStage project id taskId kind queue =
        { Id = id; ProjectId = project
          SubjectId = taskId; Kind = kind; QueueId = queue
          StageGeneration = 1; State = WorkState.Available
          Domain = DomainState.Undecided; Gate = GateState.Clear
          ClaimGeneration = 0; Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 0L; AvailableSequence = 0L
          SourceLineage = None; Push = None; IntegrationAuthorized = false }

    let launch project id role =
        { Id = id; ProjectId = project
          RoleId = role; RoleVersion = 1
          AccountTokenId = "token-" + id
          State = LaunchState.ReservedUnregistered
          Lease = None; Version = 0; Credit = None
          PrimaryWorkId = None; EverClaimed = false
          AncillaryMessages = Set.empty; DispatchCount = 0 }

    let message id subject body =
        { Id = id; Subject = subject; Body = body
          State = MessageState.Pending; HandlerGeneration = 0
          WorkId = None }

    let version binding =
        let _, state = WorkQueueGitLedger.load binding |> success
        state.Version

    let mutate binding now mutation =
        WorkQueueGitLedger.executeAt binding "Fixture" (Guid.NewGuid())
            (version binding) now mutation |> success |> ignore

    let registeredPrimary binding launchId =
        mutate binding 1010L (DispatchOnce(launchId, DispatchState.Submitted))
        mutate binding 1011L (InitializeAgent(launchId, "session-" + launchId))
        mutate binding 1012L (ClaimNext launchId)

    [<Test>]
    member _.TaskReadDoesNotClaimAndTwoCloneAncillaryReplyIsDurable() =
        withClones (fun a b ->
            let pid = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning"; "role:Lead" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage(workStage pid "P" "task-1" WorkKind.Planning "planning"))
            let ask = message "m-task" (Subject.Task "task-1") "Can we continue?"
            let posted =
                WorkQueueConversationTransport.post a "Operator" (Guid.NewGuid())
                    (version a) { Message = ask; RoleWorkFromProducer = None }
                |> success
            let readOnly = WorkQueueConversationTransport.readThread b (Subject.Task "task-1") |> success
            Assert.That(readOnly.PendingCount, Is.EqualTo(1))
            Assert.That(readOnly.Entries.Head.Reply.IsNone, Is.True)
            Assert.That(readOnly.ProjectVersion, Is.EqualTo(2))
            Assert.That(posted.Operation.Type, Is.EqualTo("WQ02.Mutate"))
            let role = message "m-role" (Subject.Role "Lead") "Please triage."
            let roleWork = workStage pid "role-work" "Lead" (WorkKind.RoleMessage "m-role") "role:Lead"
            WorkQueueConversationTransport.post b "Operator" (Guid.NewGuid()) (version b)
                { Message = role; RoleWorkFromProducer = Some roleWork }
            |> success |> ignore
            mutate a 1003L (AdmitProject(launch pid "L" "Lead"))
            registeredPrimary a "L"
            let _, primary = WorkQueueGitLedger.load b |> success
            Assert.That(primary.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "P"))
            let staleVersion = primary.Version
            let _, lease =
                WorkQueueConversationTransport.claimAncillary a "Lead" (Guid.NewGuid())
                    staleVersion "L" "m-task" |> success
            match WorkQueueConversationTransport.claimAncillary b "Lead" (Guid.NewGuid())
                    staleVersion "L" "m-task" with
            | Error(StaleEntityVersion _) -> ()
            | unexpected -> Assert.Fail(sprintf "Competing clone took claimed Task message: %A" unexpected)
            Assert.That(lease.HandlerGeneration, Is.GreaterThan(0))
            let prepareOp = Guid.NewGuid()
            let prepared =
                WorkQueueConversationTransport.prepareReply b "Lead" prepareOp lease
                    "reply-task" "The checkpoint is available."
                |> success
            let prepareReplay =
                WorkQueueConversationTransport.prepareReply a "Lead" prepareOp lease
                    "reply-task" "The checkpoint is available."
                |> success
            Assert.That(prepareReplay.Operation.OriginalResultHash,
                        Is.EqualTo(prepared.Operation.OriginalResultHash))
            Assert.That(prepareReplay.AlreadyApplied, Is.True)
            let beforeAck = WorkQueueConversationTransport.readThread a (Subject.Task "task-1") |> success
            Assert.That(beforeAck.Entries.Head.Message.State,
                        Is.EqualTo(MessageState.ClaimedAncillary "L"))
            Assert.That(beforeAck.Entries.Head.Reply.IsNone, Is.True,
                        "Precommitted content must not be visible before canonical Handled CAS")
            let expectedVersion = version a
            let ackId = Guid.NewGuid()
            let completion =
                WorkQueueConversationTransport.completePrepared a "Lead" ackId
                    expectedVersion lease "reply-task" |> success
            let completionReplay =
                WorkQueueConversationTransport.completePrepared b "Lead" ackId
                    expectedVersion lease "reply-task" |> success
            Assert.That(completionReplay.Operation.OriginalResultHash,
                        Is.EqualTo(completion.Operation.OriginalResultHash))
            let afterAck = WorkQueueConversationTransport.readThread b (Subject.Task "task-1") |> success
            Assert.That(afterAck.PendingCount, Is.EqualTo(0))
            Assert.That(afterAck.Entries.Head.Message.State,
                        Is.EqualTo(MessageState.Handled "reply-task"))
            Assert.That(afterAck.Entries.Head.Reply.Value.Body, Is.EqualTo("The checkpoint is available."))
            let _, preserved = WorkQueueGitLedger.load a |> success
            Assert.That(preserved.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L"))
            Assert.That(preserved.Launches.["L"].State.ToString().StartsWith("Registered"), Is.True)
            match WorkQueueConversationTransport.completePrepared a "Lead" (Guid.NewGuid())
                    (version a) lease "reply-task" with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Stale new acknowledgement accepted: %A" unexpected))

    [<Test>]
    member _.PrimaryRoleMessageCompletesSameLaunchOnlyAfterPreparedReceipt() =
        withClones (fun a b ->
            let pid = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "role:Lead" ] ])
            |> success |> ignore
            let role = message "role-1" (Subject.Role "Lead") "Status of the project?"
            let work = workStage pid "role-work-1" "Lead" (WorkKind.RoleMessage "role-1") "role:Lead"
            WorkQueueConversationTransport.post a "Operator" (Guid.NewGuid()) 0
                { Message = role; RoleWorkFromProducer = Some work }
            |> success |> ignore
            let beforeAdmission = WorkQueueConversationTransport.readThread b (Subject.Role "Lead") |> success
            Assert.That(beforeAdmission.PendingCount, Is.EqualTo(1))
            mutate b 1010L (AdmitProject(launch pid "L1" "Lead"))
            registeredPrimary b "L1"
            let _, state = WorkQueueGitLedger.load a |> success
            let lease = WorkQueueConversationTransport.primaryMessageClaim state "L1"
                        |> Option.defaultWith (fun () -> failwith "Missing primary Role claim")
            Assert.That(lease.WorkId, Is.EqualTo(Some "role-work-1"))
            match WorkQueueConversationTransport.completePrepared a "Lead" (Guid.NewGuid())
                    state.Version lease "reply-role" with
            | Error(LedgerIntegrityError _) -> ()
            | unexpected -> Assert.Fail(sprintf "Unprepared reply handled: %A" unexpected)
            WorkQueueConversationTransport.prepareReply a "Lead" (Guid.NewGuid()) lease
                "reply-role" "Queued; no Scheduler activated." |> success |> ignore
            let completion =
                WorkQueueConversationTransport.completePrepared b "Lead" (Guid.NewGuid())
                    (version b) lease "reply-role" |> success
            Assert.That(completion.AlreadyApplied, Is.False)
            let _, completed = WorkQueueGitLedger.load a |> success
            Assert.That(completed.Works.["role-work-1"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(completed.Launches.["L1"].State,
                        Is.EqualTo(LaunchState.TerminalPendingRelease(
                            "terminal:" + completion.Operation.OperationId)))
            let thread = WorkQueueConversationTransport.readThread b (Subject.Role "Lead") |> success
            Assert.That(thread.Entries.Head.Reply.Value.Body,
                        Is.EqualTo("Queued; no Scheduler activated.")))

    [<Test>]
    member _.StoppedHandlerAndCoverageDefectsFailClosed() =
        withClones (fun a b ->
            let pid = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage(workStage pid "Plan" "T" WorkKind.Planning "planning"))
            WorkQueueConversationTransport.post a "Operator" (Guid.NewGuid()) (version a)
                { Message = message "M" (Subject.Task "T") "Is this safe?"
                  RoleWorkFromProducer = None } |> success |> ignore
            mutate a 1002L (AdmitProject(launch pid "StopL" "Lead"))
            registeredPrimary a "StopL"
            let _, current = WorkQueueGitLedger.load b |> success
            let _, lease = WorkQueueConversationTransport.claimAncillary b "Lead" (Guid.NewGuid())
                                current.Version "StopL" "M" |> success
            let _, current = WorkQueueGitLedger.load a |> success
            let generation = current.Launches.["StopL"].Lease.Value.Generation
            mutate a 4000L (StopStalled("StopL", generation, 1000L))
            match WorkQueueConversationTransport.prepareReply b "Lead" (Guid.NewGuid()) lease
                    "unsafe-reply" "Must not commit" with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "STOP-requested handler prepared a reply: %A" unexpected)
            let _, covered = WorkQueueGitLedger.load a |> success
            // Test every independent coverage proof; no synthetic true assertion
            // can turn an incomplete projection into an actionable conversation.
            for broken in
                [ { covered with QueueCovered = false }
                  { covered with GraphCovered = false }
                  { covered with MessageCovered = false }
                  { covered with SourceCovered = false } ] do
                match Contracts.orderAvailable broken with
                | Error(Rejection.IncompleteCoverage _) -> ()
                | unexpected -> Assert.Fail(sprintf "Incomplete index became runnable: %A" unexpected)
            let pending = WorkQueueConversationTransport.readThread b (Subject.Task "T") |> success
            Assert.That(pending.Entries.Head.Message.State,
                        Is.EqualTo(MessageState.ClaimedAncillary "StopL"))
            Assert.That(pending.Entries.Head.Reply.IsNone, Is.True))

    
    [<Test>]
    member _.GatedAncillaryReleaseFencesPreparedReplyAcrossSameLaunchReclaim() =
        withClones (fun a b ->
            let pid = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning"; "role:Lead" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage(workStage pid "P" "task-P" WorkKind.Planning "planning"))
            mutate b 1001L (PublishStage(workStage pid "B" "task-B" WorkKind.Planning "planning"))
            let m = message "M" (Subject.Role "Lead") "Is the blocked stage ready?"
            let w = workStage pid "W-M" "Lead" (WorkKind.RoleMessage "M") "role:Lead"
            WorkQueueConversationTransport.post a "Operator" (Guid.NewGuid()) (version a)
                { Message = m; RoleWorkFromProducer = Some w } |> success |> ignore
            mutate a 1004L (AdmitProject(launch pid "L1" "Lead"))
            registeredPrimary a "L1"
            let _, first = WorkQueueGitLedger.load b |> success
            Assert.That(first.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            let originalClaimOperation = Guid.NewGuid()
            let _, g1 = WorkQueueConversationTransport.claimAncillary b "Lead" originalClaimOperation
                            first.Version "L1" "M" |> success
            mutate b 1020L (PublishStage(
                { workStage pid "D" "task-D" WorkKind.Planning "planning" with
                    Dependencies = set [ "W-M" ] }))
            Assert.That(g1.HandlerGeneration, Is.EqualTo(1))
            WorkQueueConversationTransport.prepareReply a "Lead" (Guid.NewGuid()) g1
                "reply-old" "The old reply must not escape." |> success |> ignore
            match WorkQueueConversationTransport.prepareReply b "Lead" (Guid.NewGuid()) g1
                    "reply-old" "Different content for the same reply ID" with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Immutable reply ID overwritten: %A" unexpected)
            mutate a 2000L (SetHardPrerequisite("W-M", "B"))
            let _, gated = WorkQueueGitLedger.load b |> success
            let work = gated.Works.["W-M"]
            let graph =
                match work.Gate with
                | GateState.DependencyGatePending version -> version
                | other -> failwithf "Expected gated ancillary handler, got %A" other
            match WorkQueueConversationTransport.completePrepared b "Lead" (Guid.NewGuid())
                    gated.Version g1 "reply-old" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Gated reply completed incorrectly: %A" other)
            WorkQueueConversationTransport.resolveGatedAncillary b "Lead" (Guid.NewGuid())
                gated.Version "L1" "M" "W-M" gated.Launches.["L1"].Version
                work.ClaimGeneration graph
            |> success |> ignore
            let _, released = WorkQueueGitLedger.load a |> success
            Assert.That(released.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
            Assert.That(released.Works.["W-M"].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(released.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L1"))
            Assert.That(released.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            mutate a 2001L (AdmitProject(launch pid "L2" "Lead"))
            registeredPrimary b "L2"
            let _, second = WorkQueueGitLedger.load a |> success
            Assert.That(second.Launches.["L2"].PrimaryWorkId, Is.EqualTo(Some "B"))
            mutate a 2010L (CompleteOrdinary("L2", "B"))
            let _, unblocked = WorkQueueGitLedger.load a |> success
            Assert.That(unblocked.Works.["W-M"].State, Is.EqualTo(WorkState.Available))
            let _, g3 = WorkQueueConversationTransport.claimAncillary a "Lead" (Guid.NewGuid())
                            unblocked.Version "L1" "M" |> success
            Assert.That(g3.HandlerGeneration, Is.GreaterThan(g1.HandlerGeneration))
            // Lost acknowledgement replay must recover the original immutable
            // g1 snapshot, not misidentify this *same launch's* later g3 claim.
            let _, historicalG1 =
                WorkQueueConversationTransport.claimAncillary b "Lead" originalClaimOperation
                    first.Version "L1" "M" |> success
            Assert.That(historicalG1, Is.EqualTo(g1))
            match WorkQueueConversationTransport.completePrepared a "Lead" (Guid.NewGuid())
                    (version a) g1 "reply-old" with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Old same-launch handler was accepted: %A" unexpected)
            WorkQueueConversationTransport.prepareReply b "Lead" (Guid.NewGuid()) g3
                "reply-new" "The prerequisite is now satisfied." |> success |> ignore
            WorkQueueConversationTransport.completePrepared b "Lead" (Guid.NewGuid())
                (version b) g3 "reply-new" |> success |> ignore
            let thread = WorkQueueConversationTransport.readThread a (Subject.Role "Lead") |> success
            Assert.That(thread.Entries.Head.Message.State,
                        Is.EqualTo(MessageState.Handled "reply-new"))
            Assert.That(thread.Entries.Head.Reply.Value.Body,
                        Is.EqualTo("The prerequisite is now satisfied."))
            let _, final = WorkQueueGitLedger.load b |> success
            Assert.That(final.Works.["W-M"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(final.Works.["D"].State, Is.EqualTo(WorkState.Available))
            Assert.That(final.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L1")))
