module Client.Tests.EmptyServerWorkContinuityTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks
open NUnit.Framework
open Common
open Common.SignalR
open Jarvis.WorkQueues
open Client
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.RoleInstructions
open Client.AccountCapacityLedger
open Client.WorkQueueOperatorConversations
open Client.WorkQueueNativePrivateHostBridge
open Server.Services
open Microsoft.Data.Sqlite

// Disposable Git CAS + real in-memory UserService: no production Work dispatch,
// provider bootstrap, public route, scheduler activation or legacy Agent workflow.
type private Pair = { First: string; Second: string; Bare: string }

type private Authorities =
    { Root: string
      ProjectA: LedgerBinding
      ProjectB: LedgerBinding
      AccountA: AccountBinding
      AccountB: AccountBinding
      TaskId: string
      ReviewId: Guid
      ReviewHead: string
      TaskMessage: string
      TaskMessageOp: Guid
      RoleMessage: string
      HistoricalTree: string
      HistoricalReview: string
      AccountHead: string }

let private ok = function
    | Ok value -> value
    | Error reason -> failwithf "Empty-server disposable fixture failed: %A" reason

let private git directory args =
    use p = new Process()
    p.StartInfo.FileName <- "git"
    p.StartInfo.WorkingDirectory <- directory
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    for arg in args do p.StartInfo.ArgumentList.Add arg
    Assert.That(p.Start(), Is.True, sprintf "Could not start disposable git %A" args)
    let output = p.StandardOutput.ReadToEnd()
    let errors = p.StandardError.ReadToEnd()
    if not (p.WaitForExit(30000)) then
        p.Kill(true)
        Assert.Fail(sprintf "Disposable git timed out: %A" args)
    Assert.That(p.ExitCode, Is.Zero, sprintf "git %A: %s" args errors)
    output.Trim()

let private pair root name =
    let bare = Path.Combine(root, name + ".git")
    let first = Path.Combine(root, name + "-a")
    let second = Path.Combine(root, name + "-b")
    git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
    git root ["clone"; bare; first] |> ignore
    File.WriteAllText(Path.Combine(first, "source.txt"), "disposable " + name)
    git first ["add"; "source.txt"] |> ignore
    git first ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"
               "commit"; "-m"; "disposable original source"] |> ignore
    git first ["push"; "-u"; "origin"; "HEAD"] |> ignore
    git root ["clone"; bare; second] |> ignore
    { First = first; Second = second; Bare = bare }

let private agentKey (name: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes name)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private head (binding: LedgerBinding) = (LedgerStore.fetch binding |> ok).Commit
let private projectState (binding: LedgerBinding) = WorkQueueGitLedger.load binding |> ok |> snd
let private protectedTree (binding: LedgerBinding) =
    git binding.RepositoryPath
        ["ls-tree"; "-r"; head binding; "--"
         "agents"; "records/agent-instructions"; "records/roles"; "notifications"]

let private assertHistory (f: Authorities) binding =
    Assert.That(protectedTree binding, Is.EqualTo(f.HistoricalTree),
                "No old Agent/role/instruction/notification blob OID may change.")
    let _, task, _ = ReviewWorkflow.getTask binding f.TaskId |> ok
    Assert.That(task.TaskId, Is.EqualTo(f.TaskId))
    let review = ReviewWorkflow.getBranchReview binding "_reviews" f.ReviewId |> ok
    Assert.That(review.HeadCommit, Is.EqualTo(f.ReviewHead))
    Assert.That(review.Review.Status, Is.EqualTo("Accepted"))
    let _, doc =
        ReviewWorkflow.getReviewDocument binding "_reviews" f.ReviewId "findings/empty-server-history.md" |> ok
    Assert.That(doc.Content, Is.EqualTo(f.HistoricalReview))
    let taskThread = WorkQueueOperatorConversations.read binding "Task" f.TaskId |> ok
    let roleThread = WorkQueueOperatorConversations.read binding "Role" "Worker" |> ok
    Assert.That(taskThread.Thread.Messages.Length, Is.EqualTo(1))
    Assert.That(roleThread.Thread.Messages.Length, Is.EqualTo(1))
    Assert.That(taskThread.Thread.Messages.[0].Id, Is.EqualTo(f.TaskMessage))
    Assert.That(roleThread.Thread.Messages.[0].Id, Is.EqualTo(f.RoleMessage))
    Assert.That(taskThread.Thread.Messages.[0].Status, Is.EqualTo("Pending"))
    Assert.That(roleThread.Thread.Messages.[0].Status, Is.EqualTo("Pending"))

