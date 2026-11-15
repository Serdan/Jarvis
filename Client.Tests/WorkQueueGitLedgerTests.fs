module Client.Tests.WorkQueueGitLedgerTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger

[<TestFixture>]
type WorkQueueGitLedgerTests() =
    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        for argument in args do runner.StartInfo.ArgumentList.Add(argument)
        Assert.That(runner.Start(), Is.True)
        let output = runner.StandardOutput.ReadToEnd()
        let error = runner.StandardError.ReadToEnd()
        runner.WaitForExit()
        Assert.That(runner.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let success = function
        | Ok result -> result
        | Error failure -> failwithf "WQ-02 Git CAS failed: %A" failure

    let withClones work =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq02-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; first ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "test only")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "source" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; second ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            work a b
        finally
            try Directory.Delete(root, true) with _ -> ()

    let stage projectId id dependencies =
        { Id = id; ProjectId = projectId
          SubjectId = "task-" + id
          Kind = WorkKind.FrozenReview
          QueueId = "review"
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = Set.ofList dependencies
          WaivedDependencies = Map.empty
          AvailableAt = 1000L
          AvailableSequence = 1L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }

    let plan projectId id dependencies =
        { stage projectId id dependencies with Kind = WorkKind.Planning; QueueId = "planning" }

    let launch projectId id role =
        { Id = id; ProjectId = projectId; RoleId = role; RoleVersion = 1
          AccountTokenId = "account-token-" + id
          State = LaunchState.ReservedUnregistered
          Lease = None
          Version = 0
          Credit = None
          PrimaryWorkId = None
          EverClaimed = false
          AncillaryMessages = Set.empty
          DispatchCount = 0 }

    let roleMessage projectId id role =
        let work =
            { stage projectId ("message-work-" + id) [] with
                Kind = WorkKind.RoleMessage id
                SubjectId = role
                QueueId = "role:" + role }
        let message =
            { Id = id; Subject = Subject.Role role; Body = id
              State = MessageState.Pending
              HandlerGeneration = 0; WorkId = None }
        message, work


    [<Test>]
    member _.CrossCloneStaleWriterAndOriginalReplayReceipt() =
        withClones (fun a b ->
            let init = WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) (Map.ofList [ "Reviewer", set [ "review" ] ])
                       |> success
            Assert.That(init.AlreadyApplied, Is.False)
            let firstStage = stage (a.ProjectId.ToString("D")) "review-A" []
            let secondStage = stage (a.ProjectId.ToString("D")) "review-B" []
            let operationId = Guid.NewGuid()
            let first =
                WorkQueueGitLedger.executeAt a "Worker" operationId 0 2000L (PublishStage firstStage)
                |> success
            Assert.That(first.AlreadyApplied, Is.False)
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 0 2001L (PublishStage secondStage) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "A stale clone must fail CAS: %A" other)
            let replay =
                WorkQueueGitLedger.executeAt b "Worker" operationId 0 2000L (PublishStage firstStage)
                |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(first.Operation.OriginalResultHash))
            let _, verified = WorkQueueGitLedger.load b |> success
            Assert.That(verified.Works.Count, Is.EqualTo(1))
            Assert.That(verified.Works.ContainsKey "review-A", Is.True)
            Assert.That(verified.Works.ContainsKey "review-B", Is.False))

    [<Test>]
    member _.OperationIdReusedWithChangedNestedPrerequisiteIsRejected() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) Map.empty |> success |> ignore
            let op = Guid.NewGuid()
            let original = stage (a.ProjectId.ToString("D")) "stage" []
            WorkQueueGitLedger.executeAt a "Worker" op 0 2000L (PublishStage original)
            |> success |> ignore
            let changed = { original with Dependencies = Set.singleton "other" }
            match WorkQueueGitLedger.executeAt b "Worker" op 0 2000L (PublishStage changed) with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Mutation payload replay must reject: %A" other)
            let _, state = WorkQueueGitLedger.load b |> success
            Assert.That(state.Works.["stage"].Dependencies, Is.Empty))

    [<Test>]
    member _.BlockedStageRemainsUnclaimableUnderVerifiedGitSnapshot() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) (Map.ofList [ "Reviewer", set [ "review" ] ])
            |> success |> ignore
            let projectId = a.ProjectId.ToString("D")
            let predecessor = stage projectId "B" []
            let dependent = { stage projectId "D" [ "B" ] with AvailableSequence = 2L }
            WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) 0 1000L (PublishStage predecessor)
            |> success |> ignore
            WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 1 1001L (PublishStage dependent)
            |> success |> ignore
            let _, verified = WorkQueueGitLedger.load a |> success
            Assert.That(verified.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            let ranked = Contracts.orderAvailable verified |> success
            Assert.That((ranked |> List.map (fun item -> item.Id)) = [ "B" ], Is.True))


    [<Test>]
    member _.TwoCloneRegistrationClaimTerminalAndDependencyWake() =
        withClones (fun a b ->
            let write client version time mutation =
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) version time mutation
                |> success |> ignore
            let subscriptions = Map.ofList [ "Lead", set [ "planning" ] ]
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) subscriptions |> success |> ignore
            let id = a.ProjectId.ToString("D")
            write a 0 1000L (PublishStage (plan id "B" []))
            write b 1 1001L (PublishStage (plan id "D" [ "B" ]))
            write a 2 1002L (AdmitProject (launch id "L" "Lead"))
            write a 3 1003L (DispatchOnce ("L", DispatchState.Submitted))
            let register = Guid.NewGuid()
            let registered =
                WorkQueueGitLedger.executeAt a "Worker" register 4 1004L (InitializeAgent ("L", "session-1"))
                |> success
            let _, afterRegistration = WorkQueueGitLedger.load b |> success
            let uuid =
                match afterRegistration.Launches.["L"].State with
                | LaunchState.Registered(uuid, "session-1") -> uuid
                | unexpected -> failwithf "First connect must be UUID-bound: %A" unexpected
            Assert.That(Guid.TryParse(uuid) |> fst, Is.True)
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 5 1005L
                    (InitializeAgent ("L", "distinct-session")) with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Distinct session must fail registration: %A" unexpected)
            let registeredReplay =
                WorkQueueGitLedger.executeAt b "Worker" register 4 1004L (InitializeAgent ("L", "session-1"))
                |> success
            Assert.That(registeredReplay.Operation.OriginalResultHash, Is.EqualTo(registered.Operation.OriginalResultHash))
            let claimOp = Guid.NewGuid()
            let claim =
                WorkQueueGitLedger.executeAt a "Worker" claimOp 5 1006L (ClaimNext "L")
                |> success
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 5 1006L (ClaimNext "L") with
            | Error(StaleEntityVersion _) -> ()
            | unexpected -> Assert.Fail(sprintf "Second primary claim must fail project CAS: %A" unexpected)
            let replay =
                WorkQueueGitLedger.executeAt b "Worker" claimOp 5 1006L (ClaimNext "L")
                |> success
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(claim.Operation.OriginalResultHash))
            let _, claimed = WorkQueueGitLedger.load b |> success
            Assert.That(claimed.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "B"))
            Assert.That(claimed.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            let previousSequence = claimed.NextAvailability
            let completeOp = Guid.NewGuid()
            let completed =
                WorkQueueGitLedger.executeAt b "Worker" completeOp 6 4000L (CompleteOrdinary ("L", "B"))
                |> success
            let _, updated = WorkQueueGitLedger.load a |> success
            Assert.That(updated.Works.["B"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(updated.Works.["D"].State, Is.EqualTo(WorkState.Available))
            Assert.That(updated.Works.["D"].AvailableAt, Is.EqualTo(4000L))
            Assert.That(updated.Works.["D"].AvailableSequence, Is.GreaterThan(previousSequence))
            Assert.That(updated.Launches.["L"].State, Is.EqualTo(LaunchState.TerminalPendingRelease("terminal:" + completeOp.ToString("D"))))
            let completionReplay =
                WorkQueueGitLedger.executeAt a "Worker" completeOp 6 4000L (CompleteOrdinary ("L", "B"))
                |> success
            Assert.That(completionReplay.Operation.OriginalResultHash, Is.EqualTo(completed.Operation.OriginalResultHash)))

    [<Test>]
    member _.DedicatedRolePrimaryRejectsAncillaryAndStaleMessageHandlerGeneration() =
        withClones (fun a b ->
            let write client version time mutation =
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) version time mutation
                |> success |> ignore
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "R", set [ "role:R" ] ]) |> success |> ignore
            let id = a.ProjectId.ToString("D")
            let m1, w1 = roleMessage id "M1" "R"
            let m2, w2 = roleMessage id "M2" "R"
            write a 0 1000L (PostMessage(m1, Some w1))
            write b 1 1001L (PostMessage(m2, Some { w2 with AvailableSequence = 2L }))
            write a 2 1002L (AdmitProject(launch id "L" "R"))
            write a 3 1003L (DispatchOnce("L", DispatchState.Submitted))
            write b 4 1004L (InitializeAgent("L", "key"))
            write b 5 1005L (ClaimNext "L")
            let _, snapshot = WorkQueueGitLedger.load a |> success
            Assert.That(snapshot.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some w1.Id))
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 6 1006L
                    (ClaimAncillary("L", "M2")) with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Dedicated Role primary must not be extended: %A" unexpected)
            match WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) 6 1007L
                    (CompleteMessage("L", w1.Id, "M1", 0, "reply")) with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Stale handler generation must reject: %A" unexpected)
            write b 6 1008L (CompleteMessage("L", w1.Id, "M1", 1, "reply"))
            let _, final = WorkQueueGitLedger.load a |> success
            Assert.That(final.Works.[w1.Id].State, Is.EqualTo(WorkState.Completed))
            Assert.That(final.Messages.["M1"].State, Is.EqualTo(MessageState.Handled "reply"))
            Assert.That(final.Messages.["M2"].State, Is.EqualTo(MessageState.Pending))
            Assert.That(final.Launches.["L"].State.ToString().StartsWith("TerminalPendingRelease"), Is.True))

    [<Test>]
    member _.AncillaryGateReleaseFencesStaleReplyAndPreservesUnrelatedPrimary() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, previous = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) previous.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            let subs = Map.ofList [ "R", set [ "planning"; "role:R" ] ]
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) subs |> success |> ignore
            let primary = { plan id "P" [] with AvailableAt = 900L; AvailableSequence = 1L }
            let blocker = { plan id "B" [] with AvailableAt = 1000L; AvailableSequence = 2L }
            let msg, work0 = roleMessage id "M" "R"
            let work = { work0 with AvailableAt = 1001L; AvailableSequence = 3L }
            let downstream =
                { plan id "D" [ work.Id ] with AvailableAt = 1002L; AvailableSequence = 4L }
            mutate a 1000L (PublishStage primary)
            mutate b 1001L (PublishStage blocker)
            mutate a 1002L (PostMessage (msg, Some work))
            mutate a 1004L (AdmitProject (launch id "L1" "R"))
            mutate b 1005L (DispatchOnce("L1", DispatchState.Submitted))
            mutate a 1006L (InitializeAgent("L1", "key-1"))
            mutate b 1007L (ClaimNext "L1")
            let _, claimed = WorkQueueGitLedger.load b |> success
            Assert.That(claimed.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            mutate a 1008L (ClaimAncillary ("L1", "M"))
            mutate b 1008L (PublishStage downstream)
            let _, ancillary = WorkQueueGitLedger.load a |> success
            Assert.That(ancillary.Messages.["M"].HandlerGeneration, Is.EqualTo(1))
            mutate b 1009L (SetHardPrerequisite (work.Id, "B"))
            let _, gated = WorkQueueGitLedger.load b |> success
            let claim = gated.Works.[work.Id]
            let l1 = gated.Launches.["L1"]
            let graph =
                match claim.Gate with
                | GateState.DependencyGatePending v -> v
                | unexpected -> failwithf "Ancillary edit must fence handler: %A" unexpected
            match WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) gated.Version 1010L
                    (CompleteAncillary ("L1", "M", 1, "premature")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Gated handler may not reply: %A" other)
            mutate b 1011L
                (ResolveAncillaryGate ("L1", "M", work.Id, l1.Version, claim.ClaimGeneration, graph))
            let _, released = WorkQueueGitLedger.load a |> success
            Assert.That(released.Works.[work.Id].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(released.Messages.["M"].State, Is.EqualTo(MessageState.Pending))
            Assert.That(released.Messages.["M"].HandlerGeneration, Is.EqualTo(2))
            Assert.That(released.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L1"))
            Assert.That(released.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            Assert.That(released.Launches.["L1"].AncillaryMessages, Is.Empty)
            Assert.That(released.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            mutate a 1012L (AdmitProject (launch id "L2" "R"))
            mutate b 1013L (DispatchOnce ("L2", DispatchState.Submitted))
            mutate a 1014L (InitializeAgent ("L2", "key-2"))
            mutate b 1015L (ClaimNext "L2")
            let _, anotherClaim = WorkQueueGitLedger.load a |> success
            Assert.That(anotherClaim.Launches.["L2"].PrimaryWorkId, Is.EqualTo(Some "B"))
            mutate b 1020L (CompleteOrdinary ("L2", "B"))
            let _, afterBlocker = WorkQueueGitLedger.load b |> success
            Assert.That(afterBlocker.Works.[work.Id].State, Is.EqualTo(WorkState.Available))
            Assert.That(afterBlocker.Works.[work.Id].AvailableAt, Is.EqualTo(1020L))
            Assert.That(afterBlocker.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            mutate a 1021L (ClaimAncillary ("L1", "M"))
            let _, reclaimed = WorkQueueGitLedger.load a |> success
            Assert.That(reclaimed.Messages.["M"].HandlerGeneration, Is.EqualTo(3))
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) reclaimed.Version 1022L
                    (CompleteAncillary ("L1", "M", 1, "stale")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Stale handler generation must be fenced: %A" other)
            mutate b 1023L (CompleteAncillary ("L1", "M", 3, "accepted"))
            let _, afterReply = WorkQueueGitLedger.load a |> success
            Assert.That(afterReply.Messages.["M"].State, Is.EqualTo(MessageState.Handled "accepted"))
            Assert.That(afterReply.Works.[work.Id].State, Is.EqualTo(WorkState.Completed))
            Assert.That(afterReply.Works.["D"].State, Is.EqualTo(WorkState.Available))
            Assert.That(afterReply.Works.["D"].AvailableAt, Is.EqualTo(1023L))
            Assert.That(afterReply.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L1"))
            mutate a 1024L (CompleteOrdinary ("L1", "P"))
            let _, final = WorkQueueGitLedger.load b |> success
            Assert.That(final.Works.["P"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(final.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            Assert.That(final.Launches.["L1"].AncillaryMessages, Is.Empty))

    [<Test>]
    member _.GitPublicationOverridesCallerFIFOTimeAndSequence() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) Map.empty |> success |> ignore
            let untrusted = { plan (a.ProjectId.ToString("D")) "W" [] with
                                AvailableAt = 91234567L; AvailableSequence = 12345L }
            WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) 0 2000L (PublishStage untrusted)
            |> success |> ignore
            let _, snapshot = WorkQueueGitLedger.load b |> success
            Assert.That(snapshot.Works.["W"].AvailableAt, Is.EqualTo(2000L))
            Assert.That(snapshot.Works.["W"].AvailableSequence, Is.EqualTo(1L))
            Assert.That(snapshot.NextAvailability, Is.EqualTo(1L)))

    [<Test>]
    member _.TamperedHistoricalPassiveTaskWorkIdFailsClosedEvenWithMatchingHash() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) Map.empty |> success |> ignore
            let victim = plan (a.ProjectId.ToString("D")) "victim" []
            WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) 0 1000L (PublishStage victim)
            |> success |> ignore
            let message =
                { Id = "T"; Subject = Subject.Task "task"; Body = "passive"
                  State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }
            WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 1 1001L (PostMessage(message, None))
            |> success |> ignore
            let snapshot = LedgerStore.fetch a |> success
            let content, oid = LedgerStore.readAt a snapshot WorkQueueGitLedger.RecordPath |> success
            let original = content |> Option.defaultWith (fun () -> failwith "Missing record")
            let envelope = JsonNode.Parse(original)
            let state = envelope.["State"]
            state.["Messages"].["T"].AsObject().["WorkId"] <-
                JsonNode.Parse("""{"case":"Some","fields":["victim"]}""")
            let hash =
                SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString()))
                |> Convert.ToHexString |> fun text -> text.ToLowerInvariant()
            envelope.AsObject().["StateHash"] <- JsonValue.Create(hash)
            let altered = envelope.ToJsonString()
            let poisonIntent =
                { OperationId = Guid.NewGuid(); Actor = "AdversarialFixture"
                  Kind = "FixtureHistoricalRecordTamper"
                  RequestJson = """{"case":"passive-task-poison"}""" }
            LedgerStore.transact a poisonIntent (fun _ ->
                Ok [ { Path = WorkQueueGitLedger.RecordPath
                       ExpectedBlobOid = oid
                       Content = altered } ])
            |> success |> ignore
            match WorkQueueGitLedger.load b with
            | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Cross-queue passive Task poison must fail closed: %A" other))

    [<Test>]
    member _.SimultaneousClientsPublishOnlyOneWinnerOnBareRemote() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) Map.empty |> success |> ignore
            LedgerStore.fetch b |> success |> ignore
            use ready = new CountdownEvent(2)
            use gate = new ManualResetEventSlim(false)
            let publish client label =
                Task.Run(fun () ->
                    ready.Signal() |> ignore
                    if not (gate.Wait(TimeSpan.FromSeconds(20.))) then
                        failwith "race gate expired"
                    WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) 0 1000L
                        (PublishStage(plan (a.ProjectId.ToString("D")) label [])))
            let first = publish a "A"
            let second = publish b "B"
            Assert.That(ready.Wait(TimeSpan.FromSeconds(20.)), Is.True)
            gate.Set()
            Assert.That(Task.WaitAll([| first :> Task; second :> Task |], TimeSpan.FromSeconds(90.)), Is.True)
            let results = [ first.Result; second.Result ]
            Assert.That(results |> List.filter Result.isOk |> List.length, Is.EqualTo(1))
            Assert.That(results |> List.filter (function Error(StaleEntityVersion _) -> true | _ -> false)
                        |> List.length, Is.EqualTo(1))
            let _, state = WorkQueueGitLedger.load a |> success
            Assert.That(state.Works.Count, Is.EqualTo(1))
            Assert.That(state.Version, Is.EqualTo(1)))

    [<Test>]
    member _.ReadOnlyYieldMovesFIFOAndFencesOldPrimaryGeneration() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, state = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) state.Version now mutation
                |> success
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ]) |> success |> ignore
            mutate a 1000L (PublishStage (plan id "P" [])) |> ignore
            mutate b 1001L (PublishStage (plan id "B" [])) |> ignore
            mutate a 1002L (AdmitProject (launch id "L1" "Lead")) |> ignore
            mutate b 1003L (DispatchOnce ("L1", DispatchState.Submitted)) |> ignore
            mutate a 1004L (InitializeAgent ("L1", "key")) |> ignore
            mutate b 1005L (ClaimNext "L1") |> ignore
            let _, before = WorkQueueGitLedger.load a |> success
            Assert.That(before.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            let olderSequence = before.Works.["B"].AvailableSequence
            let yieldOp = Guid.NewGuid()
            let write =
                WorkQueueGitLedger.executeAt a "Worker" yieldOp before.Version 2000L
                    (YieldReadOnly ("L1", "P", 1)) |> success
            let _, after = WorkQueueGitLedger.load b |> success
            Assert.That(after.Works.["P"].State, Is.EqualTo(WorkState.Available))
            Assert.That(after.Works.["P"].ClaimGeneration, Is.EqualTo(2))
            Assert.That(after.Works.["P"].AvailableAt, Is.EqualTo(2000L))
            Assert.That(after.Works.["P"].AvailableSequence, Is.GreaterThan(olderSequence))
            Assert.That(after.Launches.["L1"].State, Is.EqualTo(LaunchState.TerminalPendingRelease("yield:" + yieldOp.ToString("D"))))
            let order = Contracts.orderAvailable after |> success
            Assert.That((order |> List.map (fun w -> w.Id)) = [ "B"; "P" ], Is.True)
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) after.Version 2001L
                    (CompleteOrdinary ("L1", "P")) with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Stale original primary completion must reject: %A" unexpected)
            let replay =
                WorkQueueGitLedger.executeAt b "Worker" yieldOp before.Version 2000L
                    (YieldReadOnly ("L1", "P", 1)) |> success
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(write.Operation.OriginalResultHash)))

    [<Test>]
    member _.PrimaryHardGateBlocksUntilPrerequisiteCompletesAndWakesOnce() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, state = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) state.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ]) |> success |> ignore
            mutate a 1000L (PublishStage (plan id "P" []))
            mutate b 1001L (PublishStage (plan id "B" []))
            mutate a 1002L (AdmitProject (launch id "L1" "Lead"))
            mutate a 1003L (DispatchOnce ("L1", DispatchState.Submitted))
            mutate b 1004L (InitializeAgent ("L1", "key-1"))
            mutate a 1005L (ClaimNext "L1")
            let _, primary = WorkQueueGitLedger.load b |> success
            Assert.That(primary.Launches.["L1"].PrimaryWorkId, Is.EqualTo(Some "P"))
            mutate b 1006L (SetHardPrerequisite ("P", "B"))
            let _, gated = WorkQueueGitLedger.load a |> success
            match WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) gated.Version 1007L
                    (CompleteOrdinary ("L1", "P")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Gated stage cannot complete: %A" other)
            mutate a 1008L (ResolveReadOnlyGate ("L1", "P", 1))
            let _, blocked = WorkQueueGitLedger.load b |> success
            Assert.That(blocked.Works.["P"].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(blocked.Launches.["L1"].State.ToString().StartsWith("TerminalPendingRelease"), Is.True)
            mutate b 1009L (AdmitProject (launch id "L2" "Lead"))
            mutate a 1010L (DispatchOnce ("L2", DispatchState.Submitted))
            mutate b 1011L (InitializeAgent ("L2", "key-2"))
            mutate a 1012L (ClaimNext "L2")
            let _, secondClaim = WorkQueueGitLedger.load b |> success
            Assert.That(secondClaim.Launches.["L2"].PrimaryWorkId, Is.EqualTo(Some "B"))
            mutate a 2000L (CompleteOrdinary ("L2", "B"))
            let _, ready = WorkQueueGitLedger.load b |> success
            Assert.That(ready.Works.["P"].State, Is.EqualTo(WorkState.Available))
            Assert.That(ready.Works.["P"].AvailableAt, Is.EqualTo(2000L))
            Assert.That(ready.Works.["P"].AvailableSequence, Is.EqualTo(ready.NextAvailability)))

    [<Test>]
    member _.ReadOnlyCheckpointHasExactImmutableReplayAndClaimGenerationFence() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, state = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) state.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ]) |> success |> ignore
            mutate a 1000L (PublishStage(plan id "P" []))
            mutate b 1001L (AdmitProject(launch id "L" "Lead"))
            mutate a 1002L (DispatchOnce("L", DispatchState.Submitted))
            mutate b 1003L (InitializeAgent("L", "key"))
            mutate a 1004L (ClaimNext "L")
            let _, before = WorkQueueGitLedger.load b |> success
            Assert.That(before.Works.["P"].ClaimGeneration, Is.EqualTo(1))
            let op = Guid.NewGuid()
            let sha = String.replicate 64 "a"
            let payload = CheckpointReadOnly("L", "P", 1, sha)
            let written = WorkQueueGitLedger.executeAt b "Worker" op before.Version 1005L payload
                          |> success
            let _, recorded = WorkQueueGitLedger.load a |> success
            Assert.That(recorded.Version, Is.EqualTo(before.Version + 1))
            Assert.That(recorded.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L"))
            match recorded.Receipts.[op.ToString("D")] with
            | ProjectOperationPayload.OtherMutation evidence ->
                Assert.That(evidence.Contains(sha), Is.True)
            | unexpected -> Assert.Fail(sprintf "Checkpoint must have typed immutable receipt: %A" unexpected)
            let replay = WorkQueueGitLedger.executeAt a "Worker" op before.Version 1005L payload
                         |> success
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(written.Operation.OriginalResultHash))
            match WorkQueueGitLedger.executeAt b "Worker" op before.Version 1005L
                    (CheckpointReadOnly("L", "P", 1, String.replicate 64 "b")) with
            | Error OperationIdReused -> ()
            | unexpected -> Assert.Fail(sprintf "Changed checkpoint semantic payload must reject: %A" unexpected)
            mutate b 1006L (YieldReadOnly("L", "P", 1))
            let _, yielded = WorkQueueGitLedger.load a |> success
            match WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) yielded.Version 1007L payload with
            | Error(InvalidBinding _) -> ()
            | unexpected -> Assert.Fail(sprintf "Stale read-only checkpoint generation must reject: %A" unexpected))

    [<Test>]
    member _.ForgedPublicationWaiversNeverBypassDependenciesAcrossClones() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, prior = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) prior.Version now mutation
                |> success |> ignore
            let reject client now mutation =
                let _, prior = WorkQueueGitLedger.load client |> success
                match WorkQueueGitLedger.executeAt client "Attacker" (Guid.NewGuid()) prior.Version now mutation with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Forged waiver was admitted: %A" other)
                let _, unchanged = WorkQueueGitLedger.load b |> success
                Assert.That(unchanged.Version, Is.EqualTo(prior.Version))
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage (plan id "B" []))
            let forgedD =
                { plan id "D" [ "B" ] with
                    WaivedDependencies = Map.ofList [ "B", "not-an-account-approval" ] }
            reject b 1001L (PublishStage forgedD)
            let message, roleWork = roleMessage id "M" "R"
            let forgedRoleWork =
                { roleWork with
                    Dependencies = Set.singleton "B"
                    WaivedDependencies = Map.ofList [ "B", "not-an-account-approval" ] }
            reject a 1002L (PostMessage (message, Some forgedRoleWork))
            let _, before = WorkQueueGitLedger.load b |> success
            Assert.That(before.Works.ContainsKey "D", Is.False)
            Assert.That(before.Works.ContainsKey roleWork.Id, Is.False)
            Assert.That(before.Messages.ContainsKey "M", Is.False)
            mutate b 1003L (PublishStage (plan id "D" [ "B" ]))
            mutate a 1004L (PostMessage (message, Some { roleWork with Dependencies = Set.singleton "B" }))
            let _, blocked = WorkQueueGitLedger.load b |> success
            Assert.That(blocked.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(blocked.Works.[roleWork.Id].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(blocked.Works.["D"].WaivedDependencies, Is.Empty)
            mutate a 1005L (AdmitProject (launch id "L" "Lead"))
            mutate b 1006L (DispatchOnce ("L", DispatchState.Submitted))
            mutate a 1007L (InitializeAgent ("L", "session"))
            mutate b 1008L (ClaimNext "L")
            let _, claimed = WorkQueueGitLedger.load a |> success
            Assert.That(claimed.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "B"))
            Assert.That(claimed.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
            mutate a 2000L (CompleteOrdinary ("L", "B"))
            let _, woken = WorkQueueGitLedger.load b |> success
            Assert.That(woken.Works.["B"].State, Is.EqualTo(WorkState.Completed))
            Assert.That(woken.Works.["D"].State, Is.EqualTo(WorkState.Available))
            Assert.That(woken.Works.[roleWork.Id].State, Is.EqualTo(WorkState.Available))
            Assert.That(woken.Works.["D"].AvailableAt, Is.EqualTo(2000L))
            Assert.That(woken.Works.[roleWork.Id].AvailableAt, Is.EqualTo(2000L)))

    [<Test>]
    member _.ForgedRegisteredSubmittedAndTerminalAdmissionsCannotClaim() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, prior = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) prior.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage (plan id "P" []))
            let reserved = launch id "L" "Lead"
            let lease = { LastSeenAt = 1001L; Generation = 1; StopRequested = false }
            let forged =
                [ { reserved with State = LaunchState.Registered("forged-uuid", "forged-session")
                                  Lease = Some lease; Version = 2; DispatchCount = 1 }
                  { reserved with State = LaunchState.SubmittedUnregistered DispatchState.Submitted
                                  Version = 1; DispatchCount = 1 }
                  { reserved with State = LaunchState.TerminalPendingRelease "forged-release"
                                  Version = 1 }
                  { reserved with Lease = Some lease }
                  { reserved with Credit = Some "P" }
                  { reserved with EverClaimed = true; PrimaryWorkId = Some "P" }
                  { reserved with AccountTokenId = "" } ]
            for bad in forged do
                let _, prior = WorkQueueGitLedger.load b |> success
                match WorkQueueGitLedger.executeAt b "Attacker" (Guid.NewGuid()) prior.Version 1001L
                        (AdmitProject bad) with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Invalid initial launch was admitted: %A / %A" bad other)
                let _, untouched = WorkQueueGitLedger.load a |> success
                Assert.That(untouched.Version, Is.EqualTo(prior.Version))
                Assert.That(untouched.Launches.ContainsKey "L", Is.False)
            let _, noLaunch = WorkQueueGitLedger.load b |> success
            match WorkQueueGitLedger.executeAt b "Attacker" (Guid.NewGuid()) noLaunch.Version 1002L
                    (ClaimNext "L") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Forged launch obtained claim: %A" other)
            mutate a 1003L (AdmitProject reserved)
            mutate b 1004L (DispatchOnce ("L", DispatchState.Submitted))
            mutate a 1005L (InitializeAgent ("L", "real-session"))
            mutate b 1006L (ClaimNext "L")
            let _, real = WorkQueueGitLedger.load a |> success
            Assert.That(real.Launches.["L"].EverClaimed, Is.True)
            Assert.That(real.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "P"))
            Assert.That(real.Launches.["L"].DispatchCount, Is.EqualTo(1))
            match real.Launches.["L"].State with
            | LaunchState.Registered(_, "real-session") -> ()
            | other -> Assert.Fail(sprintf "Legitimate registration failed: %A" other))

    [<Test>]
    member _.SourceWritingCompletionWithoutHandoffCannotWakeDependents() =
        for useIncompleteLineage in [ false; true ] do
            withClones (fun a b ->
                let mutate client now mutation =
                    let _, prior = WorkQueueGitLedger.load client |> success
                    WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) prior.Version now mutation
                    |> success |> ignore
                let id = a.ProjectId.ToString("D")
                let lineage =
                    if useIncompleteLineage then
                        Some
                            { RepositoryId = "source-repo"
                              TaskId = "task-I"
                              TargetRef = "refs/heads/main"
                              LineageId = "mutable-I"
                              PinnedBaseCommit = "pinned"
                              ReachableRef = None
                              ContinuationSha = None
                              Generation = 1 }
                    else None
                let implementation =
                    { plan id "I" [] with
                        Kind = WorkKind.Implementation
                        QueueId = "implementation"
                        SourceLineage = lineage }
                WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                    (Map.ofList [ "Lead", set [ "implementation" ] ])
                |> success |> ignore
                mutate a 1000L (PublishStage implementation)
                mutate b 1001L (PublishStage (plan id "D" [ "I" ]))
                mutate a 1002L (AdmitProject (launch id "L" "Lead"))
                mutate b 1003L (DispatchOnce ("L", DispatchState.Submitted))
                mutate a 1004L (InitializeAgent ("L", "real-session"))
                mutate b 1005L (ClaimNext "L")
                let _, claimed = WorkQueueGitLedger.load a |> success
                Assert.That(claimed.Works.["I"].State, Is.EqualTo(WorkState.Claimed "L"))
                Assert.That(claimed.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
                match WorkQueueGitLedger.executeAt b "Attacker" (Guid.NewGuid()) claimed.Version 2000L
                        (CompleteOrdinary ("L", "I")) with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Unproven source completion was accepted: %A" other)
                let _, after = WorkQueueGitLedger.load b |> success
                Assert.That(after.Version, Is.EqualTo(claimed.Version))
                Assert.That(after.Works.["I"].State, Is.EqualTo(WorkState.Claimed "L"))
                Assert.That(after.Works.["D"].State, Is.EqualTo(WorkState.Blocked))
                Assert.That(after.NextAvailability, Is.EqualTo(claimed.NextAvailability))
                match after.Launches.["L"].State with
                | LaunchState.Registered _ -> ()
                | other -> Assert.Fail(sprintf "Unverified completion minted terminal receipt: %A" other))

    [<Test>]
    member _.NoClaimTerminalNeedsProofAndCannotBeatLiveClaim() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, prior = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) prior.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage (plan id "P" []))
            mutate b 1001L (AdmitProject (launch id "L" "Lead"))
            mutate a 1002L (DispatchOnce ("L", DispatchState.Submitted))
            mutate b 1003L (InitializeAgent ("L", "live-session"))
            let _, registered = WorkQueueGitLedger.load a |> success
            match WorkQueueGitLedger.executeAt a "Attacker" (Guid.NewGuid()) registered.Version 1004L
                    (TerminalizeNoClaim "L") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Live no-claim release was minted: %A" other)
            let _, stillLive = WorkQueueGitLedger.load b |> success
            Assert.That(stillLive.Version, Is.EqualTo(registered.Version))
            match stillLive.Launches.["L"].State with
            | LaunchState.Registered(_, "live-session") -> ()
            | other -> Assert.Fail(sprintf "Live registration changed without proof: %A" other)
            mutate b 1005L (ClaimNext "L")
            let _, claimed = WorkQueueGitLedger.load a |> success
            Assert.That(claimed.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "P"))
            match WorkQueueGitLedger.executeAt a "Attacker" (Guid.NewGuid()) claimed.Version 1006L
                    (TerminalizeNoClaim "L") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Already-claimed launch terminalized: %A" other)
            let _, unchanged = WorkQueueGitLedger.load b |> success
            Assert.That(unchanged.Version, Is.EqualTo(claimed.Version))
            Assert.That(unchanged.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L")))

    [<Test>]
    member _.StoppedLeaseWithoutJournalOrSessionProofCannotReleaseToken() =
        withClones (fun a b ->
            let mutate client now mutation =
                let _, prior = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) prior.Version now mutation
                |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            mutate a 1000L (PublishStage (plan id "P" []))
            mutate b 1001L (AdmitProject (launch id "L" "Lead"))
            mutate a 1002L (DispatchOnce ("L", DispatchState.Submitted))
            mutate b 1003L (InitializeAgent ("L", "session"))
            mutate a 2000L (StopStalled ("L", 1, 100L))
            let _, stopped = WorkQueueGitLedger.load b |> success
            let lease = stopped.Launches.["L"].Lease.Value
            Assert.That(lease.StopRequested, Is.True)
            Assert.That(lease.Generation, Is.EqualTo(2))
            match WorkQueueGitLedger.executeAt b "Attacker" (Guid.NewGuid()) stopped.Version 2100L
                    (TerminalizeNoClaim "L") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Stopped lease was treated as terminal proof: %A" other)
            let _, after = WorkQueueGitLedger.load a |> success
            Assert.That(after.Version, Is.EqualTo(stopped.Version))
            Assert.That(after.Launches.["L"].EverClaimed, Is.False)
            match after.Launches.["L"].State with
            | LaunchState.Registered _ -> ()
            | other -> Assert.Fail(sprintf "Stopped live lease minted release receipt: %A" other))

    [<Test>]
    member _.PoisonedHistoricalWaiverFailsClosedEvenWithMatchingHash() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid()) Map.empty |> success |> ignore
            let id = a.ProjectId.ToString("D")
            WorkQueueGitLedger.executeAt a "Worker" (Guid.NewGuid()) 0 1000L
                (PublishStage (plan id "B" [])) |> success |> ignore
            WorkQueueGitLedger.executeAt b "Worker" (Guid.NewGuid()) 1 1001L
                (PublishStage (plan id "D" [ "B" ])) |> success |> ignore
            let snapshot = LedgerStore.fetch a |> success
            let payload, oid = LedgerStore.readAt a snapshot WorkQueueGitLedger.RecordPath |> success
            let original = payload |> Option.defaultWith (fun () -> failwith "Missing work record")
            let envelope = JsonNode.Parse(original)
            let state = envelope.["State"]
            state.["Works"].["D"].AsObject().["WaivedDependencies"] <-
                JsonNode.Parse("""{"B":"forged-approval"}""")
            let hash =
                SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString()))
                |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
            envelope.AsObject().["StateHash"] <- JsonValue.Create(hash)
            let poison =
                { OperationId = Guid.NewGuid()
                  Actor = "AdversarialFixture"
                  Kind = "FixtureForgedWaiver"
                  RequestJson = """{"case":"historical-waiver-poison"}""" }
            LedgerStore.transact a poison (fun _ ->
                Ok [ { Path = WorkQueueGitLedger.RecordPath
                       ExpectedBlobOid = oid
                       Content = envelope.ToJsonString() } ])
            |> success |> ignore
            match WorkQueueGitLedger.load b with
            | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Unproven historical waiver was loaded: %A" other))

    [<Test>]
    member _.MalformedNestedStagePublicationCannotPoisonRemoteAndValidClaimRecovers() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "Lead", set [ "planning" ] ])
            |> success |> ignore
            let id = a.ProjectId.ToString("D")
            let valid = plan id "P" []
            let lineage =
                { RepositoryId = "repository"; TaskId = "task-P"
                  TargetRef = "refs/heads/main"; LineageId = "lineage-P"
                  PinnedBaseCommit = "base-sha"; ReachableRef = None
                  ContinuationSha = None; Generation = 1 }
            let malformed =
                [ { valid with QueueId = null }
                  { valid with QueueId = "  " }
                  { valid with SubjectId = null }
                  { valid with Dependencies = Set.singleton null }
                  { valid with SourceLineage = Some { lineage with TargetRef = null } } ]
            let rejectedOp = Guid.NewGuid()
            for index, bad in malformed |> List.indexed do
                let snapshot, prior = WorkQueueGitLedger.load b |> success
                let operationId = if index = 0 then rejectedOp else Guid.NewGuid()
                match WorkQueueGitLedger.executeAt a "Attacker" operationId prior.Version (1000L + int64 index)
                        (PublishStage bad) with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Malformed nested stage %d was published: %A" index other)
                let verified, unchanged = WorkQueueGitLedger.load b |> success
                Assert.That(verified.Commit, Is.EqualTo(snapshot.Commit), "Rejected stage changed remote head")
                Assert.That(unchanged.Version, Is.EqualTo(prior.Version))
                Assert.That(unchanged.Receipts = prior.Receipts, Is.True)
                Assert.That(unchanged.Works = prior.Works, Is.True)
            // A rejected UUID has no operation receipt: valid corrected publication
            // with the same UUID is not mistaken for a committed poison.
            let published =
                WorkQueueGitLedger.executeAt b "Worker" rejectedOp 0 2000L (PublishStage valid)
                |> success
            Assert.That(published.AlreadyApplied, Is.False)
            let _, afterPublish = WorkQueueGitLedger.load a |> success
            Assert.That(afterPublish.Works.["P"].QueueId, Is.EqualTo("planning"))
            let mutate client now payload =
                let _, current = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) current.Version now payload
                |> success |> ignore
            mutate a 2001L (AdmitProject (launch id "L" "Lead"))
            mutate b 2002L (DispatchOnce ("L", DispatchState.Submitted))
            mutate a 2003L (InitializeAgent ("L", "session"))
            mutate b 2004L (ClaimNext "L")
            let _, recovered = WorkQueueGitLedger.load a |> success
            Assert.That(recovered.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some "P"))
            Assert.That(recovered.Works.["P"].State, Is.EqualTo(WorkState.Claimed "L"))
            Assert.That(recovered.Receipts.Count, Is.EqualTo(recovered.Version)))

    [<Test>]
    member _.MalformedNestedTaskAndRoleMessagesCannotPoisonRemoteAndRoleClaimRecovers() =
        withClones (fun a b ->
            WorkQueueGitLedger.initialize a "Worker" (Guid.NewGuid())
                (Map.ofList [ "R", set [ "role:R" ] ])
            |> success |> ignore
            let id = a.ProjectId.ToString("D")
            let passive =
                { Id = "T"; Subject = Subject.Task "task-T"; Body = "passive task"
                  State = MessageState.Pending; HandlerGeneration = 0; WorkId = None }
            let role, roleWork = roleMessage id "M" "R"
            let malformed =
                [ PostMessage ({ passive with Body = null }, None)
                  PostMessage ({ passive with Body = "  " }, None)
                  PostMessage ({ passive with Id = null }, None)
                  PostMessage ({ passive with Subject = Subject.Task null }, None)
                  PostMessage ({ role with Subject = Subject.Role null }, Some roleWork)
                  PostMessage (role, Some { roleWork with QueueId = null })
                  PostMessage (role, Some { roleWork with SubjectId = " " }) ]
            for index, payload in malformed |> List.indexed do
                let snapshot, prior = WorkQueueGitLedger.load a |> success
                let client = if index % 2 = 0 then b else a
                match WorkQueueGitLedger.executeAt client "Attacker" (Guid.NewGuid()) prior.Version
                        (1000L + int64 index) payload with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Malformed message %d was published: %A" index other)
                let verified, unchanged = WorkQueueGitLedger.load b |> success
                Assert.That(verified.Commit, Is.EqualTo(snapshot.Commit), "Rejected message changed remote head")
                Assert.That(unchanged.Version, Is.EqualTo(prior.Version))
                Assert.That(unchanged.Receipts = prior.Receipts, Is.True)
                Assert.That(unchanged.Messages = prior.Messages, Is.True)
                Assert.That(unchanged.Works = prior.Works, Is.True)
            let mutate client now payload =
                let _, current = WorkQueueGitLedger.load client |> success
                WorkQueueGitLedger.executeAt client "Worker" (Guid.NewGuid()) current.Version now payload
                |> success |> ignore
            mutate a 2000L (PostMessage (passive, None))
            mutate b 2001L (PostMessage (role, Some roleWork))
            mutate a 2002L (AdmitProject (launch id "L" "R"))
            mutate b 2003L (DispatchOnce ("L", DispatchState.Submitted))
            mutate a 2004L (InitializeAgent ("L", "session"))
            mutate b 2005L (ClaimNext "L")
            let _, recovered = WorkQueueGitLedger.load a |> success
            Assert.That(recovered.Messages.["T"].WorkId, Is.EqualTo(None))
            Assert.That(recovered.Launches.["L"].PrimaryWorkId, Is.EqualTo(Some roleWork.Id))
            Assert.That(recovered.Works.[roleWork.Id].State, Is.EqualTo(WorkState.Claimed "L"))
            Assert.That(recovered.Receipts.Count, Is.EqualTo(recovered.Version)))
