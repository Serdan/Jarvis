module Client.Tests.AutomationNativeAccountProjectIngress20261123Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open NUnit.Framework
open Client.AccountCapacityLedger
open Client.LedgerStore
open Client.NativeSchedulerRosterPolicy
open Client.NativeSchedulerAccountReservation
open Client.ReviewWorkflow
open Client.AutomationNativeAccountProjectIngress

type private ConfiguredFixture =
    { Root: string
      AccountOne: AccountBinding
      AccountTwo: AccountBinding
      ProjectOne: LedgerBinding
      ProjectTwo: LedgerBinding }

module private ConfiguredFixture =
    let git directory args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- directory
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        proc.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        proc.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] <- "1"
        args |> List.iter proc.StartInfo.ArgumentList.Add
        Assert.That(proc.Start(), Is.True)
        let stdout = proc.StandardOutput.ReadToEnd().Trim()
        let stderr = proc.StandardError.ReadToEnd()
        Assert.That(proc.WaitForExit(30000), Is.True, "Bounded offline git fixture timeout")
        Assert.That(proc.ExitCode, Is.EqualTo(0), stderr)
        stdout

    let success = function
        | Ok x -> x
        | Error e -> failwithf "Expected success; actual %A" e

    let private markPrivate (path: string) =
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    let private remote root name =
        let bare = Path.Combine(root, name + ".git")
        let local = Path.Combine(root, name + "-seed")
        git root ["init"; "--bare"; "--initial-branch=main"; bare] |> ignore
        git root ["clone"; bare; local] |> ignore
        git local ["config"; "user.name"; "Disposable Fixture"] |> ignore
        git local ["config"; "user.email"; "disposable@example.test"] |> ignore
        File.WriteAllText(Path.Combine(local,"seed.txt"), name)
        git local ["add"; "seed.txt"] |> ignore
        git local ["commit"; "-m"; "bounded source seed"] |> ignore
        git local ["push"; "-u"; "origin"; "HEAD"] |> ignore
        bare, local

    let withGit action =
        let root = Path.Combine(Path.GetTempPath(), "native-started-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        markPrivate root
        try
            let scope1 = Path.Combine(root, "wq10-private-client-one")
            let scope2 = Path.Combine(root, "wq10-private-client-two")
            Directory.CreateDirectory(scope1) |> ignore
            Directory.CreateDirectory(scope2) |> ignore
            markPrivate scope1
            markPrivate scope2
            let accountRemote, _ = remote root "account"
            let acc1Path = Path.Combine(scope1, "account")
            let acc2Path = Path.Combine(scope2, "account")
            git root ["clone"; accountRemote; acc1Path] |> ignore
            git root ["clone"; accountRemote; acc2Path] |> ignore
            let accountId = Guid.NewGuid()
            let accountLedgerId = Guid.NewGuid()
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = accountRemote; ApprovedPushUrl = accountRemote
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            AccountCapacityLedger.initialize (account acc1Path) 2 |> success |> ignore
            let projectRemote, projectPath = remote root "project"
            let projectId, projectLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let project path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = projectRemote; ApprovedPushUrl = projectRemote
                  ProjectId = projectId; LedgerId = projectLedgerId; Epoch = 1 }
            LedgerStore.initialize (project projectPath) true |> success |> ignore
            let otherProject = Path.Combine(root, "project-other-client")
            git root ["clone"; projectRemote; otherProject] |> ignore
            let f =
                { Root = root; AccountOne = account acc1Path; AccountTwo = account acc2Path
                  ProjectOne = project projectPath; ProjectTwo = project otherProject }
            action f
        finally
            try Directory.Delete(root, true) with _ -> ()

    let projectBindings f account =
        if account = 1 then [| f.ProjectOne |] else [| f.ProjectTwo |]

    let configureVersion f version previous accountWorkerLimit projectWorkerLimit =
        let project = f.ProjectOne
        let account = f.AccountOne
        let initial = (AccountCapacityLedger.fetch account |> success).Head
        let authority =
            { ProjectId = project.ProjectId; LedgerId = project.LedgerId; Epoch = project.Epoch
              ApprovedFetchUrl = project.ApprovedFetchUrl; ApprovedPushUrl = project.ApprovedPushUrl }
        let policy =
            { SchemaVersion = 1
              AccountId = account.AccountId.ToString("D")
              AccountLedgerId = account.LedgerId.ToString("D")
              AccountEpoch = account.Epoch
              AccountAuthoritySha256 =
                  NativeSchedulerRosterPolicy.accountAuthoritySha256
                      (account.AccountId.ToString("D")) (account.LedgerId.ToString("D"))
                      account.Epoch account.ApprovedFetchUrl account.ApprovedPushUrl
              Version = version; MembershipComplete = true
              AccountLimits = { Worker = accountWorkerLimit; Reviewer = 1 }
              Projects =
                  [| { ProjectId = project.ProjectId.ToString("D")
                       Alias = "Disposable project"
                       LedgerId = project.LedgerId.ToString("D")
                       Epoch = project.Epoch
                       SourceRef = NativeSchedulerRosterPolicy.ProjectSourceRef
                       RemoteAuthoritySha256 = AccountCapacityLedger.projectAuthoritySha256 authority
                       Limits = { Worker = projectWorkerLimit; Reviewer = 1 } } |] }
        let update =
            { SchemaVersion = 1
              OperationId = Guid.NewGuid().ToString("D")
              Actor = "Configured account fixture"
              PreviousVersion = previous
              ExpectedAccountHead = initial
              Policy = policy }
        AccountCapacityLedger.setNativeRosterPolicy account [| project |] update
        |> success |> ignore

    let configure f = configureVersion f 1L 0L 1 1

    let coverage f account projects =
        let projection =
            AccountCapacityLedger.readNativeRosterPolicy account projects
        Assert.That(projection.Status, Is.EqualTo("PolicyVerified"),
                    String.concat ";" projection.HoldReasons)
        { AccountHead = projection.VerifiedAccountHead.Value
          ProjectHeads = projection.VerifiedProjectHeads
          ObservedAtUtc = DateTimeOffset.UtcNow }

    let private json value =
        JsonSerializer.Serialize(value, JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase))

    let startedWorker f =
        let p = f.ProjectOne
        let originalHead = (LedgerStore.fetch p |> success).Commit
        let taskId = "started-" + Guid.NewGuid().ToString("N")
        let attemptId = Guid.NewGuid().ToString("N")
        let sourceRef = "refs/heads/disposable-source"
        let spec = "<h1>Native Started fixture</h1>"
        let specOid = LedgerStore.computeBlobOid p spec |> success
        let task: ReviewTask =
            { TaskId=taskId; Title="Native Started fixture"; Version=2
              Lifecycle="InProgress"; SpecVersion=1; AttemptId=attemptId }
        let attempt: ReviewAttempt =
            { TaskId=taskId; AttemptId=attemptId; Version=3
              OwnerAgentId="Worker fixture"; Phase="Started"
              AssignmentGeneration=1; Priority=0; IssuedSequence=1L
              SpecBlobOid=specOid; BaseCommit=originalHead
              TargetRef="refs/heads/main"
              SourceRepositoryId=String.replicate 64 "a"
              SourceRemote=p.ApprovedFetchUrl; SourcePushRemote=p.ApprovedPushUrl
              SourceRef=sourceRef }
        let attemptRaw = json attempt
        LedgerStore.transact p
            { OperationId=Guid.NewGuid(); Actor="Disposable issuer"
              Kind="NativeWorkerIssued"
              RequestJson=json {| taskId=taskId; attemptId=attemptId |} }
            (fun _ -> Ok [
                { Path=$"tasks/{taskId}/task.json"; ExpectedBlobOid=None; Content=json task }
                { Path=$"tasks/{taskId}/spec.html"; ExpectedBlobOid=None; Content=spec }
                { Path=$"tasks/{taskId}/attempts/{attemptId}.json"; ExpectedBlobOid=None; Content=attemptRaw } ])
        |> success |> ignore
        { DispatchId=Guid.NewGuid().ToString("D")
          EffectOperationId=Guid.NewGuid().ToString("D")
          Kind="Worker"
          ProjectId=p.ProjectId.ToString("D")
          ProjectLedgerId=p.LedgerId.ToString("D")
          ProjectEpoch=p.Epoch
          OwnerAgentId=attempt.OwnerAgentId
          TaskId=taskId; AttemptId=attemptId
          AssignmentGeneration=int64 attempt.AssignmentGeneration
          SourceRepositoryId=attempt.SourceRepositoryId
          SourceBaseOid=originalHead
          SourceRef=sourceRef
          SourceLeaseSha256=NativeSchedulerRosterPolicy.sha256 attemptRaw
          ReviewId=""; ReviewVersion=0L; ReviewedHeadOid=""
          ReviewRef=""; ClaimOperationId="" }

    let advanceStartedGeneration f (candidate: NativeDispatchCandidate) =
        let project = f.ProjectOne
        let path = $"tasks/{candidate.TaskId}/attempts/{candidate.AttemptId}.json"
        let write =
            LedgerStore.transact project
                { OperationId = Guid.NewGuid()
                  Actor = "Disposable issuer"
                  Kind = "FixtureAdvanceStartedGeneration"
                  RequestJson = json {| attemptId = candidate.AttemptId |} }
                (fun snapshot ->
                    let raw, blob = LedgerStore.readAt project snapshot path |> success
                    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                    let attempt = JsonSerializer.Deserialize<ReviewAttempt>(raw.Value, options)
                    Assert.That(attempt.Phase, Is.EqualTo("Started"))
                    Assert.That(int64 attempt.AssignmentGeneration, Is.EqualTo(candidate.AssignmentGeneration))
                    let advanced =
                        { attempt with
                            AssignmentGeneration = attempt.AssignmentGeneration + 1
                            Version = attempt.Version + 1 }
                    Ok [ { Path = path; ExpectedBlobOid = Some blob.Value; Content = json advanced } ])
            |> success
        Assert.That(write.AlreadyApplied, Is.False)
        LedgerStore.fetch project |> success |> _.Commit

    let reserved f =
        configure f
        let c = startedWorker f
        let original = Guid.NewGuid()
        NativeSchedulerAccountReservation.reserve
            f.AccountOne [| f.ProjectOne |]
            (coverage f f.AccountOne [| f.ProjectOne |])
            original "Disposable native source" c
        |> success |> ignore
        let head = (LedgerStore.fetch f.ProjectOne |> success).Commit
        let accountHead = (AccountCapacityLedger.fetch f.AccountOne |> success).Head
        c, original,
        { OperationId=Guid.NewGuid(); ExpectedProjectHead=head
          AccountHead=accountHead; OriginalReservationOperationId=original
          Candidate=c; DeviceId="client-device"; WindowRegistration="window-disposable" }

    let ingress f account project claim =
        let projects = [| project |]
        Client.AutomationNativeAccountProjectIngress.claimStarted
            account projects
            (coverage f account projects) claim

    let state f =
        let cover = coverage f f.AccountOne [| f.ProjectOne |]
        NativeSchedulerAccountReservation.readVerified
            f.AccountOne [| f.ProjectOne |] cover
        |> success |> snd

    let reservationProof f (claim: StartedProjectClaim) =
        let snapshot, receipt =
            AccountCapacityLedger.readOperation f.AccountOne claim.OriginalReservationOperationId
            |> success
        Assert.That(receipt.IsSome, Is.True, "Missing original NativeReserve receipt")
        Assert.That(receipt.Value.Kind, Is.EqualTo("NativeReserve"))
        let document = AccountCapacityLedger.readNativeReservationDocument f.AccountOne snapshot.Head |> success
        let records = (state f).Records
        Assert.That(records.Length, Is.EqualTo(1))
        Assert.That(records[0].Candidate = claim.Candidate, Is.True)
        Assert.That(records[0].Status, Is.EqualTo("Reserved"))
        Assert.That(records[0].OriginalOperationId, Is.EqualTo(claim.OriginalReservationOperationId.ToString("D")))
        snapshot.Head, receipt.Value, document.Value

    let assertNoProjectClaim f (claim: StartedProjectClaim) =
        let snapshot = LedgerStore.fetch f.ProjectOne |> success
        Assert.That(snapshot.Commit, Is.EqualTo(claim.ExpectedProjectHead))
        let receipt = LedgerStore.getOperationAt f.ProjectOne snapshot claim.OperationId |> success
        Assert.That(receipt.IsNone, Is.True, "Denied claim wrote a project receipt")
        let c = claim.Candidate
        let key = NativeSchedulerRosterPolicy.sha256 (c.ProjectLedgerId + "|Worker|" + c.TaskId + "|" + c.AttemptId)
        let path = $"records/native-account-started-effect-v1/{key}.json"
        let fence, blob = LedgerStore.readAt f.ProjectOne snapshot path |> success
        Assert.That(fence.IsNone && blob.IsNone, Is.True, "Denied claim wrote a project fence")

