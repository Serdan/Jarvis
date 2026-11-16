module Client.Tests.WorkNoPersistentAgentLifecycleTests

open System
open System.Diagnostics
open System.IO
open System.Text
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.RoleInstructions

// All Git repositories, launch credentials, Task and Agent history in this file
// are private disposable fixtures. This exercises accepted default-off WQ02 CAS,
// NOT an operational browser/provider launch, private bearer handoff, Scheduler,
// account bootstrap, or the old-writer fence. None of those is a cutover GO.

type private Fixture =
    { A: LedgerBinding
      B: LedgerBinding
      TaskId: string
      TaskPath: string
      TaskBody: string
      AgentPath: string option
      AgentBody: string option
      InstructionsPath: string option
      InstructionsBody: string option
      HistoricalTree: string }

let private succeed = function
    | Ok value -> value
    | Error reason -> failwithf "Disposable Work/Agent fixture rejected: %A" reason

let private git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in args do p.StartInfo.ArgumentList.Add arg
    Assert.That(p.Start(), Is.True, sprintf "Failed to start disposable git %A" args)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    if not (p.WaitForExit(30000)) then
        p.Kill(true)
        Assert.Fail(sprintf "Disposable Git command timed out: %A" args)
    Assert.That(p.ExitCode, Is.Zero, sprintf "git %A: %s" args error)
    output.Trim()

let private agentKey (id: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes id)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private rawAt binding snapshot path =
    match LedgerStore.readAt binding snapshot path |> succeed with
    | Some content, Some _ -> content
    | other -> failwithf "Expected readable historical blob %s: %A" path other

// Exact tree entries include blob OIDs, not only paths. Equality proves that
// existing Agent, Agent instructions, roles and notification bytes survive each
// winning original Git CAS, and that no new path in those families was created.
let private protectedTree binding snapshot =
    git binding.RepositoryPath
        [ "ls-tree"; "-r"; snapshot.Commit; "--"
          "agents"; "records/agent-instructions"; "records/roles"; "notifications" ]

let private withClones seedHistorical test =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-no-agent-work-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let bare = Path.Combine(root, "remote.git")
        let first, second = Path.Combine(root, "first"), Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        File.WriteAllText(Path.Combine(first, "source.txt"), "no-persistent-Agent disposable source")
        git first [ "add"; "source.txt" ] |> ignore
        git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
                    "commit"; "-m"; "disposable source" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore

        let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
        let binding path : LedgerBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = bare; ApprovedPushUrl = bare
              ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
        let a, b = binding first, binding second
        LedgerStore.initialize a true |> succeed |> ignore
        LedgerStore.fetch b |> succeed |> ignore
        WorkQueueGitLedger.initialize a "Work-native fixture" (Guid.NewGuid())
            (Map.ofList [ "Worker", set [ "planning" ] ]) |> succeed |> ignore

        // Seed historical legacy data BEFORE the Work lifecycle. This is never
        // registration of either tested ephemeral launch, and is never used as
        // authority for FirstConnect, Claim, Checkpoint, STOP or Complete.
        let historicalAgent = "Historical Fixture Worker"
        if seedHistorical then
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) historicalAgent
            |> succeed |> ignore
            RoleInstructions.createRole a "fixture" (Guid.NewGuid()) "LegacyFixtureRole"
                "Historical role only" "Retained historical role instructions"
            |> succeed |> ignore
            RoleInstructions.attachRole a "fixture" (Guid.NewGuid()) historicalAgent
                "LegacyFixtureRole" 0 |> succeed |> ignore
            RoleInstructions.setAgentInstructions a "fixture" (Guid.NewGuid())
                historicalAgent 1 "These original instructions must be byte-identical."
            |> succeed |> ignore

        // Real durable Task record; synthetic default-off WQ02 planning stages
        // below are linked to the same Task ID and Worker role. No WQ04 producer
        // or production Scheduler/launch is invoked by this fixture.
        let taskId = "native-lineage-task"
        ReviewWorkflow.createTask a (Guid.NewGuid()) taskId "Work-native lineage"
        |> succeed |> ignore
        let taskPath = "tasks/" + taskId + "/task.json"
        let key = agentKey historicalAgent
        let agentPath = "agents/" + key + ".json"
        let instructionsPath = "records/agent-instructions/" + key + ".json"
        let snapshot, _ = WorkQueueGitLedger.load a |> succeed
        let historicalTree = protectedTree a snapshot
        if seedHistorical then
            Assert.That(historicalTree, Does.Contain(agentPath))
            Assert.That(historicalTree, Does.Contain(instructionsPath))
            Assert.That(historicalTree, Does.Contain("records/roles/LegacyFixtureRole.json"))
        else
            Assert.That(historicalTree, Is.Empty,
                        "This variant must have zero registered Agents, roles and instructions.")
        let fixture =
            { A = a; B = b; TaskId = taskId; TaskPath = taskPath
              TaskBody = rawAt a snapshot taskPath
              AgentPath = if seedHistorical then Some agentPath else None
              AgentBody = if seedHistorical then Some(rawAt a snapshot agentPath) else None
              InstructionsPath = if seedHistorical then Some instructionsPath else None
              InstructionsBody = if seedHistorical then Some(rawAt a snapshot instructionsPath) else None
              HistoricalTree = historicalTree }
        test fixture
        // Ledger writes must never leak into either source checkout.
        Assert.That(git first [ "status"; "--porcelain" ], Is.Empty)
        Assert.That(git second [ "status"; "--porcelain" ], Is.Empty)
    finally
        if Directory.Exists root then Directory.Delete(root, true)

