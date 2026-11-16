module Client.Tests.WorkQueueCanonicalMigrationPrepareTests

open System
open System.Diagnostics
open System.IO
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
        Directory.CreateDirectory root |> ignore
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