[<TestFixture>]
type StartedNativeIngressTests() =
    [<Test>]
    member _.ConfiguredStartedWorkerWinsOneRealProjectCasAndOtherCloneLoses() =
        ConfiguredFixture.withGit (fun f ->
            let c, original, claim = ConfiguredFixture.reserved f
            let before = (LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit
            Assert.That(before, Is.EqualTo(claim.ExpectedProjectHead))
            let result = ConfiguredFixture.ingress f f.AccountOne f.ProjectOne claim
                         |> ConfiguredFixture.success
            Assert.That(result.AlreadyApplied, Is.False)
            let loser = { claim with OperationId=Guid.NewGuid() }
            match ConfiguredFixture.ingress f f.AccountTwo f.ProjectTwo loser with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Second independent clone won stale Started project CAS")
            let after = (LedgerStore.fetch f.ProjectTwo |> ConfiguredFixture.success).Commit
            Assert.That(after, Is.Not.EqualTo(before))
            let records = (ConfiguredFixture.state f).Records
            Assert.That(records.Length, Is.EqualTo(1))
            Assert.That(records[0].OriginalOperationId, Is.EqualTo(original.ToString("D")))
            Assert.That(records[0].Status, Is.EqualTo("Reserved"),
                        "Project-won/native account remains occupied until authenticated terminal")
            let path =
                "records/native-account-started-effect-v1/"
                + NativeSchedulerRosterPolicy.sha256 (
                    c.ProjectLedgerId + "|Worker|" + c.TaskId + "|" + c.AttemptId) + ".json"
            let snapshot = LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success
            match LedgerStore.readAt f.ProjectOne snapshot path with
            | Ok(Some raw, Some _) ->
                Assert.That(raw, Does.Contain("HeldBeforeSend"))
                Assert.That(raw, Does.Contain(c.DispatchId))
                Assert.That(raw, Does.Contain(original.ToString("D")))
            | other -> failwithf "Expected original project fence: %A" other)

    [<Test>]
    member _.ForgedRoleLeaseGenerationAndUUIDCannotCrossConfiguredAccountBoundary() =
        ConfiguredFixture.withGit (fun f ->
            let _, _, claim = ConfiguredFixture.reserved f
            let cases =
                [ "foreign role", { claim with Candidate={ claim.Candidate with Kind="Reviewer" } }
                  "stale assignment", { claim with Candidate={ claim.Candidate with AssignmentGeneration=2L } }
                  "rewritten source", { claim with Candidate={ claim.Candidate with SourceRef="refs/heads/attacker" } }
                  "rewritten lease", { claim with Candidate={ claim.Candidate with SourceLeaseSha256=String.replicate 64 "b" } }
                  "rewritten dispatch", { claim with Candidate={ claim.Candidate with DispatchId=Guid.NewGuid().ToString("D") } }
                  "altered effect", { claim with Candidate={ claim.Candidate with EffectOperationId=Guid.NewGuid().ToString("D") } }
                  "unrelated original reservation", { claim with OriginalReservationOperationId=Guid.NewGuid() }
                  "stale account head", { claim with AccountHead=String.replicate 40 "a" }
                  "stale project head", { claim with ExpectedProjectHead=String.replicate 40 "b" }
                  "missing window", { claim with WindowRegistration="" } ]
            let originalHead = (LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit
            for label, bad in cases do
                match ConfiguredFixture.ingress f f.AccountTwo f.ProjectTwo bad with
                | Error _ -> ()
                | Ok _ -> Assert.Fail(label + " changed project CAS")
                let observed = (LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit
                Assert.That(observed, Is.EqualTo(originalHead), label))

    [<Test>]
    member _.NoCallerObservationCanMintAnAccountReleaseOrProviderTerminal() =
        let observations =
            [ Unsupported; Unknown; AcknowledgmentLost; Foreign "other-ledger"
              Late; Duplicate; TabClosed ]
        for observation in observations do
            match denyUntrustedTerminal observation with
            | Denied reason -> Assert.That(reason, Is.Not.Empty)

    [<TestCase(0, 1)>]
    [<TestCase(1, 0)>]
    member _.CurrentConfiguredWorkerRevocationDeniesPreSendAndPreservesReservation(accountLimit: int, projectLimit: int) =
        ConfiguredFixture.withGit (fun f ->
            let _, _, claim = ConfiguredFixture.reserved f
            let originalAccount, originalReceipt, originalDocument = ConfiguredFixture.reservationProof f claim
            ConfiguredFixture.configureVersion f 2L 1L accountLimit projectLimit
            let projects = [| f.ProjectTwo |]
            let projection =
                AccountCapacityLedger.readNativeRosterPolicy f.AccountTwo projects
            Assert.That(projection.Status, Is.EqualTo("PolicyVerified"))
            let currentAccount = projection.VerifiedAccountHead.Value
            let policy = projection.Policy.Value
            Assert.That(currentAccount, Is.Not.EqualTo(originalAccount))
            Assert.That(policy.Version, Is.EqualTo(2L))
            Assert.That(policy.AccountLimits.Worker, Is.EqualTo(accountLimit))
            Assert.That(policy.Projects.Length, Is.EqualTo(1))
            Assert.That(policy.Projects[0].ProjectId, Is.EqualTo(claim.Candidate.ProjectId))
            Assert.That(policy.Projects[0].LedgerId, Is.EqualTo(claim.Candidate.ProjectLedgerId))
            Assert.That(policy.Projects[0].Epoch, Is.EqualTo(claim.Candidate.ProjectEpoch))
            Assert.That(policy.Projects[0].Limits.Worker, Is.EqualTo(projectLimit))
            Assert.That(projection.VerifiedProjectHeads[0].VerifiedHead, Is.EqualTo(claim.ExpectedProjectHead))
            let refreshed = { claim with AccountHead = currentAccount }
            let accountProof = currentAccount, originalReceipt, originalDocument
            Assert.That(ConfiguredFixture.reservationProof f refreshed = accountProof, Is.True)
            match ConfiguredFixture.ingress f f.AccountTwo f.ProjectTwo claim with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Superseded account head admitted pre-send")
            match ConfiguredFixture.ingress f f.AccountTwo f.ProjectTwo refreshed with
            | Error(InvalidBinding detail) ->
                Assert.That(detail, Does.Contain("Current configured roster denies pre-send"))
            | other -> failwithf "Expected current configured Worker policy denial: %A" other
            ConfiguredFixture.assertNoProjectClaim f refreshed
            Assert.That(ConfiguredFixture.reservationProof f refreshed = accountProof, Is.True)
            TestContext.WriteLine($"Account: {originalAccount} -> {currentAccount}; project: {claim.ExpectedProjectHead}"))

    [<Test>]
    member _.CanonicalStartedGenerationChangeDeniesOriginalCandidateAndPreservesReservation() =
        ConfiguredFixture.withGit (fun f ->
            let candidate, _, claim = ConfiguredFixture.reserved f
            let accountProof = ConfiguredFixture.reservationProof f claim
            let advancedHead = ConfiguredFixture.advanceStartedGeneration f candidate
            Assert.That(advancedHead, Is.Not.EqualTo(claim.ExpectedProjectHead))
            let refreshed = { claim with ExpectedProjectHead = advancedHead }
            Assert.That(ConfiguredFixture.reservationProof f refreshed = accountProof, Is.True)
            match ConfiguredFixture.ingress f f.AccountTwo f.ProjectTwo refreshed with
            | Error(StaleEntityVersion detail) ->
                Assert.That(detail, Does.Contain("Canonical Started assignment/source lease is stale or rewritten"))
            | other -> failwithf "Expected canonical Started assignment/source lease denial: %A" other
            ConfiguredFixture.assertNoProjectClaim f refreshed
            Assert.That(ConfiguredFixture.reservationProof f refreshed = accountProof, Is.True)
            TestContext.WriteLine($"Project: {claim.ExpectedProjectHead} -> {advancedHead}; account: {claim.AccountHead}"))

    [<Test>]
    member _.RestartedIndependentCloneCannotRegenerateOriginalSourceOrEffectUUID() =
        ConfiguredFixture.withGit (fun f ->
            let _, _, claim = ConfiguredFixture.reserved f
            let first = ConfiguredFixture.ingress f f.AccountOne f.ProjectOne claim
                        |> ConfiguredFixture.success
            Assert.That(first.AlreadyApplied, Is.False)
            let scope = Path.Combine(f.Root, "wq10-private-client-three")
            Directory.CreateDirectory(scope) |> ignore
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(scope,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let thirdAccount = Path.Combine(scope, "account")
            let thirdProject = Path.Combine(f.Root, "third-project")
            ConfiguredFixture.git f.Root ["clone"; f.AccountOne.ApprovedFetchUrl; thirdAccount] |> ignore
            ConfiguredFixture.git f.Root ["clone"; f.ProjectOne.ApprovedFetchUrl; thirdProject] |> ignore
            let restartedAccount = { f.AccountOne with RepositoryPath=thirdAccount }
            let restartedProject = { f.ProjectOne with RepositoryPath=thirdProject }
            let coverage = ConfiguredFixture.coverage f restartedAccount [| restartedProject |]
            let original =
                claimStarted restartedAccount
                    [| restartedProject |] coverage claim
                |> ConfiguredFixture.success
            Assert.That(original.AlreadyApplied, Is.True,
                        "Exact immutable claim is a receipt replay, never a second send")
            match claimStarted restartedAccount
                    [| restartedProject |] (ConfiguredFixture.coverage f restartedAccount [| restartedProject |])
                    { claim with OperationId=Guid.NewGuid() } with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Restarted client minted a new original operation")
            let native = ConfiguredFixture.state f
            Assert.That(native.Records.Length, Is.EqualTo(1))
            Assert.That(native.Records[0].Status, Is.EqualTo("Reserved")))

    [<Test>]
    member _.NoIncompleteRosterCanBePromotedByCaller() =
        ConfiguredFixture.withGit (fun f ->
            let _, _, claim = ConfiguredFixture.reserved f
            let projectHead = (LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit
            let covers = ConfiguredFixture.coverage f f.AccountOne [| f.ProjectOne |]
            let tests =
                [ "incomplete coverage", { covers with ProjectHeads=[||] }
                  "stale observation", { covers with ObservedAtUtc=DateTimeOffset.UtcNow.AddMinutes(-5.0) }
                  "wrong account head", { covers with AccountHead=String.replicate 40 "f" } ]
            for label, coverage in tests do
                match claimStarted f.AccountTwo [| f.ProjectTwo |] coverage claim with
                | Error _ -> ()
                | Ok _ -> Assert.Fail(label + " produced an unauthorized Started fence")
                Assert.That((LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit,
                            Is.EqualTo(projectHead), label))

    [<Test>]
    member _.AcceptedOldFakeStageBRefusesTheCanonicalStartedWorker() =
        ConfiguredFixture.withGit (fun f ->
            let candidate, _, real = ConfiguredFixture.reserved f
            let fakeAccount : Client.NativeProjectEffectFence.FakeAccountReservationReceipt =
                { Marker="STAGE_B_DISPOSABLE_FAKE_ACCOUNT_RECEIPT/v1"
                  AccountLedgerId=f.AccountOne.LedgerId.ToString("D")
                  AccountEpoch=f.AccountOne.Epoch
                  AccountHead=real.AccountHead
                  OriginalReservationOperationId=real.OriginalReservationOperationId.ToString("D")
                  ReservationId="old-fake-receipt"
                  ProjectId=f.ProjectOne.ProjectId.ToString("D")
                  Kind="Worker" }
            let oldCandidate : Client.NativeProjectEffectFence.NativeCandidate =
                { Kind="Worker"
                  TaskId=candidate.TaskId; AttemptId=candidate.AttemptId
                  ReviewId=""; OwnerAgentId=candidate.OwnerAgentId
                  AssignmentGeneration=int candidate.AssignmentGeneration
                  ReviewVersion=0
                  SourceRepositoryId=candidate.SourceRepositoryId
                  SourceRef=candidate.SourceRef
                  BaseCommit=candidate.SourceBaseOid
                  TargetRef="refs/heads/main"
                  ReviewRef=""; ReviewedHeadOid="" }
            let oldClaim : Client.NativeProjectEffectFence.PreSendClaim =
                { OperationId=Guid.NewGuid()
                  ExpectedProjectHead=real.ExpectedProjectHead
                  Account=fakeAccount
                  Candidate=oldCandidate
                  DispatchId=Guid.Parse candidate.DispatchId
                  EffectId=Guid.Parse candidate.EffectOperationId
                  DeviceId="disposable-control-device"
                  WindowRegistration="disposable-control-window" }
            match Client.NativeProjectEffectFence.claim f.ProjectOne oldClaim with
            | Error(StaleEntityVersion detail) ->
                Assert.That(detail, Does.Contain("Worker canonical attempt"))
            | other -> failwithf "Old fake B1 unexpectedly accepted Started: %A" other
            Assert.That((LedgerStore.fetch f.ProjectOne |> ConfiguredFixture.success).Commit,
                        Is.EqualTo(real.ExpectedProjectHead)))
