module Client.Tests.WorkQueueCanonicalMigrationPrepareTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open NUnit.Framework
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.WorkQueueCanonicalMigrationPrepare
open Jarvis.WorkQueues

type private MigrationFixture =
    { Root: string
      AccountA: AccountBinding
      AccountB: AccountBinding
      ProjectA: LedgerBinding
      ProjectAOther: LedgerBinding
      ProjectB: LedgerBinding
      ProjectBOther: LedgerBinding
      AccountRemote: string
      ProjectARemote: string
      ProjectBRemote: string }

[<TestFixture>]
type WorkQueueCanonicalMigrationPrepareTests() =
    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        args |> List.iter runner.StartInfo.ArgumentList.Add
        Assert.That(runner.Start(), Is.True)
        let output = runner.StandardOutput.ReadToEnd()
        let errors = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Fixture Git command exceeded 20 seconds."
        Assert.That(runner.ExitCode, Is.EqualTo(0), errors)
        output.Trim()

    let success = function
        | Ok result -> result
        | Error error -> failwithf "Unexpected fixture setup failure: %A" error

    let repository root name =
        let bare = Path.Combine(root, name + ".git")
        let first = Path.Combine(root, name + "-first")
        let second = Path.Combine(root, name + "-second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        git first [ "config"; "user.name"; "Fixture" ] |> ignore
        git first [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        File.WriteAllText(Path.Combine(first, "original.txt"), "untouched legacy history")
        git first [ "add"; "original.txt" ] |> ignore
        git first [ "commit"; "-m"; "legacy fixture" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore
        bare, first, second

    let withClones action =
        let root = Path.Combine(Path.GetTempPath(), "wq10-canonical-" + Guid.NewGuid().ToString("N"))
        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory root |> ignore
        else
            Directory.CreateDirectory(
                root, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            |> ignore
            Assert.That(File.GetUnixFileMode root,
                        Is.EqualTo(UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute))
        File.WriteAllText(Path.Combine(root, ".wq10-disposable-fixture"), "WQ10_DISPOSABLE_FIXTURE_V1")
        try
            let ar, a1, a2 = repository root "account"
            let pr1, p1, p2 = repository root "project-alpha"
            let pr2, q1, q2 = repository root "project-beta"
            let accountId, accountLedgerId = Guid.NewGuid(), Guid.NewGuid()
            let projectId1, ledgerId1 = Guid.NewGuid(), Guid.NewGuid()
            let projectId2, ledgerId2 = Guid.NewGuid(), Guid.NewGuid()
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = ar; ApprovedPushUrl = ar
                  AccountId = accountId; LedgerId = accountLedgerId; Epoch = 1 }
            let project path remote projectId ledgerId =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let fixture =
                { Root = root
                  AccountA = account a1; AccountB = account a2
                  ProjectA = project p1 pr1 projectId1 ledgerId1
                  ProjectAOther = project p2 pr1 projectId1 ledgerId1
                  ProjectB = project q1 pr2 projectId2 ledgerId2
                  ProjectBOther = project q2 pr2 projectId2 ledgerId2
                  AccountRemote = ar; ProjectARemote = pr1; ProjectBRemote = pr2 }
            AccountCapacityLedger.initialize fixture.AccountA 3 |> success |> ignore
            for p in [ fixture.ProjectA; fixture.ProjectB ] do
                LedgerStore.initialize p true |> success |> ignore
                WorkQueueGitLedger.initialize p "fixture" (Guid.NewGuid())
                    (Map.ofList [ "Lead", set [ "planning" ] ]) |> success |> ignore
            action fixture
            for dir in [ a1; a2; p1; p2; q1; q2 ] do
                Assert.That(git dir [ "status"; "--porcelain" ], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    let request fixture mode =
        let account = AccountCapacityLedger.fetch fixture.AccountA |> success
        let project name binding =
            let snapshot = LedgerStore.fetch binding |> success
            let state = WorkQueueGitLedger.loadAt binding snapshot |> success
            { Name = name; Binding = binding; ExpectedHead = snapshot.Commit
              ExpectedHighWater = snapshot.Header.NextSequence - 1L
              ExpectedVersion = state.Version }
        { OperationId = Guid.NewGuid()
          Actor = "WQ10 fixture"
          DisposableRoot = fixture.Root
          Mode = mode
          Account = fixture.AccountA
          ExpectedAccountHead = account.Head
          Projects = [ project "Alpha" fixture.ProjectA; project "Beta" fixture.ProjectB ]
          ReviewedOfflineReportSha256 = String('a', 64) }

    let snapshotRemoteHeads fixture =
        [ fixture.AccountRemote, AccountCapacityLedger.AccountRef
          fixture.ProjectARemote, "refs/heads/jarvis-orchestration"
          fixture.ProjectBRemote, "refs/heads/jarvis-orchestration" ]
        |> List.map (fun (remote, branch) ->
            remote, git fixture.Root [ "--git-dir=" + remote; "rev-parse"; branch ])

    let assertNoCanonicalWrite fixture (before: (string * string) list) =
        Assert.That(snapshotRemoteHeads fixture = before, Is.True)

    let blocked result =
        match result with
        | Error assessment ->
            Assert.That(assessment.CanonicalPrepared, Is.False)
            Assert.That(assessment.SchedulerEnabled, Is.False)
            Assert.That(assessment.ProductionCutoverAuthorized, Is.False)
            assessment
        | Ok _ -> failwith "An incomplete WQ10 boundary falsely returned canonical Prepared."

    let contains predicate assessment = assessment.Missing |> List.exists predicate

    let withRedirectedMetadata repo relative action =
        let original = Path.Combine(repo, relative)
        let escaped = Path.Combine(Path.GetTempPath(), "wq10-outside-" + Guid.NewGuid().ToString("N"))
        Directory.Move(original, escaped)
        Directory.CreateSymbolicLink(original, escaped) |> ignore
        try
            let originalOutsideFiles =
                Directory.GetFiles(escaped, "*", SearchOption.AllDirectories) |> Set.ofArray
            action ()
            let observedOutsideFiles =
                Directory.GetFiles(escaped, "*", SearchOption.AllDirectories) |> Set.ofArray
            Assert.That(
                (observedOutsideFiles = originalOutsideFiles),
                Is.True,
                "A Git operation escaped the disposable fixture through nested metadata.")
        finally
            Directory.Delete original
            Directory.Move(escaped, original)

    // All proof producers here are ephemeral and independent of the command.
    // This simulates host-pinned authorities only inside a disposable Git lab;
    // no production signer, old-writer fence or approval is provisioned.
    let freezeDisposableAccount fixture =
        let clone = fixture.AccountA.RepositoryPath
        git clone [ "fetch"; "origin"
                    AccountCapacityLedger.AccountRef + ":refs/heads/wq10-fixture-frozen" ]
        |> ignore
        git clone [ "checkout"; "wq10-fixture-frozen" ] |> ignore
        let statePath = Path.Combine(clone, "state.json")
        let original = File.ReadAllText statePath
        let frozen = original.Replace("\"migrationPhase\":\"LegacyActive\"",
                                      "\"migrationPhase\":\"Frozen\"")
        Assert.That(frozen, Is.Not.EqualTo(original))
        File.WriteAllText(statePath, frozen)
        git clone [ "add"; "state.json" ] |> ignore
        git clone [ "commit"; "-m"; "WQ10 disposable-only frozen account fixture" ] |> ignore
        git clone [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore

    let withSignedPreparing fixture action =
        use protocol = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        use fence = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        use approval = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let key (signer: ECDsa) = signer.ExportSubjectPublicKeyInfo()
        let keys: WorkQueueCanonicalMigrationPrepare.PinnedAccountPreparingKeys =
            { ClientProtocolPublicKeySpki = key protocol
              OldWriterFencePublicKeySpki = key fence
              OperatorApprovalPublicKeySpki = key approval }
        let previousVersion = (AccountCapacityLedger.fetch fixture.AccountA |> success).State.StateVersion
        // Stable identities across retries. This is a hermetic simulation of
        // independently inventoried legacy journals, NOT real host evidence.
        let journalIds =
            [ fixture.ProjectA.ProjectId, Guid.NewGuid()
              fixture.ProjectB.ProjectId, Guid.NewGuid() ] |> Map.ofList
        let sha (bytes: byte array) = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        let sign (signer: ECDsa) role statement evidence =
            { EvidenceSha256 = evidence
              SignatureBase64 =
                  signer.SignData(
                      WorkQueueCanonicalMigrationPrepare.accountPreparingWitnessMessage
                          role statement evidence, HashAlgorithmName.SHA256)
                  |> Convert.ToBase64String
              SignerPublicKeySha256 = sha (key signer) }
        let makeWithCoverage (target: CanonicalPrepareRequest) (overrideCheckpoints: AccountMigrationOriginalSourceCheckpoint array option) =
            let checkpoints =
                match overrideCheckpoints with
                | Some supplied -> supplied
                | None ->
                    target.Projects
                    |> List.map (fun project ->
                        { ProjectId = string project.Binding.ProjectId
                          FrozenProjectHead = project.ExpectedHead
                          OriginalJournalId = string journalIds[project.Binding.ProjectId]
                          OriginalJournalHighWater = 0L
                          OriginalSourceEffectHighWater = 0L })
                    |> List.toArray
            let blank = { EvidenceSha256 = String('0', 64)
                          SignatureBase64 = ""
                          SignerPublicKeySha256 = String('0', 64) }
            let raw: WorkQueueCanonicalMigrationPrepare.AccountPreparingCommand =
                { Target = target
                  ExpectedAccountStateVersion = previousVersion
                  MinConnectedClientProtocol = 3
                  ClientProtocolReportSha256 = String('b', 64)
                  OldWriterFenceReportSha256 = String('c', 64)
                  OperatorDecisionSha256 = String('d', 64)
                  OriginalSourceCheckpoints = checkpoints
                  OriginalSourceCoverageWitness = blank
                  ClientProtocolWitness = blank
                  OldWriterFenceWitness = blank
                  OperatorApprovalWitness = blank }
            let statement = WorkQueueCanonicalMigrationPrepare.accountPreparingStatementSha256 raw
            let coverage = WorkQueueCanonicalMigrationPrepare.accountPreparingCoverageSha256 raw
            { raw with
                OriginalSourceCoverageWitness =
                    sign fence "original-journal-coverage" statement coverage
                ClientProtocolWitness = sign protocol "client-protocol" statement raw.ClientProtocolReportSha256
                OldWriterFenceWitness = sign fence "old-writer-fence" statement raw.OldWriterFenceReportSha256
                OperatorApprovalWitness = sign approval "operator-approval" statement raw.OperatorDecisionSha256 }
        let make (target: CanonicalPrepareRequest) = makeWithCoverage target None
        action keys make (fun target checkpoints -> makeWithCoverage target (Some checkpoints))

    let preparing (result: WorkQueueCanonicalMigrationPrepare.AccountPreparingOutcome) : AccountMigrationPreparingReceipt * AccountWrite =
        Assert.That(result.Projects,
                    Is.EqualTo(WorkQueueCanonicalMigrationPrepare.ProjectPreparedState.ProjectPreparedNotEstablished))
        Assert.That(result.SchedulerEnabled, Is.False)
        Assert.That(result.ProductionCutoverAuthorized, Is.False)
        match result.Account with
        | WorkQueueCanonicalMigrationPrepare.AccountPreparingState.AccountPreparing(receipt, write) ->
            receipt, write
        | WorkQueueCanonicalMigrationPrepare.AccountPreparingState.AccountRecoveryBlocked reasons ->
            failwithf "Expected independently verified disposable account Preparing: %A" reasons

    let accountBlocked (result: WorkQueueCanonicalMigrationPrepare.AccountPreparingOutcome) =
        Assert.That(result.SchedulerEnabled, Is.False)
        Assert.That(result.ProductionCutoverAuthorized, Is.False)
        match result.Account with
        | WorkQueueCanonicalMigrationPrepare.AccountPreparingState.AccountRecoveryBlocked reasons ->
            Assert.That(reasons, Is.Not.Empty)
            reasons
        | _ -> failwith "A blocked account migration was accepted."

    [<Test>]
    member _.DefaultOffTwoProjectsRemainsReadOnlyWithNoCanonicalReceipt() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let result = request fixture CanonicalPrepareMode.InspectOnly
                         |> WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare |> blocked
            Assert.That(result.AccountHead.IsSome, Is.True)
            Assert.That(result.ProjectHeads.Length, Is.EqualTo(2))
            Assert.That(contains (function CanonicalPrepareGap.DisabledByDefault -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.AccountNotFrozen "LegacyActive" -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.AccountPreparingCasUnavailable -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.ProjectPreparedCasUnavailable -> true | _ -> false) result, Is.True)
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.ExplicitDisposableAttemptAndReviewHashNeverBecomeAnApprovalCapability() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let result = request fixture CanonicalPrepareMode.AttemptOnDisposable
                         |> WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare |> blocked
            Assert.That(contains (function CanonicalPrepareGap.DisabledByDefault -> true | _ -> false) result, Is.False)
            Assert.That(contains (function CanonicalPrepareGap.AuthenticatedOfflineReviewMissing -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.AuthenticatedOldWriterFenceMissing -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.CrossLedgerPartialReceiptRecoveryUnavailable -> true | _ -> false) result, Is.True)
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.SameOriginalUuidReplayIsReadOnlyAndDeterministic() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let first = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
            let second = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
            Assert.That(first, Is.EqualTo(second))
            Assert.That(first.OperationId, Is.EqualTo(input.OperationId))
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.CompetingAccountCloneChangeIsDetectedAtOriginalHead() =
        withClones (fun fixture ->
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            AccountCapacityLedger.setCapacity fixture.AccountB (Guid.NewGuid()) "legacy writer" 1L 4
            |> success |> ignore
            let authoritativeAfter = snapshotRemoteHeads fixture
            let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
            Assert.That(contains (function CanonicalPrepareGap.AccountHeadChanged(e, o) when e = input.ExpectedAccountHead && o <> e -> true | _ -> false) result, Is.True)
            assertNoCanonicalWrite fixture authoritativeAfter)

    [<Test>]
    member _.CompetingProjectCloneChangeAndOriginalHighWaterAreBothFenced() =
        withClones (fun fixture ->
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let updated = input.Projects |> List.map (fun p -> if p.Name = "Beta" then { p with ExpectedHighWater = p.ExpectedHighWater + 1L } else p)
            let stale = { input with Projects = updated }
            // Canonical legacy write from another project clone, not a synthetic head assertion.
            ReviewWorkflow.registerAgent fixture.ProjectAOther (Guid.NewGuid()) "Legacy Agent"
            |> success |> ignore
            let authoritativeAfter = snapshotRemoteHeads fixture
            let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare stale |> blocked
            Assert.That(contains (function CanonicalPrepareGap.ProjectHeadChanged("Alpha", _, _) -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.ProjectHighWaterChanged("Alpha", _, _) -> true | _ -> false) result, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.ProjectHighWaterChanged("Beta", _, _) -> true | _ -> false) result, Is.True)
            assertNoCanonicalWrite fixture authoritativeAfter)

    [<Test>]
    member _.MissingMarkerAndTraversingRepoCannotEvenReadAuthority() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            File.Delete(Path.Combine(fixture.Root, ".wq10-disposable-fixture"))
            let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
            Assert.That(result.AccountHead.IsNone, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
            File.WriteAllText(Path.Combine(fixture.Root, ".wq10-disposable-fixture"), "WQ10_DISPOSABLE_FIXTURE_V1")
            let traversed = Path.Combine(fixture.Root, "account-first", "..", "account-first")
            let account = { input.Account with RepositoryPath = traversed }
            let traversing = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare { input with Account = account } |> blocked
            Assert.That(traversing.AccountHead.IsNone, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) traversing, Is.True)
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.GroupOrWorldWritableDisposableRootIsRejectedBeforeFetch() =
        if not (OperatingSystem.IsWindows()) then
            withClones (fun fixture ->
                let original = snapshotRemoteHeads fixture
                let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
                let privateMode = File.GetUnixFileMode fixture.Root
                try
                    for unsafeBit in [ UnixFileMode.GroupWrite; UnixFileMode.OtherWrite ] do
                        File.SetUnixFileMode(fixture.Root, privateMode ||| unsafeBit)
                        let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
                        Assert.That(result.AccountHead.IsNone, Is.True)
                        Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
                        assertNoCanonicalWrite fixture original
                finally
                    File.SetUnixFileMode(fixture.Root, privateMode))

    [<Test>]
    member _.UnapprovedAuthorityOutsidePrivateScratchIsRejectedBeforeFetch() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let outside = Path.GetFullPath(Path.Combine(fixture.Root, "..", "not-approved.git"))
            let modifiedAccount = { input.Account with ApprovedPushUrl = outside }
            let assessment = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare
                                 { input with Account = modifiedAccount } |> blocked
            Assert.That(assessment.AccountHead.IsNone, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) assessment, Is.True)
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.SymlinkedCloneCannotEscapeDisposableBoundary() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let linked = Path.Combine(fixture.Root, "account-link")
            Directory.CreateSymbolicLink(linked, input.Account.RepositoryPath) |> ignore
            let linkedAccount = { input.Account with RepositoryPath = linked }
            let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare { input with Account = linkedAccount } |> blocked
            Assert.That(result.AccountHead.IsNone, Is.True)
            Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
            assertNoCanonicalWrite fixture original)

    [<Test>]
    member _.GitObjectAlternateRedirectIsRejectedBeforeTrustedFetch() =
        withClones (fun fixture ->
            let original = snapshotRemoteHeads fixture
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let alternate = Path.Combine(fixture.ProjectA.RepositoryPath, ".git", "objects", "info", "alternates")
            File.WriteAllText(alternate, Path.Combine(fixture.Root, "..", "external-store"))
            try
                let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
                Assert.That(result.AccountHead.IsNone, Is.True)
                Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
                assertNoCanonicalWrite fixture original
            finally
                File.Delete(alternate))

    [<Test>]
    member _.NestedCloneObjectsSymlinkCannotRedirectFetchWritesOutsideFixture() =
        withClones (fun fixture ->
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            // Advance the true remote, so an unguarded Git fetch would write
            // new objects into the symlinked clone metadata directory.
            AccountCapacityLedger.setCapacity fixture.AccountB (Guid.NewGuid()) "legacy writer" 1L 4
            |> success |> ignore
            let authoritativeAfter = snapshotRemoteHeads fixture
            withRedirectedMetadata fixture.AccountA.RepositoryPath (Path.Combine(".git", "objects")) (fun () ->
                let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
                Assert.That(result.AccountHead.IsNone, Is.True)
                Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
                assertNoCanonicalWrite fixture authoritativeAfter))

    [<Test>]
    member _.NestedCloneAndBareAuthorityMetadataLinksAreRejectedBeforeAnyGitFetch() =
        withClones (fun fixture ->
            let input = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let originalHeads = snapshotRemoteHeads fixture
            for repository, relative in
                [ fixture.ProjectA.RepositoryPath, Path.Combine(".git", "refs")
                  fixture.ProjectB.RepositoryPath, Path.Combine(".git", "objects", "info")
                  fixture.AccountRemote, "refs"
                  fixture.ProjectARemote, "objects" ] do
                withRedirectedMetadata repository relative (fun () ->
                    let result = WorkQueueCanonicalMigrationPrepare.attemptCanonicalPrepare input |> blocked
                    Assert.That(result.AccountHead.IsNone, Is.True)
                    Assert.That(contains (function CanonicalPrepareGap.UnapprovedDisposableAuthority _ -> true | _ -> false) result, Is.True)
                    assertNoCanonicalWrite fixture originalHeads))

    [<Test>]
    member _.SignedAccountOnlyPreparingUsesOneOriginalCasAndImmutableReceiptAcrossClones() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let original = snapshotRemoteHeads fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let command = sign target
                let first = WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys command
                            |> preparing
                let receipt, write = first
                Assert.That(write.AlreadyApplied, Is.False)
                Assert.That(receipt.ParticipantRoster.Length, Is.EqualTo(2))
                Assert.That(receipt.OriginalOperationId, Is.EqualTo(string target.OperationId))
                Assert.That(receipt.PreviousAccountHead, Is.EqualTo(target.ExpectedAccountHead))
                Assert.That(receipt.ReviewedReportSha256, Is.EqualTo(target.ReviewedOfflineReportSha256))
                Assert.That(receipt.AccountStatus, Is.EqualTo("AccountPreparing"))
                Assert.That(receipt.ProjectStatus, Is.EqualTo("ProjectPreparedNotEstablished"))
                Assert.That((AccountCapacityLedger.fetch fixture.AccountB |> success).State.MigrationPhase,
                            Is.EqualTo("Preparing"))
                let afterFirst = snapshotRemoteHeads fixture
                Assert.That(snd afterFirst[0], Is.Not.EqualTo(snd original[0]))
                Assert.That(List.tail afterFirst = List.tail original, Is.True,
                            "Account-only Preparing must not touch either project authority.")
                let recovered = AccountCapacityLedger.readOperation fixture.AccountB target.OperationId
                                |> success
                let immutable = snd recovered |> Option.get
                Assert.That(immutable.RequestSha256, Is.EqualTo(write.Receipt.RequestSha256))
                Assert.That(immutable.ResultSha256, Is.EqualTo(write.Receipt.ResultSha256))
                Assert.That(immutable.Kind, Is.EqualTo("AccountMigrationPreparing"))
                let anotherClone =
                    { command with Target = { target with Account = fixture.AccountB } }
                let replay = WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys anotherClone
                             |> preparing
                Assert.That((snd replay).AlreadyApplied, Is.True)
                Assert.That((fst replay).StatementSha256, Is.EqualTo(receipt.StatementSha256))
                Assert.That(snapshotRemoteHeads fixture = afterFirst, Is.True,
                            "Original UUID replay must not create a second account CAS.")))

    [<Test>]
    member _.CompetingOriginalAndAlteredOriginalCannotOverrideWinningRoster() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let baseline = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let first = sign baseline
                let another =
                    sign { baseline with OperationId = Guid.NewGuid(); Account = fixture.AccountB }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys first
                |> preparing |> ignore
                let winningHeads = snapshotRemoteHeads fixture
                let losingReasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys another
                    |> accountBlocked
                Assert.That(losingReasons |> List.exists (fun r ->
                    r.Contains("AccountHeadChanged")), Is.True,
                    "The losing original must be rejected against its pinned prior head.")
                let reused = sign { baseline with ReviewedOfflineReportSha256 = String('e', 64) }
                let reuseReasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys reused
                    |> accountBlocked
                Assert.That(reuseReasons |> List.exists (fun r ->
                    r.Contains("AccountOperationIdReused")), Is.True,
                    "A different signed request cannot occupy the same original UUID.")
                // Every caller can sign a different payload with fixture keys,
                // but no such payload may inherit the first original UUID.
                let alteredActor = sign { baseline with Actor = "foreign signer actor" }
                let alteredProtocol =
                    { first with
                        MinConnectedClientProtocol = first.MinConnectedClientProtocol + 1 }
                for changed in [ alteredActor; alteredProtocol ] do
                    let reasons =
                        WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys changed
                        |> accountBlocked
                    Assert.That(reasons |> List.exists (fun r ->
                        r.Contains("AccountOperationIdReused")
                        || r.Contains("signature")
                        || r.Contains("mismatched")), Is.True)
                let alteredProjectHead =
                    { baseline with
                        Projects =
                            baseline.Projects |> List.mapi (fun i p ->
                                if i = 0 then { p with ExpectedHead = String('f', 40) }
                                else p) }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys (sign alteredProjectHead)
                |> accountBlocked |> ignore
                Assert.That(snapshotRemoteHeads fixture = winningHeads, Is.True)))

    [<Test>]
    member _.LostOriginalPushAcknowledgmentIsReconciledWithoutDuplicateToken() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let hooks =
                    { BeforeLeasePush = fun _ _ -> ()
                      GitCommand = fun args ->
                          if args |> List.exists (fun arg ->
                              arg.StartsWith("--force-with-lease=" + AccountCapacityLedger.AccountRef)) then
                              AccountGitTestFault.ExecuteThenLoseAcknowledgment
                          else AccountGitTestFault.PassThrough }
                let result =
                    AccountCapacityLedger.withTestInterposition hooks (fun () ->
                        WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys (sign target))
                    |> preparing
                Assert.That((snd result).Receipt.OperationId, Is.EqualTo(string target.OperationId))
                Assert.That((AccountCapacityLedger.fetch fixture.AccountB |> success).State.MigrationPhase,
                            Is.EqualTo("Preparing"))
                Assert.That((AccountCapacityLedger.fetch fixture.AccountB |> success).State.Tokens,
                            Is.Empty)
                Assert.That((snd (AccountCapacityLedger.readOperation fixture.AccountB target.OperationId
                                  |> success)).IsSome, Is.True)))

    [<Test>]
    member _.UnsignedOrForeignApprovalAndDefaultOffNeverMoveAccountRef() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let start = snapshotRemoteHeads fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let cmd = sign target
                let badApproval =
                    { cmd with
                        OperatorApprovalWitness =
                            { cmd.OperatorApprovalWitness with SignatureBase64 = "AA==" } }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys badApproval
                |> accountBlocked |> ignore
                let forgedProtocol =
                    { cmd with MinConnectedClientProtocol = cmd.MinConnectedClientProtocol + 1 }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys forgedProtocol
                |> accountBlocked |> ignore
                let disabled = sign { target with Mode = CanonicalPrepareMode.InspectOnly }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys disabled
                |> accountBlocked |> ignore
                let foreign =
                    { keys with OperatorApprovalPublicKeySpki = keys.ClientProtocolPublicKeySpki }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors foreign cmd
                |> accountBlocked |> ignore
                Assert.That(snapshotRemoteHeads fixture = start, Is.True)))

    [<Test>]
    member _.HeldGlobalSlotAndStaleFrozenHeadBlockAccountPreparingBeforeWrite() =
        withClones (fun fixture ->
            let p = fixture.ProjectA
            let authority: ProjectLedgerAuthority =
                { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                  ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl }
            AccountCapacityLedger.reserve fixture.AccountA (Guid.NewGuid()) "legacy"
                (Guid.NewGuid()) authority "Lead" 1L
            |> success |> ignore
            freezeDisposableAccount fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let original = snapshotRemoteHeads fixture
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let held = sign target
                let heldReasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys held
                    |> accountBlocked
                Assert.That(heldReasons |> List.exists (fun r ->
                    r.Contains("AccountCapacityOccupied")), Is.True)
                Assert.That(snapshotRemoteHeads fixture = original, Is.True)
                let staleTarget = { target with ExpectedAccountHead = String('f', 40) }
                WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys (sign staleTarget)
                |> accountBlocked |> ignore
                Assert.That(snapshotRemoteHeads fixture = original, Is.True)))


    [<Test>]
    member _.FrozenLegacyAdmissionAndCapacityWritesAreFenced() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let original = snapshotRemoteHeads fixture
            let p = fixture.ProjectA
            let authority: ProjectLedgerAuthority =
                { ProjectId = p.ProjectId; LedgerId = p.LedgerId; Epoch = p.Epoch
                  ApprovedFetchUrl = p.ApprovedFetchUrl; ApprovedPushUrl = p.ApprovedPushUrl }
            let reserved = AccountCapacityLedger.reserve fixture.AccountB
                               (Guid.NewGuid()) "legacy actor" (Guid.NewGuid())
                               authority "Lead" 1L
            match reserved with
            | Error(AccountFailure.AccountMigrationWritesFenced "Frozen") -> ()
            | other -> failwithf "Frozen account accepted legacy reservation: %A" other
            let capacity = AccountCapacityLedger.setCapacity fixture.AccountB
                               (Guid.NewGuid()) "legacy actor" 1L 5
            match capacity with
            | Error(AccountFailure.AccountMigrationWritesFenced "Frozen") -> ()
            | other -> failwithf "Frozen account accepted legacy capacity edit: %A" other
            Assert.That(snapshotRemoteHeads fixture = original, Is.True))

    [<Test>]
    member _.SignedButNonDisposableOrUnfrozenAttemptNeverPushesAccountRef() =
        withClones (fun fixture ->
            let before = snapshotRemoteHeads fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let unfrozenReasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys (sign target)
                    |> accountBlocked
                Assert.That(unfrozenReasons |> List.exists (fun r ->
                    r.Contains("AccountNotFrozen")), Is.True)
                let otherRoot =
                    { target with DisposableRoot = Path.GetTempPath() }
                let unapprovedReasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors keys (sign otherRoot)
                    |> accountBlocked
                Assert.That(unapprovedReasons |> List.exists (fun r ->
                    r.Contains("UnapprovedDisposableAuthority")), Is.True)
                Assert.That(snapshotRemoteHeads fixture = before, Is.True)))

    [<Test>]
    member _.CallerGeneratedSelfSignedKeysCannotAuthorizePublicAccountPreparing() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let original = snapshotRemoteHeads fixture
            withSignedPreparing fixture (fun _keys sign _ ->
                // Three syntactically valid, self-generated signer key pairs
                // must NOT authorize through the ordinary public entrypoint.
                let command = sign target
                let reasons =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparing command
                    |> accountBlocked
                Assert.That(reasons |> List.exists (fun reason ->
                    reason.Contains("not provisioned")), Is.True)
                assertNoCanonicalWrite fixture original))

    [<Test>]
    member _.SignedMissingLegacyJournalAndUnknownSourceHighWaterCannotMoveAccount() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            let original = snapshotRemoteHeads fixture
            withSignedPreparing fixture (fun keys sign signWithCoverage ->
                let good = sign target
                let missing = signWithCoverage target [||]
                let unknown =
                    good.OriginalSourceCheckpoints
                    |> Array.map (fun checkpoint ->
                        { checkpoint with OriginalSourceEffectHighWater = -1L })
                    |> signWithCoverage target
                for command in [ missing; unknown ] do
                    let reasons =
                        WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors
                            keys command |> accountBlocked
                    Assert.That(reasons |> List.exists (fun reason ->
                        reason.Contains("OriginalJournalsAndSourceHighWaterMissing")), Is.True)
                    assertNoCanonicalWrite fixture original))

    [<Test>]
    member _.LostAcknowledgmentThenLegitimateProjectProgressRecoversOriginalReceiptReadOnly() =
        withClones (fun fixture ->
            freezeDisposableAccount fixture
            let target = request fixture CanonicalPrepareMode.AttemptOnDisposable
            withSignedPreparing fixture (fun keys sign _ ->
                let command = sign target
                let hooks =
                    { BeforeLeasePush = fun _ _ -> ()
                      GitCommand = fun args ->
                          if args |> List.exists (fun arg ->
                              arg.StartsWith("--force-with-lease=" + AccountCapacityLedger.AccountRef)) then
                              AccountGitTestFault.ExecuteThenLoseAcknowledgment
                          else AccountGitTestFault.PassThrough }
                let first =
                    AccountCapacityLedger.withTestInterposition hooks (fun () ->
                        WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors
                            keys command) |> preparing
                Assert.That((snd first).AlreadyApplied, Is.False)
                ReviewWorkflow.registerAgent fixture.ProjectAOther
                    (Guid.NewGuid()) "Independent project progress" |> success |> ignore
                let afterProgress = snapshotRemoteHeads fixture
                let retry =
                    { command with Target = { target with Account = fixture.AccountB } }
                let recovered =
                    WorkQueueCanonicalMigrationPrepare.attemptAccountPreparingWithFixtureTrustAnchors
                        keys retry |> preparing
                Assert.That((snd recovered).AlreadyApplied, Is.True)
                Assert.That((fst recovered).StatementSha256, Is.EqualTo((fst first).StatementSha256))
                Assert.That(snapshotRemoteHeads fixture = afterProgress, Is.True,
                    "Exact original receipt recovery must not rewrite any account or project ref.")
                Assert.That((AccountCapacityLedger.fetch fixture.AccountB |> success).State.MigrationPhase,
                    Is.EqualTo("Preparing"))))