let private withAuthorities historical action = task {
    let root =
        Path.Combine(Path.GetTempPath(),
                     "jarvis-empty-server-work-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    try
        let p = pair root "original-project"
        let acc = pair root "original-account"
        let projectId, projectLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
        let project path : LedgerBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = p.Bare; ApprovedPushUrl = p.Bare
              ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
        let account path : AccountBinding =
            { RepositoryPath = path; RemoteName = "origin"
              ApprovedFetchUrl = acc.Bare; ApprovedPushUrl = acc.Bare
              AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
        let a, b = project p.First, project p.Second
        let aa, ab = account acc.First, account acc.Second
        LedgerStore.initialize a true |> ok |> ignore
        LedgerStore.fetch b |> ok |> ignore
        WorkQueueGitLedger.initialize a "Original Work" (Guid.NewGuid())
            (Map.ofList ["Worker", set ["planning"]]) |> ok |> ignore
        AccountCapacityLedger.initialize aa 2 |> ok |> ignore
        AccountCapacityLedger.fetch ab |> ok |> ignore
        let taskId = "durable-work-task"
        ReviewWorkflow.createTask a (Guid.NewGuid()) taskId "Original task"
        |> ok |> ignore
        // A pre-existing legacy record is immutable history, never launch authority.
        if historical then
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Old Worker" |> ok |> ignore
            RoleInstructions.setAgentInstructions a "original" (Guid.NewGuid())
                "Old Worker" 0 "Historical instructions never authorize this Work." |> ok |> ignore
        let historicalTree = protectedTree a
        if historical then
            Assert.That(historicalTree, Does.Contain("agents/" + agentKey "Old Worker" + ".json"))
            Assert.That(historicalTree, Does.Contain("records/agent-instructions/"))
        else
            Assert.That(historicalTree, Is.Empty)

        // A pre-restart independent review and two original operator threads.
        let reviewId = Guid.NewGuid()
        let frozenSource = git p.First ["rev-parse"; "HEAD"]
        let originalReview = "# Historical independent verdict\nAccepted before restart."
        ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) "_reviews" "Original Author"
            reviewId "" frozenSource "Original frozen review" "" "" |> ok |> ignore
        ReviewWorkflow.claimBranchReview b (Guid.NewGuid()) "_reviews"
            reviewId "Original Reviewer" 1 |> ok |> ignore
        ReviewWorkflow.putReviewDocument a (Guid.NewGuid()) "_reviews" reviewId
            "Original Reviewer" "findings/empty-server-history.md" "" originalReview |> ok |> ignore
        ReviewWorkflow.decideBranchReview b (Guid.NewGuid()) "_reviews" reviewId
            "Original Reviewer" 2 Accept |> ok |> ignore

        let taskMessage = Guid.NewGuid().ToString("D")
        let taskMessageOp = Guid.NewGuid()
        let roleMessage = Guid.NewGuid().ToString("D")
        WorkQueueOperatorConversations.post a "Operator" taskMessageOp 0
            "Task" taskId taskMessage "Question across server replacement."
        |> ok |> ignore
        WorkQueueOperatorConversations.post b "Operator" (Guid.NewGuid()) 0
            "Role" "Worker" roleMessage "Role inbox survives missing server cache."
        |> ok |> ignore
        let fixture =
            { Root = root; ProjectA = a; ProjectB = b
              AccountA = aa; AccountB = ab; TaskId = taskId
              ReviewId = reviewId; ReviewHead = frozenSource
              TaskMessage = taskMessage; TaskMessageOp = taskMessageOp
              RoleMessage = roleMessage; HistoricalTree = historicalTree
              HistoricalReview = originalReview
              AccountHead = (AccountCapacityLedger.fetch aa |> ok).Head }
        assertHistory fixture b
        do! action fixture
        for clone in [p.First; p.Second; acc.First; acc.Second] do
            Assert.That(git clone ["status"; "--porcelain"], Is.Empty,
                        "Git-ledger operations must leave original source checkouts clean.")
    finally
        if Directory.Exists root then Directory.Delete(root, true)
}

let private stage (f: Authorities) workId =
    { Id = workId; ProjectId = f.ProjectA.ProjectId.ToString("D")
      SubjectId = f.TaskId; Kind = WorkKind.Planning; QueueId = "planning"
      StageGeneration = 1; State = WorkState.Available
      Domain = DomainState.Undecided; Gate = GateState.Clear
      ClaimGeneration = 0; Dependencies = Set.empty; WaivedDependencies = Map.empty
      AvailableAt = 0L; AvailableSequence = 0L
      SourceLineage = None; Push = None; IntegrationAuthorized = false }

let private reservation (f: Authorities) launchId =
    { Id = launchId; ProjectId = f.ProjectA.ProjectId.ToString("D")
      RoleId = "Worker"; RoleVersion = 1
      AccountTokenId = Guid.NewGuid().ToString("D")
      State = LaunchState.ReservedUnregistered; Lease = None
      Version = 0; Credit = None; PrimaryWorkId = None
      EverClaimed = false; AncillaryMessages = Set.empty; DispatchCount = 0 }

let private inert (binding: LedgerBinding) now mutation =
    let state = projectState binding
    WorkQueueGitLedger.executeInertFixtureAt binding "empty-server-test-only"
        (Guid.NewGuid()) state.Version now mutation |> ok |> ignore

let private credential (binding: LedgerBinding) op expectedVersion now launchId bearer action =
    WorkQueueGitLedger.executeCredentialFixtureAt binding "empty-server-test-only"
        op expectedVersion now launchId bearer action

let private register (server: UserService) device connection =
    server.TransportConnected("fixture-user", connection)
    let registration : ClientRegistration =
        { DeviceId = device; DeviceName = device
          ProtocolVersion = AgentProtocol.version; ClientVersion = "fixture-net11"
          SupportedCommands = ["ListProjects"] }
    server.Register("fixture-user", registration, connection) |> ignore

let private idleSchedule () : ScheduleExecutionCommand =
    { ScheduleId = "manager-must-not-launch"; ProjectName = "Original Work"
      TriggerKind = "AfterAgentIdle"; RunAfterSeconds = None; RunAtUtc = None
      RepeatEverySeconds = None; IdleAgentId = Some "Manager"; IdleSeconds = Some 60
      ActionKind = "RunCommand"; Executable = Some "dotnet"; Args = ["--version"]
      WorkingDirectory = None; TimeoutSeconds = Some 10
      MaxOutputBytes = Some 256; WindowRegistration = None; Url = None
      ScriptPath = None; ArgsJson = None; Alias = None; Active = None
      CloseTabAfterExecution = None }

[<TestFixture>]
type EmptyServerWorkContinuityTests() =
    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.ReplacedEmptyServerPreservesGitWorkAndHistoryButNotSessionsOrIdleAuthority(historical) = task {
        do! withAuthorities historical (fun f -> task {
            let serverData = Path.Combine(f.Root, "server-data")
            let schedulePath = Path.Combine(f.Root, "client-owned-schedule.json")
            Directory.CreateDirectory serverData |> ignore
            File.WriteAllText(Path.Combine(serverData, "AGENTS.md"), "Permitted Jarvis-level instructions")
            File.WriteAllText(Path.Combine(serverData, "config.json"), "{}")

            // Real replaceable server routing and SQLite telemetry; neither
            // owns the Work verifier, Git receipt or durable operator messages.
            let serverBefore = UserService()
            register serverBefore "client-a" "old-transport-a"
            register serverBefore "client-b" "old-transport-b"
            Assert.That(serverBefore.ListDevices("fixture-user").Length, Is.EqualTo(2))
            // A genuine disposable SQLite diagnostic file, never an activity
            // watermark or claim source; losing it is explicitly safe here.
            let dbPath = Path.Combine(serverData, "feedback.db")
            do
                use diagnostics = new SqliteConnection("Data Source=" + dbPath)
                diagnostics.Open()
                use cmd = diagnostics.CreateCommand()
                cmd.CommandText <- "CREATE TABLE telemetry (last_seen TEXT NOT NULL);"
                Assert.That(cmd.ExecuteNonQuery(), Is.Zero)
            Assert.That(File.Exists(dbPath), Is.True)

            let workId = "planning-across-empty-server"
            let launchId = Guid.NewGuid().ToString("D")
            let issued = IssuedWorkCredential.Issue launchId
            let bearer = issued.RevealForPrivateDelivery()
            inert f.ProjectA 100L (PublishStage(stage f workId))
            inert f.ProjectB 101L
                (AdmitCredentialProject(reservation f launchId, issued.VerifierSha256))
            inert f.ProjectA 102L (DispatchOnce(launchId, DispatchState.Submitted))
            let connectOp = Guid.NewGuid()
            let preConnectVersion = (projectState f.ProjectA).Version
            let connected =
                credential f.ProjectA connectOp preConnectVersion 103L launchId bearer
                    CredentialWorkAction.FirstConnect |> ok
            Assert.That(connected.AlreadyApplied, Is.False)
            let beforeClaim = projectState f.ProjectB
            credential f.ProjectB (Guid.NewGuid()) beforeClaim.Version 104L launchId bearer
                CredentialWorkAction.ClaimPrimary |> ok |> ignore
            let originalClaim = (projectState f.ProjectA).Works.[workId]
            Assert.That(originalClaim.State, Is.EqualTo(WorkState.Claimed launchId))
            let projectAtRestart = head f.ProjectA
            let originalAccount = (AccountCapacityLedger.fetch f.AccountB |> ok).Head
            Assert.That(originalAccount, Is.EqualTo(f.AccountHead))
            assertHistory f f.ProjectA

            // Replace the complete server-local data directory and in-memory
            // registry. Only permitted instruction/config files are restored.
            Directory.Delete(serverData, true)
            Directory.CreateDirectory serverData |> ignore
            File.WriteAllText(Path.Combine(serverData, "AGENTS.md"), "Permitted Jarvis-level instructions")
            File.WriteAllText(Path.Combine(serverData, "config.json"), "{}")
            Assert.That(Directory.GetFiles(serverData) |> Array.map Path.GetFileName,
                        Is.EquivalentTo(["AGENTS.md"; "config.json"]))
            let serverAfter = UserService()
            match serverAfter.GetSessionByConnection("old-transport-a"),
                  serverAfter.GetSessionByConnection("old-transport-b") with
            | ValueNone, ValueNone -> ()
            | _ -> Assert.Fail("Restarted empty server retained stale transport authority.")
            let orphanRegistration : ClientRegistration =
                { DeviceId = "client-a"; DeviceName = "client-a"
                  ProtocolVersion = AgentProtocol.version
                  ClientVersion = "fixture-net11"; SupportedCommands = [] }
            Assert.Throws<InvalidOperationException>(fun () ->
                serverAfter.Register("fixture-user", orphanRegistration, "old-transport-a") |> ignore)
            |> ignore
            register serverAfter "client-a" "fresh-transport-a"
            register serverAfter "client-b" "fresh-transport-b"
            Assert.That(serverAfter.ListDevices("fixture-user").Length, Is.EqualTo(2))
            register serverAfter "client-a" "duplicate-transport-a"
            match serverAfter.GetSessionByConnection("fresh-transport-a") with
            | ValueNone -> ()
            | _ -> Assert.Fail("Superseded transport remained authorized.")
            match serverAfter.GetSessionByConnection("duplicate-transport-a") with
            | ValueSome _ -> ()
            | _ -> Assert.Fail("New authorized client was not registered.")

            // Both original Git clones re-verify durable state after the
            // relay was emptied. UUID replay must not cause a second connect.
            Assert.That(head f.ProjectB, Is.EqualTo(projectAtRestart))
            Assert.That((AccountCapacityLedger.fetch f.AccountA |> ok).Head,
                        Is.EqualTo(originalAccount))
            let replay =
                credential f.ProjectB connectOp preConnectVersion 103L launchId bearer
                    CredentialWorkAction.FirstConnect |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(head f.ProjectA, Is.EqualTo(projectAtRestart))
            // A fresh operation cannot re-use the pre-connect version; only
            // the exact committed UUID above is a stale-version replay.
            match credential f.ProjectB (Guid.NewGuid()) preConnectVersion
                    105L launchId bearer CredentialWorkAction.FirstConnect with
            | Error _ -> ()
            | other -> Assert.Fail(sprintf "Stale first-connect unexpectedly accepted: %A" other)
            let invalid = IssuedWorkCredential.Issue launchId
            match credential f.ProjectB (Guid.NewGuid()) (projectState f.ProjectB).Version
                    105L launchId (invalid.RevealForPrivateDelivery())
                    (CredentialWorkAction.Checkpoint(workId, originalClaim.ClaimGeneration,
                                                     String.replicate 64 "a")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Foreign credential accepted: %A" other)
            Assert.That(head f.ProjectB, Is.EqualTo(projectAtRestart))

            // A different clone progresses the original Work with the
            // originally issued credential, without any Agent entity.
            let checkpointOp = Guid.NewGuid()
            let beforeCheckpoint = projectState f.ProjectB
            credential f.ProjectB checkpointOp beforeCheckpoint.Version 106L launchId bearer
                (CredentialWorkAction.Checkpoint(workId, originalClaim.ClaimGeneration,
                                                 String.replicate 64 "a")) |> ok |> ignore
            let checkpointHead = head f.ProjectA
            Assert.That(checkpointHead, Is.Not.EqualTo(projectAtRestart))
            let launch = (projectState f.ProjectA).Launches.[launchId]
            inert f.ProjectB 2000L (StopStalled(launchId, launch.Lease.Value.Generation, 500L))
            let stoppedHead = head f.ProjectB
            Assert.That((projectState f.ProjectA).Launches.[launchId].Lease.Value.StopRequested,
                        Is.True)
            match credential f.ProjectA (Guid.NewGuid()) (projectState f.ProjectA).Version
                    2001L launchId bearer
                    (CredentialWorkAction.Checkpoint(workId, originalClaim.ClaimGeneration,
                                                     String.replicate 64 "b")) with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "STOP allowed a new checkpoint: %A" other)
            match credential f.ProjectA (Guid.NewGuid()) (projectState f.ProjectA).Version
                    2002L launchId bearer CredentialWorkAction.FirstConnect with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "STOP accepted a new first-connect: %A" other)
            let oldReceipt =
                credential f.ProjectA checkpointOp beforeCheckpoint.Version 106L launchId bearer
                    (CredentialWorkAction.Checkpoint(workId, originalClaim.ClaimGeneration,
                                                     String.replicate 64 "a")) |> ok
            Assert.That(oldReceipt.AlreadyApplied, Is.True)
            Assert.That(head f.ProjectA, Is.EqualTo(stoppedHead))
            Assert.That(head f.ProjectB, Is.EqualTo(stoppedHead))
            assertHistory f f.ProjectA
            assertHistory f f.ProjectB

            // Even a lost message ACK is recovered by original Git operation
            // UUID. Same UUID with new payload is rejected after restart.
            let replayMessage =
                WorkQueueOperatorConversations.post f.ProjectB "Operator" f.TaskMessageOp 0
                    "Task" f.TaskId f.TaskMessage "Question across server replacement."
                |> ok
            Assert.That(replayMessage.AlreadyApplied, Is.True)
            match WorkQueueOperatorConversations.post f.ProjectB "Operator" f.TaskMessageOp 0
                    "Task" f.TaskId f.TaskMessage "Different answer" with
            | Error OperationIdReused -> ()
            | other -> Assert.Fail(sprintf "Message UUID was reused: %A" other)
            Assert.That(head f.ProjectB, Is.EqualTo(stoppedHead))

            // No SQLite/cache row may prove Manager inactivity. The production
            // client schedule verifier fails closed in this disposable seam.
            let mutable currentTime = DateTimeOffset.UtcNow
            let mutable launches = 0
            let executor (_: BrowserSchedules.ScheduledAction) = task {
                launches <- launches + 1
                return Ok "UNEXPECTED MANAGER LAUNCH"
            }
            use scheduler =
                new BrowserSchedules.Service(schedulePath, executor,
                    now=(fun () -> currentTime), pollMs=25,
                    agentLastSeen=(fun _ _ -> Some(currentTime.AddHours(-2.))))
            Assert.That(scheduler.ScheduleExecution(idleSchedule()).IsOk, Is.True)
            scheduler.Start()
            currentTime <- currentTime.AddHours(2.)
            do! Task.Delay(250)
            Assert.That(launches, Is.Zero)
            Assert.That(scheduler.List(), Does.Contain("ActivityVerificationUnavailable"))
            Assert.That((AccountCapacityLedger.fetch f.AccountA |> ok).Head,
                        Is.EqualTo(f.AccountHead))
            Assert.That(head f.ProjectA, Is.EqualTo(stoppedHead))
            match WorkQueueNativePrivateHostBridge.inspectProduction true with
            | PrivateHostGate.HostBridgeUnavailable missing ->
                Assert.That(missing.Code, Is.EqualTo("HostBridgeUnavailable"))
            | other -> Assert.Fail(sprintf "Fixture must not authorize a real host: %A" other)
        })
    }