let private preserved (f: Fixture) binding =
    let snapshot, state = WorkQueueGitLedger.load binding |> succeed
    Assert.That(protectedTree binding snapshot, Is.EqualTo(f.HistoricalTree),
                "Original Git CAS changed historical Agent/role/instruction/notification blobs or added new ones.")
    match f.AgentPath, f.AgentBody with
    | Some path, Some body ->
        Assert.That(rawAt binding snapshot path, Is.EqualTo(body),
                    "The old Agent JSON must remain byte-for-byte readable.")
    | None, None -> ()
    | _ -> Assert.Fail("Invalid historical Agent fixture evidence.")
    match f.InstructionsPath, f.InstructionsBody with
    | Some path, Some body ->
        Assert.That(rawAt binding snapshot path, Is.EqualTo(body),
                    "The old Agent instruction JSON must remain byte-for-byte readable.")
    | None, None -> ()
    | _ -> Assert.Fail("Invalid historical instruction fixture evidence.")
    Assert.That(rawAt binding snapshot f.TaskPath, Is.EqualTo(f.TaskBody),
                "The durable canonical Task must remain the same across ephemeral launches.")
    snapshot, state

let private stage (f: Fixture) workId =
    { Id = workId; ProjectId = f.A.ProjectId.ToString("D")
      SubjectId = f.TaskId; Kind = WorkKind.Planning; QueueId = "planning"
      StageGeneration = 1; State = WorkState.Available
      Domain = DomainState.Undecided; Gate = GateState.Clear
      ClaimGeneration = 0; Dependencies = Set.empty
      WaivedDependencies = Map.empty
      AvailableAt = 0L; AvailableSequence = 0L
      SourceLineage = None; Push = None; IntegrationAuthorized = false }

