module Client.Tests.LegacyWriterProtocolFenceTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open NUnit.Framework
open Common
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.LegacyWriterProtocolFence

type private OldWriterFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectOne: LedgerBinding
      ProjectTwo: LedgerBinding
      AccountRemote: string
      ProjectRemote: string }

[<TestFixture; NonParallelizable>]
type LegacyWriterProtocolFenceTests() =
    let success = function
        | Ok v -> v
        | Error problem -> failwithf "Unexpected fixture error: %A" problem

    let git directory args =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- directory
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        args |> List.iter p.StartInfo.ArgumentList.Add
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let errors = p.StandardError.ReadToEnd()
        if not (p.WaitForExit(20000)) then
            p.Kill(true)
            failwith "Fixture Git timed out"
        Assert.That(p.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let repository root name =
        let remote = Path.Combine(root, name + ".git")
        let a = Path.Combine(root, name + "-one")
        let b = Path.Combine(root, name + "-two")
        git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
        git root [ "clone"; remote; a ] |> ignore
        git a [ "config"; "user.name"; "WQ10 Fence Test" ] |> ignore
        git a [ "config"; "user.email"; "wq10@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(a, "original.txt"), "historical records must remain intact")
        git a [ "add"; "original.txt" ] |> ignore
        git a [ "commit"; "-m"; "untouched original history" ] |> ignore
        git a [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; remote; b ] |> ignore
        remote, a, b

    let withFixture action =
        let oldHost = Environment.GetEnvironmentVariable LegacyWriterProtocolFence.HostBindingEnvironment
        let root = Path.Combine(Path.GetTempPath(), "wq10-legacy-fence-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        Environment.SetEnvironmentVariable(LegacyWriterProtocolFence.HostBindingEnvironment, null)
        try
            let ar, a1, a2 = repository root "account"
            let pr, p1, p2 = repository root "project"
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let projectId, projectLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let account path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = ar; ApprovedPushUrl = ar
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let project path =
                { RepositoryPath = path
                  RemoteName = "origin"
                  ApprovedFetchUrl = pr; ApprovedPushUrl = pr
                  ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
            let fixture =
                { Root = root; AccountOne = account a1; AccountTwo = account a2
                  ProjectOne = project p1; ProjectTwo = project p2
                  AccountRemote = ar; ProjectRemote = pr }
            AccountCapacityLedger.initialize fixture.AccountOne 3 |> success |> ignore
            LedgerStore.initialize fixture.ProjectOne true |> success |> ignore
            WorkQueueGitLedger.initialize fixture.ProjectOne "Fixture" (Guid.NewGuid())
                (Map.ofList [ "Worker", set [ "source" ] ]) |> success |> ignore
            ReviewWorkflow.registerAgent fixture.ProjectOne (Guid.NewGuid()) "Historical" |> success |> ignore
            action fixture
            Assert.That(git a1 [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git a2 [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git p1 [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git p2 [ "status"; "--porcelain" ], Is.Empty)
        finally
            Environment.SetEnvironmentVariable(LegacyWriterProtocolFence.HostBindingEnvironment, oldHost)
            try Directory.Delete(root, true) with _ -> ()

    let scope fixture account project =
        let cap = AccountCapacityLedger.fetch account |> success
        let source = LedgerStore.fetch project |> success
        { ProjectName = "Fixture"
          Project = project
          Account = account
          ExpectedAccountHead = cap.Head
          ExpectedProjectHead = source.Commit
          ProtocolVersion = LegacyWriterProtocolFence.ProtocolVersion }

    // Fixture-only Git state mutation, never exposed as migration authority:
    // no production Freeze operation or independent signer exists yet.
    let fixtureAccountPhase fixture phase =
        let before = AccountCapacityLedger.fetch fixture.AccountOne |> success
        let path = fixture.AccountOne.RepositoryPath
        git path [ "checkout"; "-B"; "fence-fixture"; before.Head ] |> ignore
        let statePath = Path.Combine(path, "state.json")
        let old = JsonSerializer.Deserialize<AccountCapacityState>(
            File.ReadAllText statePath, JsonSerializerOptions(PropertyNameCaseInsensitive = true))
        Assert.That(old, Is.Not.Null)
        let next =
            { old with MigrationPhase = phase
                       StateVersion = old.StateVersion + 1L
                       NextSequence = old.NextSequence + 1L }
        File.WriteAllText(statePath, JsonSerializer.Serialize next)
        git path [ "add"; "state.json" ] |> ignore
        git path [ "commit"; "-m"; "fixture-only synthetic migration phase" ] |> ignore
        git path [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore
        AccountCapacityLedger.fetch fixture.AccountTwo |> success

    let write action =
        TaskWriteCommand {
            ProjectName = "Fixture"
            Action = action
            OperationId = Guid.NewGuid().ToString("D")
            TaskId = "fixture-task"; AttemptId = Guid.NewGuid().ToString("D")
            ActorId = "ForgedRoleOfManager"; OwnerId = "Worker"
            Title = ""; Html = ""
            ExpectedVersion = 0; ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
            AssignmentGeneration = 0
            Summary = ""; NextAction = ""; Reason = ""
            BaseOid = ""; TargetRef = ""; SourceRef = ""
            AllowDetached = false; AllowUncommitted = false
            DecisionSequence = 0L; Evidence = ""; SessionId = "" }

    let review action =
        ReviewWriteCommand {
            ProjectName = "Fixture"; Action = action
            OperationId = Guid.NewGuid().ToString("D")
            TaskId = "fixture-task"; RequestId = Guid.NewGuid().ToString("D")
            ActorId = "ForgedReviewer"; ReviewerId = "ForgedReviewer"
            ExpectedVersion = 0
            Kind = ""; SourceRef = ""; HeadOid = ""; TargetRef = ""; Summary = ""
            Verdict = "Accepted"; DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }

    let inbox =
        TaskReadCommand {
            ProjectName = "Fixture"; AgentId = "Historical"; Action = "AgentInbox"
            TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
            Cursor = ""; PageSize = 0; BeforeOid = ""; AfterOid = ""; RelativePath = "" }

    let manager =
        ScheduleExecutionCommand {
            ScheduleId = "manager"; ProjectName = "Fixture"; TriggerKind = "AfterAgentIdle"
            RunAfterSeconds = None; RunAtUtc = None; RepeatEverySeconds = None
            IdleAgentId = Some "Manager"; IdleSeconds = Some 900
            ActionKind = "BrowserScript"; Executable = None; Args = []; WorkingDirectory = None
            TimeoutSeconds = None; MaxOutputBytes = None; WindowRegistration = None
            Url = None; ScriptPath = None; ArgsJson = None; Alias = None; Active = Some true
            CloseTabAfterExecution = None }

    let historicalRead =
        TaskReadCommand {
            ProjectName = "Fixture"; AgentId = "Historical"; Action = "GetTask"
            TaskId = "fixture-task"; AssignmentGeneration = 0; CheckpointSequence = 0L
            Cursor = ""; PageSize = 0; BeforeOid = ""; AfterOid = ""; RelativePath = "" }

    let workRead =
        WorkQueueReadCommand { ProjectName = "Fixture"; Action = "Overview"
                               WorkId = ""; Category = "" }

    let workWrite =
        WorkQueueWriteCommand {
            ProjectName = "Fixture"; Action = "CheckpointWork"
            ActorId = "WorkCredentialSubject"; OperationId = Guid.NewGuid().ToString("D")
            LaunchId = Guid.NewGuid().ToString("D"); WorkId = Guid.NewGuid().ToString("D")
            ExpectedProjectVersion = 0; ClaimGeneration = 0
            EvidenceSha256 = "" }

    let assertBlocked scope command =
        match LegacyWriterProtocolFence.decideWithScope scope command with
        | LegacyWriterDecision.Blocked("OldWriterFenced", accountHead, projectHead) ->
            Assert.That(accountHead, Is.EqualTo(scope.ExpectedAccountHead))
            Assert.That(projectHead, Is.EqualTo(scope.ExpectedProjectHead))
        | decision -> Assert.Fail(sprintf "Expected positive verified old-writer block, got %A" decision)

    let assertUnknown scope command =
        match LegacyWriterProtocolFence.decideWithScope scope command with
        | LegacyWriterDecision.Unknown reason ->
            Assert.That(reason, Is.Not.Empty)
        | decision -> Assert.Fail(sprintf "Expected unknown DENY, got %A" decision)

    [<Test>]
    member _.DefaultOffAndVerifiedLegacyActivePreserveOldRegisterWithoutNewLedger() =
        withFixture (fun f ->
            let act = write "RegisterAgent"
            Assert.That(Result.isOk (
                LegacyWriterProtocolFence.checkHost (fun _ -> failwith "No account binding needed when off") act),
                Is.True)
            Assert.That(LegacyWriterProtocolFence.implicitLegacyActivityAllowed
                            (fun _ -> failwith "No trust binding needed when off") "Fixture",
                        Is.True)
            let gate = scope f f.AccountOne f.ProjectOne
            match LegacyWriterProtocolFence.decideWithScope gate act with
            | LegacyWriterDecision.Allowed -> ()
            | other -> Assert.Fail(sprintf "LegacyActive should preserve legacy: %A" other)
            LegacyWriterProtocolFence.protectWithScope gate act (fun () ->
                ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) "OldBeforeFreeze"
                |> Result.mapError (sprintf "%A"))
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentWorkingTurn f.ProjectTwo "OldBeforeFreeze" |> success).AgentId,
                        Is.EqualTo("OldBeforeFreeze")))

    [<Test>]
    member _.TwoIndependentClientsDenyAgentReviewIntegrationManagerAndInboxBeforeGitMutation() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            let before = git f.Root [ "--git-dir=" + f.ProjectRemote; "rev-parse"; "refs/heads/jarvis-orchestration" ]
            let oldAgentHistory = ReviewWorkflow.getAgentWorkingTurn f.ProjectTwo "Historical" |> success
            let commands =
                [ write "RegisterAgent"; write "AssignTask"; write "ReissueAssignment"
                  write "BeginAttempt"; write "YieldAgent"; write "ResumeAgent"
                  write "CheckpointAttempt"; write "CompleteAgentWork"
                  write "AttachRole"; write "SetAgentInstructions"
                  write "SubmitIntegration"; write "CompleteIntegration"
                  review "Submit"; review "Decide"; review "Claim"
                  inbox; manager ]
            let first = scope f f.AccountOne f.ProjectOne
            let second = scope f f.AccountTwo f.ProjectTwo
            for gate in [ first; second ] do
                for command in commands do
                    assertBlocked gate command
                    let mutable ran = false
                    let result =
                        LegacyWriterProtocolFence.protectWithScope gate command (fun () ->
                            ran <- true
                            Ok "unexpected")
                    match result with
                    | Error description -> Assert.That(description, Does.Contain("OldWriterFenced"))
                    | Ok _ -> Assert.Fail("An old writer escaped the guard")
                    Assert.That(ran, Is.False)
                // A normal historical read is not an implicit AgentInbox write.
                Assert.That(LegacyWriterProtocolFence.decideWithScope gate historicalRead,
                            Is.EqualTo(LegacyWriterDecision.Allowed))
                Assert.That(LegacyWriterProtocolFence.decideWithScope gate workRead,
                            Is.EqualTo(LegacyWriterDecision.Allowed))
                Assert.That(LegacyWriterProtocolFence.decideWithScope gate workWrite,
                            Is.EqualTo(LegacyWriterDecision.Allowed))
                let mutable workEntered = false
                let workResult = LegacyWriterProtocolFence.protectWithScope gate workWrite (fun () -> workEntered <- true; Ok ())
                Assert.That(Result.isOk workResult && workEntered, Is.True)
            let after = git f.Root [ "--git-dir=" + f.ProjectRemote; "rev-parse"; "refs/heads/jarvis-orchestration" ]
            Assert.That(after, Is.EqualTo before)
            Assert.That((ReviewWorkflow.getAgentWorkingTurn f.ProjectTwo "Historical" |> success).AgentId,
                        Is.EqualTo(oldAgentHistory.AgentId)))

    [<Test>]
    member _.PreparingProtocolHeadAndProjectEpochMismatchFailClosedAcrossClients() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            let frozen = scope f f.AccountTwo f.ProjectTwo
            let next = fixtureAccountPhase f "Preparing"
            Assert.That(next.State.MigrationPhase, Is.EqualTo "Preparing")
            assertUnknown frozen (write "RegisterAgent")
            let preparing = scope f f.AccountTwo f.ProjectTwo
            assertBlocked preparing (review "Decide")
            assertUnknown { preparing with ProtocolVersion = 2 } (write "AssignTask")
            assertUnknown { preparing with ExpectedAccountHead = String('a', 40) } (write "RegisterAgent")
            assertUnknown { preparing with ExpectedProjectHead = String('b', 40) } inbox
            assertUnknown { preparing with ProjectName = "NotFixture" } (write "SubmitIntegration")
            let noProof = { preparing with Project = { preparing.Project with ProjectId = Guid.NewGuid() } }
            assertUnknown noProof manager)

    [<Test>]
    member _.OperatorRecoveryAndNewWorkRemainIndependentWithoutReplayOfOldIntent() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            let gate = scope f f.AccountTwo f.ProjectTwo
            let recovery =
                AbandonPendingIntentCommand {
                    ProjectName = "Fixture"; OperationId = Guid.NewGuid().ToString("D")
                    OperatorId = "AuthenticatedUser"; ExpectedCommandSha256 = String('a', 64)
                    ExpectedVerifiedHead = gate.ExpectedProjectHead; Reason = "reconciled" }
            Assert.That(LegacyWriterProtocolFence.decideWithScope gate recovery,
                        Is.EqualTo(LegacyWriterDecision.Allowed))
            let replay =
                RetryPendingIntentCommand {
                    ProjectName = "Fixture"; OperationId = Guid.NewGuid().ToString("D")
                    OperatorId = "AuthenticatedUser"; ExpectedCommandSha256 = String('a', 64)
                    ExpectedVerifiedHead = gate.ExpectedProjectHead }
            assertBlocked gate replay
            Assert.That(LegacyWriterProtocolFence.decideWithScope gate workRead,
                        Is.EqualTo(LegacyWriterDecision.Allowed)))

    [<Test>]
    member _.EnabledHostBoundaryFetchesFreshTrustedHeadsAndRefusesForgedIdentity() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            let gate = scope f f.AccountTwo f.ProjectTwo
            let config = Path.Combine(f.Root, "host-fence.json")
            let document =
                {| schemaVersion = 1; protocolVersion = gate.ProtocolVersion
                   projectName = gate.ProjectName
                   projectRepositoryPath = gate.Project.RepositoryPath
                   projectRemoteName = gate.Project.RemoteName
                   projectApprovedFetchUrl = gate.Project.ApprovedFetchUrl
                   projectApprovedPushUrl = gate.Project.ApprovedPushUrl
                   projectId = string gate.Project.ProjectId
                   projectLedgerId = string gate.Project.LedgerId
                   projectEpoch = gate.Project.Epoch
                   accountRepositoryPath = gate.Account.RepositoryPath
                   accountRemoteName = gate.Account.RemoteName
                   accountApprovedFetchUrl = gate.Account.ApprovedFetchUrl
                   accountApprovedPushUrl = gate.Account.ApprovedPushUrl
                   accountId = string gate.Account.AccountId
                   accountLedgerId = string gate.Account.LedgerId
                   accountEpoch = gate.Account.Epoch
                   expectedAccountHead = gate.ExpectedAccountHead
                   expectedProjectHead = gate.ExpectedProjectHead |}
            File.WriteAllText(config, JsonSerializer.Serialize document)
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(config, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            Environment.SetEnvironmentVariable(LegacyWriterProtocolFence.HostBindingEnvironment, config)
            let trusted name = if name = "Fixture" then Ok f.ProjectTwo else Error "not bound"
            match LegacyWriterProtocolFence.checkHost trusted (write "RegisterAgent") with
            | Error reason -> Assert.That(reason, Does.Contain("OldWriterFenced"))
            | Ok() -> Assert.Fail("A forged Manager label bypassed the enabled host fence")
            Assert.That(Result.isOk (LegacyWriterProtocolFence.checkHost trusted historicalRead),
                        Is.True)
            Assert.That(LegacyWriterProtocolFence.implicitLegacyActivityAllowed trusted "Fixture",
                        Is.False, "Historical reads may not activate Ready Agent or consume old response alerts")
            match LegacyWriterProtocolFence.checkHost (fun _ -> Ok { f.ProjectTwo with LedgerId = Guid.NewGuid() }) (write "AssignTask") with
            | Error reason -> Assert.That(reason, Does.Contain("OldWriterFenceUnknown"))
            | Ok() -> Assert.Fail("Untrusted host project authority escaped the fence")
            let newAccount = fixtureAccountPhase f "Preparing"
            match LegacyWriterProtocolFence.checkHost trusted (review "Decide") with
            | Error reason -> Assert.That(reason, Does.Contain("AccountEpochHeadChanged"))
            | Ok() -> Assert.Fail("Stale host account head was accepted")
            Assert.That(newAccount.Head, Is.Not.EqualTo(gate.ExpectedAccountHead)))
