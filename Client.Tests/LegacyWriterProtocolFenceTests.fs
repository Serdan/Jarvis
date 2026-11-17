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
open Client.RoleInstructions
open Client.ChallengeWorkflow
open Client.LegacyDirectWriterFence
open Client.LegacyWriterProtocolFence
open Client.OriginalGitWriterAdmissionJournal

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
        let oldJournal = Environment.GetEnvironmentVariable OriginalGitWriterAdmissionJournal.JournalEnvironment
        let root = Path.Combine(Path.GetTempPath(), "wq10-legacy-fence-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        Environment.SetEnvironmentVariable(LegacyWriterProtocolFence.HostBindingEnvironment, null)
        Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, null)
        OriginalGitWriterAdmissionJournal.setDisposableBindingRoot (Some root)
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
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, oldJournal)
            OriginalGitWriterAdmissionJournal.setDisposableBindingRoot None
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

    let activateHost f (gate: LegacyFenceScope) =
        let config = Path.Combine(f.Root, "direct-host-fence.json")
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

    let expectDirectFenced (actual: Result<unit, LedgerFailure>) =
        match actual with
        | Error(InvalidBinding reason) ->
            Assert.That(reason, Does.StartWith("OldWriterFenced"), reason)
        | other -> Assert.Fail(sprintf "Expected direct original writer denial, got %A" other)

    let expectDirectUnknown expected (actual: Result<unit, LedgerFailure>) =
        match actual with
        | Error(InvalidBinding reason) ->
            Assert.That(reason, Does.Contain(expected), reason)
        | other -> Assert.Fail(sprintf "Expected original epoch unknown/deny, got %A" other)

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
            LaunchId = Guid.NewGuid().ToString("D"); Credential = ""
            WorkId = Guid.NewGuid().ToString("D")
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

    [<Test>]
    member _.DirectLegacyWritersRemainCompatibleWhenOffOrAccountLegacyActive() =
        withFixture (fun f ->
            ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) "DirectBeforeOptIn"
            |> success |> ignore
            let gate = scope f f.AccountTwo f.ProjectTwo
            activateHost f gate
            ReviewWorkflow.registerAgent f.ProjectTwo (Guid.NewGuid()) "DirectBeforeFreeze"
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentWorkingTurn f.ProjectOne "DirectBeforeFreeze"
                         |> success).AgentId, Is.EqualTo("DirectBeforeFreeze")))

    [<Test>]
    member _.FrozenDirectAgentRoleAndChallengeWritersAreDeniedOnBothOriginalClones() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            let gate = scope f f.AccountTwo f.ProjectTwo
            activateHost f gate
            let before =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "rev-parse"; "refs/heads/jarvis-orchestration" ]
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "PostFreeze"
                |> Result.map ignore |> expectDirectFenced
                RoleInstructions.createRole binding "AuthenticatedUser" (Guid.NewGuid())
                    "GuardRole" "Purpose" "Role instruction body"
                |> Result.map ignore |> expectDirectFenced
                ChallengeWorkflow.ensureOperatorIndex binding
                |> Result.map ignore |> expectDirectFenced
                Assert.That((ReviewWorkflow.getAgentWorkingTurn binding "Historical" |> success).AgentId,
                            Is.EqualTo("Historical"))
            let after =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "rev-parse"; "refs/heads/jarvis-orchestration" ]
            Assert.That(after, Is.EqualTo(before), "Frozen direct writers must not move the original ref")
            let observation = LegacyDirectWriterFence.inspectHighWater f.ProjectTwo |> success
            Assert.That(observation.AccountHead, Is.EqualTo(gate.ExpectedAccountHead))
            Assert.That(observation.ProjectHead, Is.EqualTo(gate.ExpectedProjectHead))
            Assert.That(observation.ProjectOperationHighWater, Is.GreaterThanOrEqualTo(0L))
            Assert.That(observation.OriginalPendingJournal, Does.StartWith("Unknown:"))
            Assert.That(observation.OriginalSourceEffectJournal, Does.StartWith("Unknown:"))
            Assert.That(observation.MinimumClientProtocol, Does.StartWith("Unknown:")))

    [<Test>]
    member _.FrozenDirectReplayAndStaleAccountProjectHeadsFailClosed() =
        withFixture (fun f ->
            let originalId = Guid.NewGuid()
            ReviewWorkflow.registerAgent f.ProjectOne originalId "AlreadyAppliedBeforeFreeze"
            |> success |> ignore
            fixtureAccountPhase f "Frozen" |> ignore
            let frozen = scope f f.AccountTwo f.ProjectTwo
            activateHost f frozen
            let before =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "rev-parse"; "refs/heads/jarvis-orchestration" ]
            ReviewWorkflow.registerAgent f.ProjectTwo originalId "AlreadyAppliedBeforeFreeze"
            |> Result.map ignore |> expectDirectFenced
            let mismatch = { f.ProjectTwo with LedgerId = Guid.NewGuid() }
            let projectSnapshot = LedgerStore.fetch f.ProjectTwo |> success
            match LegacyDirectWriterFence.decideAt frozen mismatch projectSnapshot with
            | LegacyWriterDecision.Unknown "DirectProjectAuthorityMismatch" -> ()
            | other -> Assert.Fail(sprintf "Wrong bound ledger was trusted: %A" other)
            fixtureAccountPhase f "Preparing" |> ignore
            ReviewWorkflow.registerAgent f.ProjectTwo (Guid.NewGuid()) "OldAccountHead"
            |> Result.map ignore |> expectDirectUnknown "AccountEpochHeadChanged"
            let preparing = scope f f.AccountTwo f.ProjectTwo
            activateHost f preparing
            // This is a Work-native fixture record, not an old Agent write.
            let intent =
                { OperationId = Guid.NewGuid(); Actor = "WorkNativeFixture"
                  Kind = "WorkNativeDrift"; RequestJson = "{}" }
            LedgerStore.transact f.ProjectOne intent (fun _ ->
                Ok [ { Path = "records/wq10-fixture/direct-head-drift.json"
                       ExpectedBlobOid = None; Content = "{}" } ])
            |> success |> ignore
            ReviewWorkflow.registerAgent f.ProjectTwo (Guid.NewGuid()) "OldProjectHead"
            |> Result.map ignore |> expectDirectUnknown "ProjectEpochHeadChanged"
            let after =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "rev-parse"; "refs/heads/jarvis-orchestration" ]
            Assert.That(after, Is.Not.EqualTo(before))
            Assert.That((ReviewWorkflow.getAgentWorkingTurn f.ProjectTwo "Historical"
                         |> success).AgentId, Is.EqualTo("Historical")))

    [<Test>]
    member _.CompetingProjectGitCASRevalidatesAccountPhaseBeforeLegacyRetry() =
        withFixture (fun f ->
            let active = scope f f.AccountTwo f.ProjectTwo
            activateHost f active
            let mutable planned = 0
            let legacyIntent =
                { OperationId = Guid.NewGuid(); Actor = "OldClient"
                  Kind = "DirectLegacyMutation"; RequestJson = "{}" }
            let result =
                LegacyDirectWriterFence.transact f.ProjectOne legacyIntent (fun _ ->
                    planned <- planned + 1
                    if planned = 1 then
                        // A second client advances the original project CAS and
                        // the original account becomes Frozen during this plan.
                        fixtureAccountPhase f "Frozen" |> ignore
                        let other =
                            { OperationId = Guid.NewGuid(); Actor = "WorkNativeFixture"
                              Kind = "CompetingProjectWrite"; RequestJson = "{}" }
                        LedgerStore.transact f.ProjectTwo other (fun _ ->
                            Ok [ { Path = "records/wq10-fixture/competing-cas.json"
                                   ExpectedBlobOid = None; Content = "{}" } ])
                        |> success |> ignore
                    Ok [ { Path = "records/wq10-fixture/old-client-escaped.json"
                           ExpectedBlobOid = None; Content = "{}" } ])
            result |> Result.map ignore |> expectDirectUnknown "AccountEpochHeadChanged"
            Assert.That(planned, Is.EqualTo(1), "Stale planner must not run again after account freeze")
            let latest = LedgerStore.fetch f.ProjectTwo |> success
            let old, _ = LedgerStore.readAt f.ProjectTwo latest
                             "records/wq10-fixture/old-client-escaped.json" |> success
            Assert.That(old.IsNone, Is.True, "CAS losing original Agent write must not publish"))


    // RED: STOP racing a planner that already admitted the original writer
    // must not turn a pending remote effect into a quiescence assertion.
    [<Test>]
    member _.AccountFreezeBetweenOriginalPlannerAndPushMustNotProduceFalseQuiescence() =
        withFixture (fun f ->
            let journalPath = Path.Combine(f.Root, "original-stop-race-"+Guid.NewGuid().ToString("N")+".db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal journalPath
            let previousJournal = Environment.GetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment)
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, journalPath)
            try
                activateHost f (scope f f.AccountTwo f.ProjectTwo)
                let originalHead =
                    git f.Root [ "--git-dir=" + f.ProjectRemote
                                 "rev-parse"; "refs/heads/jarvis-orchestration" ]
                let mutable stopped = false
                let id = Guid.NewGuid()
                let original =
                    { OperationId = id; Actor = "OldClient"
                      Kind = "OriginalWriterStopRace"; RequestJson = "{}" }
                let outcome =
                    LedgerStore.withGitTransportFaults
                        (fun phase arguments ->
                            if not stopped && phase = "publication-push"
                               && (arguments |> List.tryHead) = Some "push" then
                                stopped <- true
                                fixtureAccountPhase f "Frozen" |> ignore
                                OriginalGitWriterAdmissionJournal.stop journalPath |> success |> ignore
                            None)
                        (fun () ->
                            LegacyDirectWriterFence.transact f.ProjectOne original (fun _ ->
                                Ok [ { Path = "records/wq10-fixture/stop-race-escaped.json"
                                       ExpectedBlobOid = None; Content = "{}" } ]))
                Assert.That(stopped, Is.True, "Fixture must freeze after legacy planning")
                let latest = LedgerStore.fetch f.ProjectTwo |> success
                let escaped, _ =
                    LedgerStore.readAt f.ProjectTwo latest
                        "records/wq10-fixture/stop-race-escaped.json" |> success
                if escaped.IsSome then
                    // A pre-STOP admission may finish after STOP, but cannot be
                    // mistaken for quiescence without durable operation coverage.
                    activateHost f (scope f f.AccountTwo f.ProjectTwo)
                    let witness = LegacyDirectWriterFence.inspectHighWater f.ProjectTwo |> success
                    let journalWitness = witness.OriginalWriterBoundary |> Option.get
                    Assert.That(journalWitness.Journal.HighWater, Is.GreaterThan(0L))
                    Assert.That(journalWitness.Journal.AdmissionClosed, Is.True)
                    Assert.That(journalWitness.Journal.RouteCoverageProven, Is.False)
                    Assert.That(journalWitness.Journal.SettledHighWater, Is.EqualTo(journalWitness.Journal.HighWater))
                    Assert.That(witness.OriginalPendingJournal, Does.Not.StartWith("Unknown:"),
                                "A pre-STOP operation published without a complete durable journal")
                else
                    Assert.That(latest.Commit, Is.EqualTo(originalHead))
                    match outcome with
                    | Error _ -> ()
                    | Ok _ -> Assert.Fail("Operation returned success without publishing or settlement")
            finally
                Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, previousJournal))

    [<Test>]
    member _.FrozenDirectReviewCannotPublishOrphanSourceRefBeforeLedgerCAS() =
        withFixture (fun f ->
            fixtureAccountPhase f "Frozen" |> ignore
            activateHost f (scope f f.AccountTwo f.ProjectTwo)
            let request = Guid.NewGuid()
            let sourceCommit = git f.ProjectOne.RepositoryPath [ "rev-parse"; "HEAD" ]
            let managed = $"refs/heads/jarvis-review/_reviews/{request:N}/r1"
            let before =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "for-each-ref"; "--format=%(refname)"; "refs/heads/jarvis-review" ]
            ReviewWorkflow.submitBranchReview f.ProjectOne (Guid.NewGuid()) "_reviews"
                "Historical" request "" sourceCommit "Frozen source request" "" ""
            |> Result.map ignore |> expectDirectFenced
            let after =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "for-each-ref"; "--format=%(refname)"; "refs/heads/jarvis-review" ]
            Assert.That(after, Is.EqualTo(before), "Frozen review must not publish orphan Git refs")
            Assert.That(after.Contains(managed, StringComparison.Ordinal), Is.False))

    // RED: an independent immutable source ref is a remote effect even when
    // its later ledger transaction is rejected by a newly frozen account.
    [<Test>]
    member _.SourceReviewRefPushRacingAccountStopNeedsDurableEffectCoverage() =
        withFixture (fun f ->
            let journalPath = Path.Combine(f.Root, "original-stop-race-"+Guid.NewGuid().ToString("N")+".db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal journalPath
            let previousJournal = Environment.GetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment)
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, journalPath)
            try
                activateHost f (scope f f.AccountTwo f.ProjectTwo)
                let request = Guid.NewGuid()
                let sourceHead = git f.ProjectOne.RepositoryPath [ "rev-parse"; "HEAD" ]
                let managed = $"refs/heads/jarvis-review/_reviews/{request:N}/r1"
                let mutable stopped = false
                let outcome =
                    LedgerStore.withGitTransportFaults
                        (fun _ args ->
                            if not stopped && (args |> List.tryHead) = Some "push"
                               && (args |> List.exists (fun a -> a.Contains(managed, StringComparison.Ordinal))) then
                                stopped <- true
                                fixtureAccountPhase f "Frozen" |> ignore
                                OriginalGitWriterAdmissionJournal.stop journalPath |> success |> ignore
                            None)
                        (fun () ->
                            ReviewWorkflow.submitBranchReview f.ProjectOne (Guid.NewGuid())
                                "_reviews" "Historical" request "" sourceHead
                                "Racing source review" "" "")
                Assert.That(stopped, Is.True, "Fixture must stop at the source ref push")
                let sourceRefs =
                    git f.Root [ "--git-dir=" + f.ProjectRemote
                                 "for-each-ref"; "--format=%(refname)"; managed ]
                if sourceRefs.Contains(managed, StringComparison.Ordinal) then
                    activateHost f (scope f f.AccountTwo f.ProjectTwo)
                    let witness = LegacyDirectWriterFence.inspectHighWater f.ProjectTwo |> success
                    let journalWitness = witness.OriginalWriterBoundary |> Option.get
                    Assert.That(journalWitness.Journal.HighWater, Is.GreaterThan(0L))
                    Assert.That(journalWitness.Journal.AdmissionClosed, Is.True)
                    Assert.That(journalWitness.Journal.RouteCoverageProven, Is.False)
                    Assert.That(journalWitness.Journal.SettledHighWater, Is.EqualTo(journalWitness.Journal.HighWater))
                    Assert.That(witness.OriginalSourceEffectJournal, Does.Not.StartWith("Unknown:"),
                                "A remote source ref was published without a durable source-effect journal")
                else
                    match outcome with
                    | Error _ -> ()
                    | Ok _ -> Assert.Fail("Source ref command acknowledged without a published ref")
            finally
                Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, previousJournal))

    [<Test>]
    member _.OriginalWriterJournalStopPreservesInflightAndFencesNewAdmissions() =
        withFixture (fun f ->
            let path = Path.Combine(f.Root, "original-writer-admission.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal path
            let prior =
                Environment.GetEnvironmentVariable(
                    OriginalGitWriterAdmissionJournal.JournalEnvironment)
            Environment.SetEnvironmentVariable(
                OriginalGitWriterAdmissionJournal.JournalEnvironment, path)
            try
                let authority = String.replicate 64 "a"
                let first = Guid.NewGuid()
                let permit =
                    OriginalGitWriterAdmissionJournal.admit first "ProjectCAS"
                        authority "refs/heads/jarvis-orchestration" "immutable-request"
                    |> success |> Option.get
                OriginalGitWriterAdmissionJournal.markAttempt permit |> success
                let stopping = OriginalGitWriterAdmissionJournal.stop path |> success
                Assert.That(stopping.AdmissionClosed, Is.True)
                Assert.That(stopping.PendingOrUnknown.Length, Is.EqualTo(1))
                Assert.That(stopping.PendingOrUnknown.Head.Status, Is.EqualTo("Attempted"))
                Assert.That(stopping.RouteCoverageProven, Is.False,
                            "A journal alone is not exhaustive writer-route proof")
                match OriginalGitWriterAdmissionJournal.admit (Guid.NewGuid()) "ProjectCAS"
                          authority "refs/heads/jarvis-orchestration" "after-stop" with
                | Error "OriginalWriterAdmissionClosed" -> ()
                | other -> Assert.Fail(sprintf "STOP admitted a new writer: %A" other)
                OriginalGitWriterAdmissionJournal.settle permit true true |> success
                let settled = OriginalGitWriterAdmissionJournal.inspect path |> success
                Assert.That(settled.PendingOrUnknown, Is.Empty)
                Assert.That(settled.AllRecordedEffectsSettled, Is.True)
                Assert.That(settled.RouteCoverageProven, Is.False)
            finally
                Environment.SetEnvironmentVariable(
                    OriginalGitWriterAdmissionJournal.JournalEnvironment, prior))

    [<Test>]
    member _.OriginalWriterJournalUnknownRemoteEffectSurvivesReinspection() =
        withFixture (fun f ->
            let path = Path.Combine(f.Root, "original-writer-unknown.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal path
            let prior =
                Environment.GetEnvironmentVariable(
                    OriginalGitWriterAdmissionJournal.JournalEnvironment)
            Environment.SetEnvironmentVariable(
                OriginalGitWriterAdmissionJournal.JournalEnvironment, path)
            try
                let authority = String.replicate 64 "b"
                let operation = Guid.NewGuid()
                let permit =
                    OriginalGitWriterAdmissionJournal.admit operation "ReviewRef"
                        authority "refs/heads/jarvis-review/example" "source-publish"
                    |> success |> Option.get
                OriginalGitWriterAdmissionJournal.markAttempt permit |> success
                OriginalGitWriterAdmissionJournal.settle permit false true |> success
                let reopened = OriginalGitWriterAdmissionJournal.inspect path |> success
                Assert.That(reopened.PendingOrUnknown.Length, Is.EqualTo(1))
                Assert.That(reopened.PendingOrUnknown.Head.Status, Is.EqualTo("Unknown"))
                match OriginalGitWriterAdmissionJournal.admit operation "ReviewRef"
                          authority "refs/heads/jarvis-review/example" "source-publish" with
                | Error reason -> Assert.That(reason, Does.Contain("RequiresReconciliation"))
                | Ok _ -> Assert.Fail("UNKNOWN remote effect was re-admitted")
            finally
                Environment.SetEnvironmentVariable(
                    OriginalGitWriterAdmissionJournal.JournalEnvironment, prior))

    [<Test>]
    member _.FourConcreteDirectReplayFastPathsRespectFrozenPreparingAndInvalidHostProof() =
        withFixture (fun f ->
            let sourceHead = git f.ProjectOne.RepositoryPath [ "rev-parse"; "HEAD" ]
            let targetRef = "refs/heads/main"
            let reviewId = Guid.NewGuid()
            let reviewSubmitId = Guid.NewGuid()
            let reviewReplaceId = Guid.NewGuid()
            let reviewSubmit binding =
                ReviewWorkflow.submitBranchReview binding reviewSubmitId "_reviews" "Historical"
                    reviewId "" sourceHead "Original legacy review" "" ""
            let reviewReplace binding =
                ReviewWorkflow.replaceBranchReview binding reviewReplaceId "_reviews"
                    "Historical" reviewId 1 "" sourceHead "Replaced legacy review"
            Assert.That((reviewSubmit f.ProjectOne |> success).AlreadyApplied, Is.False)
            Assert.That((reviewReplace f.ProjectTwo |> success).AlreadyApplied, Is.False)

            let taskId = "wq10-direct-replay"
            ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) "Owner"
            |> success |> ignore
            ReviewWorkflow.registerAgent f.ProjectOne (Guid.NewGuid()) "Reviewer"
            |> success |> ignore
            ReviewWorkflow.createTask f.ProjectOne (Guid.NewGuid()) taskId "Direct replay"
            |> success |> ignore
            let acceptedReviewId = Guid.NewGuid()
            ReviewWorkflow.submitBranchReview f.ProjectOne (Guid.NewGuid()) taskId
                "Owner" acceptedReviewId "" sourceHead "Accepted integration proof" "" targetRef
            |> success |> ignore
            ReviewWorkflow.claimBranchReview f.ProjectTwo (Guid.NewGuid()) taskId
                acceptedReviewId "Reviewer" 1
            |> success |> ignore
            ReviewWorkflow.decideBranchReview f.ProjectOne (Guid.NewGuid()) taskId
                acceptedReviewId "Reviewer" 2 Accept
            |> success |> ignore

            let submissionId = Guid.NewGuid()
            let integrationSubmitId = Guid.NewGuid()
            let integrationCompleteId = Guid.NewGuid()
            let integrationSubmit binding =
                ReviewWorkflow.submitIntegration binding integrationSubmitId taskId "Owner"
                    submissionId "" "" (acceptedReviewId.ToString("N")) targetRef
            let integrationComplete binding =
                ReviewWorkflow.completeIntegration binding integrationCompleteId taskId "Reviewer"
                    submissionId sourceHead
            Assert.That((integrationSubmit f.ProjectTwo |> success).AlreadyApplied, Is.False)
            Assert.That((integrationComplete f.ProjectOne |> success).AlreadyApplied, Is.False)

            let replay binding =
                [ "submit review", reviewSubmit binding
                  "replace review", reviewReplace binding
                  "submit integration", integrationSubmit binding
                  "complete integration", integrationComplete binding ]
            // A disabled fence and an enabled original LegacyActive epoch must
            // retain the exact prior idempotent receipts from both real clones.
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for name, outcome in replay binding do
                    Assert.That((success outcome).AlreadyApplied, Is.True, name)
            activateHost f (scope f f.AccountTwo f.ProjectTwo)
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for name, outcome in replay binding do
                    Assert.That((success outcome).AlreadyApplied, Is.True, name)

            fixtureAccountPhase f "Frozen" |> ignore
            let frozen = scope f f.AccountTwo f.ProjectTwo
            activateHost f frozen
            let projectHead () =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "rev-parse"; "refs/heads/jarvis-orchestration" ]
            let managedRefs () =
                git f.Root [ "--git-dir=" + f.ProjectRemote
                             "for-each-ref"; "--format=%(refname) %(objectname)"
                             "refs/heads/jarvis-review"; "refs/heads/jarvis-integration" ]
            let originalHead, originalRefs = projectHead (), managedRefs ()
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for _, outcome in replay binding do
                    outcome |> Result.map ignore |> expectDirectFenced

            fixtureAccountPhase f "Preparing" |> ignore
            // The host's old account head must not authorize a replay.
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for _, outcome in replay binding do
                    outcome |> Result.map ignore
                    |> expectDirectUnknown "AccountEpochHeadChanged"
            activateHost f (scope f f.AccountTwo f.ProjectTwo)
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for _, outcome in replay binding do
                    outcome |> Result.map ignore |> expectDirectFenced

            let forged = { f.ProjectTwo with LedgerId = Guid.NewGuid() }
            for _, outcome in replay forged do
                outcome |> Result.map ignore
                |> expectDirectUnknown "DirectProjectAuthorityMismatch"
            Environment.SetEnvironmentVariable(LegacyWriterProtocolFence.HostBindingEnvironment,
                                               Path.Combine(f.Root, "missing-host-binding.json"))
            for binding in [ f.ProjectOne; f.ProjectTwo ] do
                for _, outcome in replay binding do
                    outcome |> Result.map ignore |> expectDirectUnknown "OldWriterFenceUnknown"

            Assert.That(projectHead (), Is.EqualTo originalHead,
                        "Old UUID replays may not move the original project ledger ref")
            Assert.That(managedRefs (), Is.EqualTo originalRefs,
                        "Old UUID replays may not publish source refs"))

    [<Test>]
    member _.ConfiguredOriginalWriterJournalCoversProjectAccountAndSourceAndStop() =
        withFixture (fun f ->
            let path = Path.Combine(f.Root, "original-live-routes.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal path
            let oldJournal =
                Environment.GetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment)
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, path)
            try
                let accountOp = Guid.NewGuid()
                AccountCapacityLedger.setCapacity f.AccountOne accountOp "Operator" 1L 4
                |> success |> ignore
                let projectOp =
                    { OperationId = Guid.NewGuid(); Actor = "Operator"
                      Kind = "OriginalProjectMutation"; RequestJson = "{}" }
                let originalWrite =
                    LedgerStore.transact f.ProjectOne projectOp (fun _ ->
                        Ok [ { Path = "records/old-writer-test/positive.json"
                               ExpectedBlobOid = None; Content = "{}" } ])
                originalWrite |> success |> ignore
                let sourceHead = git f.ProjectOne.RepositoryPath [ "rev-parse"; "HEAD" ]
                let sourceRef =
                    $"refs/heads/jarvis-review/_reviews/{Guid.NewGuid():N}/r1"
                LedgerStore.publishReviewRef f.ProjectOne sourceRef sourceHead |> success
                let integrationRef =
                    $"refs/heads/jarvis-integration/{Guid.NewGuid():N}"
                LedgerStore.publishIntegrationRef f.ProjectOne integrationRef sourceHead |> success
                let deleted = LedgerStore.deleteReservedReviewRef f.ProjectOne sourceRef sourceHead |> success
                Assert.That(deleted, Is.EqualTo("Deleted"))
                let retainedRef =
                    $"refs/heads/jarvis-review/_reviews/{Guid.NewGuid():N}/r1"
                LedgerStore.publishReviewRef f.ProjectOne retainedRef sourceHead |> success
                let observed = OriginalGitWriterAdmissionJournal.inspect path |> success
                Assert.That(observed.HighWater, Is.GreaterThanOrEqualTo(6L))
                Assert.That(observed.SettledHighWater, Is.EqualTo(observed.HighWater))
                Assert.That(observed.PendingOrUnknown, Is.Empty)
                Assert.That(observed.RouteCoverageProven, Is.False)
                activateHost f (scope f f.AccountTwo f.ProjectTwo)
                let scoped = LegacyDirectWriterFence.inspectHighWater f.ProjectTwo |> success
                let boundary = scoped.OriginalWriterBoundary |> Option.get
                Assert.That(boundary.AccountHead, Is.EqualTo(scoped.AccountHead))
                Assert.That(boundary.ProjectHead, Is.EqualTo(scoped.ProjectHead))
                Assert.That(boundary.Journal.Epoch, Is.EqualTo(1L))
                Assert.That(boundary.Journal.RouteCoverageProven, Is.False)
                Assert.That(scoped.OriginalPendingJournal, Does.StartWith("ObservedOnly/NO-GO:"))
                let stopping = OriginalGitWriterAdmissionJournal.stop path |> success
                Assert.That(stopping.AdmissionClosed, Is.True)
                Assert.That(stopping.PendingOrUnknown, Is.Empty)
                Assert.That(
                    AccountCapacityLedger.setCapacity f.AccountTwo (Guid.NewGuid()) "Operator" 2L 5
                    |> Result.isError, Is.True, "STOP must close account Git CAS")
                let projectPostStop =
                    { projectOp with OperationId = Guid.NewGuid()
                                     Kind = "ProjectAfterStop" }
                let rejectedProject =
                    LedgerStore.transact f.ProjectTwo projectPostStop (fun _ ->
                        Ok [ { Path = "records/old-writer-test/denied.json"
                               ExpectedBlobOid = None; Content = "{}" } ])
                Assert.That(Result.isError rejectedProject, Is.True,
                            "STOP must close project Git CAS")
                let afterRef =
                    $"refs/heads/jarvis-review/_reviews/{Guid.NewGuid():N}/r1"
                Assert.That(
                    LedgerStore.publishReviewRef f.ProjectTwo afterRef sourceHead
                    |> Result.isError, Is.True, "STOP must close independent source publishing")
                Assert.That(
                    LedgerStore.deleteReservedReviewRef f.ProjectTwo retainedRef sourceHead
                    |> Result.isError, Is.True, "STOP must also close independent source deletion")
                let after = OriginalGitWriterAdmissionJournal.inspect path |> success
                Assert.That(after.HighWater, Is.EqualTo(stopping.HighWater))
                Assert.That(after.AllRecordedEffectsSettled, Is.True)
                Assert.That(after.RouteCoverageProven, Is.False)
            finally
                Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, oldJournal))


    [<Test>]
    member _.ActivatedJournalStopRejectsMissingChangedAndPartiallyDeletedHostBinding() =
        withFixture (fun f ->
            let pathA = Path.Combine(f.Root, "writer-a.db")
            let pathB = Path.Combine(f.Root, "writer-b.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal pathA
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal pathB
            let setting = OriginalGitWriterAdmissionJournal.JournalEnvironment
            Environment.SetEnvironmentVariable(setting, pathA)
            AccountCapacityLedger.setCapacity f.AccountOne (Guid.NewGuid()) "Operator" 1L 4
            |> success |> ignore
            let before = AccountCapacityLedger.fetch f.AccountTwo |> success
            let closed = OriginalGitWriterAdmissionJournal.stop pathA |> success
            Assert.That(closed.AdmissionClosed, Is.True)
            let projectHead =
                git f.Root [ "--git-dir=" + f.ProjectRemote; "rev-parse"; "refs/heads/jarvis-orchestration" ]
            let accountHead =
                git f.Root [ "--git-dir=" + f.AccountRemote; "rev-parse"; AccountCapacityLedger.AccountRef ]
            let denied (label: string) =
                let result =
                    AccountCapacityLedger.setCapacity f.AccountTwo (Guid.NewGuid()) "Operator"
                        before.State.StateVersion 5
                Assert.That(Result.isError result, Is.True, label)
                let operation =
                    { OperationId = Guid.NewGuid(); Actor = "Operator"
                      Kind = "PostStopBindingAttempt"; RequestJson = "{}" }
                let resultProject =
                    LedgerStore.transact f.ProjectTwo operation (fun _ ->
                        Ok [ { Path = "records/binding-guard/" + Guid.NewGuid().ToString("N") + ".json"
                               ExpectedBlobOid = None; Content = "{}" } ])
                Assert.That(Result.isError resultProject, Is.True, label + ":project")
                let currentProject =
                    git f.Root [ "--git-dir=" + f.ProjectRemote; "rev-parse"; "refs/heads/jarvis-orchestration" ]
                let currentAccount =
                    git f.Root [ "--git-dir=" + f.AccountRemote; "rev-parse"; AccountCapacityLedger.AccountRef ]
                Assert.That(currentProject, Is.EqualTo(projectHead), label + ":project-head")
                Assert.That(currentAccount, Is.EqualTo(accountHead), label + ":account-head")
            Environment.SetEnvironmentVariable(setting, null)
            denied "missing activated environment binding"
            Environment.SetEnvironmentVariable(setting, pathB)
            denied "substituted open journal"
            let archived = pathA + ".original"
            File.Move(pathA, archived)
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal pathA
            Environment.SetEnvironmentVariable(setting, pathA)
            denied "same journal path with replaced identity"
            File.Delete pathA
            File.Move(archived, pathA)
            denied "restored binding cannot reset STOP"
            let manifest = Path.Combine(f.Root, "original-writer-host-binding", "journal.binding")
            File.Delete manifest
            denied "missing persistent binding after activation"
            File.Delete(Path.Combine(f.Root, "original-writer-host-binding", "journal.binding.sha256"))
            Environment.SetEnvironmentVariable(setting, null)
            denied "entire binding pair deleted after activation"
            Assert.That((OriginalGitWriterAdmissionJournal.inspect pathA |> success).AdmissionClosed,
                        Is.True)
            Assert.That((OriginalGitWriterAdmissionJournal.inspect pathB |> success).HighWater,
                        Is.EqualTo(0L)))

    [<Test>]
    member _.JournalConfiguredBeforeActualProjectAndAccountInitializationFencesBothAfterStop() =
        withFixture (fun f ->
            let path = Path.Combine(f.Root, "before-initialization.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal path
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, path)
            let accountRemote, accountLocal, _ = repository f.Root "new-account"
            let projectRemote, projectLocal, _ = repository f.Root "new-project"
            let account =
                { f.AccountOne with RepositoryPath = accountLocal
                                    ApprovedFetchUrl = accountRemote
                                    ApprovedPushUrl = accountRemote
                                    AccountId = Guid.NewGuid()
                                    LedgerId = Guid.NewGuid() }
            let project =
                { f.ProjectOne with RepositoryPath = projectLocal
                                    ApprovedFetchUrl = projectRemote
                                    ApprovedPushUrl = projectRemote
                                    ProjectId = Guid.NewGuid()
                                    LedgerId = Guid.NewGuid() }
            let accountState = AccountCapacityLedger.initialize account 2 |> success
            let projectState = LedgerStore.initialize project true |> success
            Assert.That(accountState.Head, Is.Not.Empty)
            Assert.That(projectState.Commit, Is.Not.Empty)
            let observed = OriginalGitWriterAdmissionJournal.inspect path |> success
            Assert.That(observed.HighWater, Is.EqualTo(2L),
                        "Both real initialization publication paths must be admitted")
            Assert.That(observed.PendingOrUnknown, Is.Empty)
            Assert.That(observed.SettledHighWater, Is.EqualTo(2L))
            OriginalGitWriterAdmissionJournal.stop path |> success |> ignore
            let stoppedAccountRemote, stoppedAccountLocal, _ = repository f.Root "blocked-account"
            let stoppedProjectRemote, stoppedProjectLocal, _ = repository f.Root "blocked-project"
            let blockedAccount =
                { account with RepositoryPath = stoppedAccountLocal
                               ApprovedFetchUrl = stoppedAccountRemote
                               ApprovedPushUrl = stoppedAccountRemote
                               AccountId = Guid.NewGuid(); LedgerId = Guid.NewGuid() }
            let blockedProject =
                { project with RepositoryPath = stoppedProjectLocal
                               ApprovedFetchUrl = stoppedProjectRemote
                               ApprovedPushUrl = stoppedProjectRemote
                               ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid() }
            Assert.That(AccountCapacityLedger.initialize blockedAccount 3 |> Result.isError, Is.True)
            Assert.That(LedgerStore.initialize blockedProject true |> Result.isError, Is.True)
            let accountRef =
                git f.Root [ "--git-dir=" + stoppedAccountRemote; "for-each-ref"
                             "--format=%(refname)"; AccountCapacityLedger.AccountRef ]
            let projectRef =
                git f.Root [ "--git-dir=" + stoppedProjectRemote; "for-each-ref"
                             "--format=%(refname)"; "refs/heads/jarvis-orchestration" ]
            Assert.That(accountRef, Is.Empty, "STOP must prevent remote account ref creation")
            Assert.That(projectRef, Is.Empty, "STOP must prevent remote project ref creation")
            Assert.That((OriginalGitWriterAdmissionJournal.inspect path |> success).HighWater,
                        Is.EqualTo(2L)))

    [<Test>]
    member _.LostGitAcknowledgmentRemainsUnknownAfterReopenAndChangedDuplicatePayloadDenied() =
        withFixture (fun f ->
            let path = Path.Combine(f.Root, "lost-git-ack.db")
            OriginalGitWriterAdmissionJournal.provisionDisposableJournal path
            Environment.SetEnvironmentVariable(OriginalGitWriterAdmissionJournal.JournalEnvironment, path)
            let operation = Guid.NewGuid()
            let authority = String.replicate 64 "c"
            let remote, local, _ = repository f.Root "lost-ack"
            let gitRef = "refs/heads/jarvis-review/_reviews/lost-ack/r1"
            let publish () : Result<string,string> =
                git local [ "push"; "origin"; "HEAD:" + gitRef ] |> ignore
                Error "fixture intentionally discarded the successful Git push acknowledgment"
            let result =
                OriginalGitWriterAdmissionJournal.withPublication
                    operation "ReviewRef" authority gitRef "first-payload"
                    publish (fun _ -> true) id
            Assert.That(Result.isError result, Is.True)
            let remoteRef =
                git f.Root [ "--git-dir=" + remote; "rev-parse"; gitRef ]
            Assert.That(remoteRef, Is.Not.Empty, "Real fixture Git effect must have occurred")
            let unknown = OriginalGitWriterAdmissionJournal.inspect path |> success
            Assert.That(unknown.PendingOrUnknown.Length, Is.EqualTo(1))
            Assert.That(unknown.PendingOrUnknown.Head.Status, Is.EqualTo("Unknown"))
            match OriginalGitWriterAdmissionJournal.admit operation "ReviewRef"
                      authority gitRef "first-payload" with
            | Error reason -> Assert.That(reason, Does.Contain("RequiresReconciliation"))
            | Ok _ -> Assert.Fail("UNKNOWN lost acknowledgment was replayed")
            match OriginalGitWriterAdmissionJournal.admit operation "ReviewRef"
                      authority gitRef "changed-payload" with
            | Error reason -> Assert.That(reason, Does.Contain("RequiresReconciliation"))
            | Ok _ -> Assert.Fail("UUID reused with different bytes was admitted")
            Assert.That((OriginalGitWriterAdmissionJournal.inspect path |> success).PendingOrUnknown.Length,
                        Is.EqualTo(1)))