let private reserved (f: Fixture) launchId =
    { Id = launchId; ProjectId = f.A.ProjectId.ToString("D")
      RoleId = "Worker"; RoleVersion = 1
      AccountTokenId = Guid.NewGuid().ToString("D")
      State = LaunchState.ReservedUnregistered; Lease = None
      Version = 0; Credit = None; PrimaryWorkId = None
      EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

let private inert (f: Fixture) binding now mutation =
    let _, state = preserved f binding
    let result =
        WorkQueueGitLedger.executeInertFixtureAt binding "no-agent-fixture"
            (Guid.NewGuid()) state.Version now mutation |> succeed
    preserved f binding |> ignore
    result

let private credentialCall binding op expectedVersion now launchId bearer action =
    WorkQueueGitLedger.executeCredentialFixtureAt binding "no-agent-credential-fixture"
        op expectedVersion now launchId bearer action

let private authenticated (f: Fixture) binding op expectedVersion now launchId bearer action =
    let result = credentialCall binding op expectedVersion now launchId bearer action |> succeed
    preserved f binding |> ignore
    result

let private mustRejectCredential (f: Fixture) binding expectedVersion now launchId bearer action =
    let before, _ = preserved f binding
    match credentialCall binding (Guid.NewGuid()) expectedVersion now launchId bearer action with
    | Error (InvalidBinding _) -> ()
    | other -> Assert.Fail(sprintf "Expected denied credential/terminal launch: %A" other)
    let after, _ = preserved f binding
    Assert.That(after.Commit, Is.EqualTo(before.Commit), "Rejected credential changed original Git head.")

let private prepare (f: Fixture) workId launchId now =
    let credential = IssuedWorkCredential.Issue launchId
    inert f f.A now (PublishStage(stage f workId)) |> ignore
    inert f f.B (now + 1L)
        (AdmitCredentialProject(reserved f launchId, credential.VerifierSha256)) |> ignore
    inert f f.A (now + 2L) (DispatchOnce(launchId, DispatchState.Submitted)) |> ignore
    credential

let private assertNoLaunchAgentRecord (f: Fixture) binding launchId =
    let snapshot, _ = preserved f binding
    Assert.That(protectedTree binding snapshot,
                Does.Not.Contain("agents/" + agentKey launchId + ".json"),
                "Work execution UUID is not a persistent Agent identity.")
    Assert.That(protectedTree binding snapshot,
                Does.Not.Contain("records/agent-instructions/" + agentKey launchId + ".json"))

let private claimAndCheckpoint f binding launchId bearer workId now =
    let _, state = preserved f binding
    authenticated f binding (Guid.NewGuid()) state.Version now launchId bearer
        CredentialWorkAction.ClaimPrimary |> ignore
    let _, claimed = preserved f binding
    let claimedStage = claimed.Works.[workId]
    Assert.That(claimedStage.State, Is.EqualTo(WorkState.Claimed launchId))
    let checkpointOp = Guid.NewGuid()
    authenticated f binding checkpointOp claimed.Version (now + 1L) launchId bearer
        (CredentialWorkAction.Checkpoint(workId, claimedStage.ClaimGeneration,
                                         String.replicate 64 "a")) |> ignore
    claimedStage.ClaimGeneration, checkpointOp, claimed.Version

let private complete f binding launchId bearer workId generation now =
    let _, before = preserved f binding
    authenticated f binding (Guid.NewGuid()) before.Version now launchId bearer
        (CredentialWorkAction.CompleteReadOnly(workId, generation, String.replicate 64 "b"))
    |> ignore
    let _, after = preserved f binding
    Assert.That(after.Works.[workId].State, Is.EqualTo WorkState.Completed)

[<TestFixture>]
type WorkNoPersistentAgentLifecycleTests() =
    [<Test>]
    member _.CredentialConnectClaimCheckpointCompletionAndOriginalUuidReplayNeverCreateAgentRecords() =
        withClones true (fun f ->
            let workId = "plan-credential-completion"
            let launchId = Guid.NewGuid().ToString("D")
            let credential = prepare f workId launchId 100L
            let bearer = credential.RevealForPrivateDelivery()
            let _, ready = preserved f f.B
            let connectOp = Guid.NewGuid()
            let connected =
                authenticated f f.B connectOp ready.Version 103L launchId bearer
                    CredentialWorkAction.FirstConnect
            Assert.That(connected.AlreadyApplied, Is.False)
            let connectHead, connectedState = preserved f f.A
            let replay =
                authenticated f f.A connectOp ready.Version 103L launchId bearer
                    CredentialWorkAction.FirstConnect
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That((preserved f f.A |> fst).Commit, Is.EqualTo(connectHead.Commit))
            let generation, checkpointOp, oldCheckpointVersion =
                claimAndCheckpoint f f.A launchId bearer workId 104L
            let checkpointHead, _ = preserved f f.B
            let checkpointReplay =
                authenticated f f.B checkpointOp oldCheckpointVersion 105L launchId bearer
                    (CredentialWorkAction.Checkpoint(workId, generation, String.replicate 64 "a"))
            Assert.That(checkpointReplay.AlreadyApplied, Is.True)
            Assert.That((preserved f f.B |> fst).Commit, Is.EqualTo(checkpointHead.Commit))
            let wrong = IssuedWorkCredential.Issue launchId
            let _, checkpointed = preserved f f.B
            mustRejectCredential f f.B checkpointed.Version 106L launchId
                (wrong.RevealForPrivateDelivery()) CredentialWorkAction.ClaimPrimary
            let finishOp = Guid.NewGuid()
            let _, beforeFinish = preserved f f.B
            let finishedOnce =
                authenticated f f.B finishOp beforeFinish.Version 107L launchId bearer
                    (CredentialWorkAction.CompleteReadOnly(workId, generation, String.replicate 64 "b"))
            Assert.That(finishedOnce.AlreadyApplied, Is.False)
            let finishedHead, finished = preserved f f.A
            Assert.That(finished.Works.[workId].State, Is.EqualTo WorkState.Completed)
            let finishReplay =
                authenticated f f.A finishOp beforeFinish.Version 107L launchId bearer
                    (CredentialWorkAction.CompleteReadOnly(workId, generation, String.replicate 64 "b"))
            Assert.That(finishReplay.AlreadyApplied, Is.True)
            Assert.That((preserved f f.A |> fst).Commit, Is.EqualTo(finishedHead.Commit))
            mustRejectCredential f f.A finished.Version 108L launchId bearer
                CredentialWorkAction.FirstConnect
            assertNoLaunchAgentRecord f f.A launchId
            Assert.That(connectedState.Launches.[launchId].RoleId, Is.EqualTo("Worker")))

    [<Test>]
    member _.TwoEphemeralCredentialLaunchesContinueOneDurableTaskRoleAndWorkLineageWithoutLegacyRegistration() =
        withClones true (fun f ->
            let firstId, secondId = Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")
            let firstWork, secondWork = "task-stage-first", "task-stage-next"
            let first = prepare f firstWork firstId 100L
            let firstBearer = first.RevealForPrivateDelivery()
            let _, beforeFirst = preserved f f.A
            authenticated f f.A (Guid.NewGuid()) beforeFirst.Version 103L firstId firstBearer
                CredentialWorkAction.FirstConnect |> ignore
            let firstGeneration, _, _ =
                claimAndCheckpoint f f.B firstId firstBearer firstWork 104L
            complete f f.B firstId firstBearer firstWork firstGeneration 106L
            assertNoLaunchAgentRecord f f.A firstId

            // A second independently issued one-shot launch continues the same
            // canonical Task and role via a subsequent Work stage; there is no
            // persistent Worker-N identity to recycle or carry between them.
            let second = prepare f secondWork secondId 200L
            let secondBearer = second.RevealForPrivateDelivery()
            let _, beforeSecond = preserved f f.B
            mustRejectCredential f f.B beforeSecond.Version 203L secondId firstBearer
                CredentialWorkAction.FirstConnect
            mustRejectCredential f f.A beforeSecond.Version 203L firstId secondBearer
                CredentialWorkAction.FirstConnect
            authenticated f f.B (Guid.NewGuid()) beforeSecond.Version 204L secondId secondBearer
                CredentialWorkAction.FirstConnect |> ignore
            let _, connected = preserved f f.A
            mustRejectCredential f f.A connected.Version 205L firstId firstBearer
                CredentialWorkAction.ClaimPrimary
            let secondGeneration, _, _ =
                claimAndCheckpoint f f.A secondId secondBearer secondWork 206L
            complete f f.A secondId secondBearer secondWork secondGeneration 208L

            let _, final = preserved f f.B
            Assert.That(firstId, Is.Not.EqualTo(secondId))
            Assert.That(final.Launches.[firstId].RoleId, Is.EqualTo("Worker"))
            Assert.That(final.Launches.[secondId].RoleId, Is.EqualTo("Worker"))
            Assert.That(final.Works.[firstWork].SubjectId, Is.EqualTo(f.TaskId))
            Assert.That(final.Works.[secondWork].SubjectId, Is.EqualTo(f.TaskId))
            Assert.That(final.Works.[firstWork].State, Is.EqualTo WorkState.Completed)
            Assert.That(final.Works.[secondWork].State, Is.EqualTo WorkState.Completed)
            assertNoLaunchAgentRecord f f.B firstId
            assertNoLaunchAgentRecord f f.B secondId)

    [<Test>]
    member _.StaleTwoCloneConnectStopAndReceiptReplayDoNotRecreateAgentOrInstructionRecords() =
        withClones true (fun f ->
            let workId = "plan-stop-replay"
            let launchId = Guid.NewGuid().ToString("D")
            let credential = prepare f workId launchId 100L
            let bearer = credential.RevealForPrivateDelivery()
            let _, beforeConnect = preserved f f.A
            let connectOp = Guid.NewGuid()
            authenticated f f.B connectOp beforeConnect.Version 1000L launchId bearer
                CredentialWorkAction.FirstConnect |> ignore
            let connectedHead, _ = preserved f f.A
            // Clone A's original expected version is stale even with a good
            // bearer. Only the exact original UUID may be replayed.
            match credentialCall f.A (Guid.NewGuid()) beforeConnect.Version 1001L
                      launchId bearer CredentialWorkAction.FirstConnect with
            | Error _ -> ()
            | Ok applied -> Assert.Fail(sprintf "Stale clone wrote a second connect: %A" applied)
            Assert.That((preserved f f.A |> fst).Commit, Is.EqualTo(connectedHead.Commit))
            let replay =
                authenticated f f.A connectOp beforeConnect.Version 1000L launchId bearer
                    CredentialWorkAction.FirstConnect
            Assert.That(replay.AlreadyApplied, Is.True)
            let generation, checkpointOp, oldCheckpointVersion =
                claimAndCheckpoint f f.A launchId bearer workId 1002L
            Assert.That(generation, Is.EqualTo(1))
            inert f f.B 2000L (StopStalled(launchId, 1, 500L)) |> ignore
            let stoppedHead, stopped = preserved f f.A
            Assert.That(stopped.Launches.[launchId].Lease.Value.StopRequested, Is.True)
            mustRejectCredential f f.A stopped.Version 2001L launchId bearer
                (CredentialWorkAction.Checkpoint(workId, generation, String.replicate 64 "c"))
            mustRejectCredential f f.B stopped.Version 2002L launchId bearer
                (CredentialWorkAction.CompleteReadOnly(workId, generation, String.replicate 64 "d"))
            mustRejectCredential f f.A stopped.Version 2003L launchId bearer
                (CredentialWorkAction.HeartbeatLease 1)
            let replayCheckpoint =
                authenticated f f.B checkpointOp oldCheckpointVersion 1003L launchId bearer
                    (CredentialWorkAction.Checkpoint(workId, generation, String.replicate 64 "a"))
            Assert.That(replayCheckpoint.AlreadyApplied, Is.True)
            Assert.That((preserved f f.B |> fst).Commit, Is.EqualTo(stoppedHead.Commit))
            assertNoLaunchAgentRecord f f.A launchId)

    [<Test>]
    member _.EmptyAgentLedgerMakesProgressWithoutAnyLegacyRegisterAgentOrAgentInboxPrerequisite() =
        withClones false (fun f ->
            let workId = "plan-agent-free-first"
            let launchId = Guid.NewGuid().ToString("D")
            Assert.That(f.HistoricalTree, Is.Empty)
            let credential = prepare f workId launchId 100L
            let bearer = credential.RevealForPrivateDelivery()
            let _, before = preserved f f.A
            authenticated f f.B (Guid.NewGuid()) before.Version 103L launchId bearer
                CredentialWorkAction.FirstConnect |> ignore
            let generation, _, _ =
                claimAndCheckpoint f f.A launchId bearer workId 104L
            complete f f.B launchId bearer workId generation 106L
            let _, final = preserved f f.A
            Assert.That(final.Works.[workId].State, Is.EqualTo WorkState.Completed)
            Assert.That(final.Launches.[launchId].RoleId, Is.EqualTo("Worker"))
            Assert.That(protectedTree f.A (preserved f f.A |> fst), Is.Empty,
                        "No RegisterAgent/AgentInbox/role/instruction write is needed for Work progress.")
            assertNoLaunchAgentRecord f f.B launchId)
