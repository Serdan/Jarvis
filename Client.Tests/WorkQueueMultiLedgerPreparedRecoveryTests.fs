module Client.Tests.WorkQueueMultiLedgerPreparedRecoveryTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalMigrationPrepare
open Client.WorkQueueProjectMigrationPrepare
open Client.WorkQueueMultiLedgerPreparedRecovery
open Client.ReviewWorkflow
open Jarvis.WorkQueues

type private ProjectLab =
    { First: LedgerBinding
      Second: LedgerBinding
      Bare: string
      JournalId: Guid
      Journal: DisposableOriginalJournal
      FrozenHistory: Map<string, string>
      HistoricalSourceSha: string
      HistoricalSourceRef: string }

type private MultiLedgerLab =
    { Root: string
      AccountA: AccountBinding
      AccountB: AccountBinding
      AccountBare: string
      A: ProjectLab
      B: ProjectLab
      Plan: MultiLedgerPreparePlan }

[<TestFixture>]
type WorkQueueMultiLedgerPreparedRecoveryTests() =
    let ok = function
        | Ok value -> value
        | Error failure -> failwithf "Disposable fixture setup failed: %A" failure

    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        runner.StartInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
        args |> List.iter runner.StartInfo.ArgumentList.Add
        Assert.That(runner.Start(), Is.True)
        let stdout = runner.StandardOutput.ReadToEnd()
        let stderr = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Disposable Git command exceeded 20 seconds"
        Assert.That(runner.ExitCode, Is.EqualTo(0), stderr)
        stdout.Trim()

    let repository root name =
        let bare = Path.Combine(root, name + ".git")
        let first = Path.Combine(root, name + "-one")
        let second = Path.Combine(root, name + "-two")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        git first [ "config"; "user.name"; "WQ10 Recovery Fixture" ] |> ignore
        git first [ "config"; "user.email"; "wq10-recovery@example.invalid" ] |> ignore
        File.WriteAllText(Path.Combine(first,"legacy.txt"), "original source remains intact")
        git first [ "add"; "legacy.txt" ] |> ignore
        git first [ "commit"; "-m"; "legacy original" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore
        bare, first, second

    let freezeAccount (binding: AccountBinding) =
        git binding.RepositoryPath
            [ "fetch"; "origin"; AccountCapacityLedger.AccountRef + ":refs/heads/wq10-frozen" ]
        |> ignore
        git binding.RepositoryPath [ "checkout"; "wq10-frozen" ] |> ignore
        let path = Path.Combine(binding.RepositoryPath, "state.json")
        let before = File.ReadAllText path
        let after = before.Replace("\"migrationPhase\":\"LegacyActive\"",
                                   "\"migrationPhase\":\"Frozen\"")
        Assert.That(after, Is.Not.EqualTo(before))
        File.WriteAllText(path, after)
        git binding.RepositoryPath [ "add"; "state.json" ] |> ignore
        git binding.RepositoryPath [ "commit"; "-m"; "disposable Frozen account" ] |> ignore
        git binding.RepositoryPath
            [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore

    // Entity reads deliberately reject ops/ paths. Hash those immutable
    // operation blobs at the exact Git snapshot and separately verify their
    // signed UUID/request hashes through LedgerStore.getOperationAt.
    let historicalBlob (binding: LedgerBinding) (snapshot: LedgerSnapshot) (path: string) =
        if path.StartsWith("ops/", StringComparison.Ordinal) then
            git binding.RepositoryPath [ "rev-parse"; snapshot.Commit + ":" + path ]
        else
            let _, oid = LedgerStore.readAt binding snapshot path |> ok
            oid |> Option.get

    let seedOriginal (first: LedgerBinding) slug =
        let worker, reviewer, task = "Original " + slug, "Reviewer " + slug, slug + "-legacy-task"
        let agentOp, reviewerOp, taskOp, reviewOp, managerOp =
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        ReviewWorkflow.registerAgent first agentOp worker |> ok |> ignore
        ReviewWorkflow.registerAgent first reviewerOp reviewer |> ok |> ignore
        ReviewWorkflow.createTask first taskOp task "Immutable legacy task" |> ok |> ignore
        let branch = "refs/heads/feature/" + slug + "-original-source"
        git first.RepositoryPath [ "checkout"; "-b"; "feature/" + slug + "-original-source" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath,"reviewed.txt"), "original " + slug + " source")
        git first.RepositoryPath [ "add"; "reviewed.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "reviewed original source" ] |> ignore
        let sourceSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        git first.RepositoryPath [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        ReviewWorkflow.submitBranchReview first reviewOp task worker
            (Guid.NewGuid()) branch sourceSha "Original independent review" "preserved review"
            "refs/heads/main" |> ok |> ignore
        let managerPath = "records/wq10-fixture/legacy-manager-" + slug + ".json"
        LedgerStore.putRecord first
            { OperationId = managerOp; Actor = "original Manager"
              Path = managerPath; ExpectedBlobOid = None
              Content = """{"legacyManager":"frozen","runnable":false}""" }
        |> ok |> ignore
        let beforeJournal = LedgerStore.fetch first |> ok
        // Cover ALL original operation receipts, including initialization,
        // reviewer registration and legacy Manager operations, not a sample.
        let originalIds = LedgerStore.getOperationIdsAt first beforeJournal |> ok
        Assert.That(originalIds.Contains(reviewerOp.ToString("N")), Is.True)
        let originals =
            originalIds
            |> Set.toList
            |> List.map (fun key ->
                let id = Guid.ParseExact(key, "N")
                let op = LedgerStore.getOperationAt first beforeJournal id |> ok |> Option.get
                id.ToString("D"), op.RequestHash, op.Sequence)
        let journalId = Guid.NewGuid()
        let journal: DisposableOriginalJournal =
            { SchemaVersion = 1
              JournalId = journalId.ToString("D")
              OriginalHighWater = originals |> List.map (fun (_,_,seq) -> seq) |> List.max
              OriginalSourceEffectHighWater = 0L
              OriginalRequestHashes =
                originals |> List.map (fun (id,hash,_) -> id,hash) |> Map.ofList }
        LedgerStore.putRecord first
            { OperationId = Guid.NewGuid(); Actor = "disposable original-journal producer"
              Path = WorkQueueProjectMigrationPrepare.FixtureJournalPath
              ExpectedBlobOid = None; Content = CanonicalCodec.encode journal }
        |> ok |> ignore
        let snapshot = LedgerStore.fetch first |> ok
        let operationPaths =
            originalIds
            |> Set.toList
            |> List.map (fun key -> sprintf "ops/%s/%s.json" (key.Substring(0, 2)) key)
        let paths =
            [ "agents"; "tasks"; "records/roles" ]
            |> List.collect (fun dir -> LedgerStore.listAt first snapshot dir |> ok)
            |> fun enumerated ->
                managerPath :: WorkQueueProjectMigrationPrepare.FixtureJournalPath :: (operationPaths @ enumerated)
            |> List.distinct
        let history =
            paths |> List.map (fun path -> path, historicalBlob first snapshot path)
            |> Map.ofList
        journalId, journal, history, sourceSha, branch

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "wq10-multiledger-" + Guid.NewGuid().ToString("N"))
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
            let pAR, pA1, pA2 = repository root "project-alpha"
            let pBR, pB1, pB2 = repository root "project-beta"
            let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
            let account path: AccountBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = ar; ApprovedPushUrl = ar
                  AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
            let project id ledger remote path: LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = id; LedgerId = ledger; Epoch = 1 }
            let paId, paLedger, pbId, pbLedger =
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            let a = account a1
            let aOther = account a2
            let pA = project paId paLedger pAR pA1
            let pAOther = project paId paLedger pAR pA2
            let pB = project pbId pbLedger pBR pB1
            let pBOther = project pbId pbLedger pBR pB2
            AccountCapacityLedger.initialize a 3 |> ok |> ignore
            for p in [pA;pB] do
                LedgerStore.initialize p true |> ok |> ignore
                WorkQueueGitLedger.initialize p "WQ10 disposable lab" (Guid.NewGuid()) Map.empty
                |> ok |> ignore
            let ja, journalA, historyA, sourceA, refA = seedOriginal pA "alpha"
            let jb, journalB, historyB, sourceB, refB = seedOriginal pB "beta"
            freezeAccount a
            let accountBefore = AccountCapacityLedger.fetch a |> ok
            let targetProject name binding =
                let snap = LedgerStore.fetch binding |> ok
                let state = WorkQueueGitLedger.loadAt binding snap |> ok
                { Name = name; Binding = binding; ExpectedHead = snap.Commit
                  ExpectedHighWater = snap.Header.NextSequence - 1L
                  ExpectedVersion = state.Version }
            let targets = [targetProject "Alpha" pA; targetProject "Beta" pB]
            let request: CanonicalPrepareRequest =
                { OperationId = Guid.NewGuid()
                  Actor = "WQ10 disposable operator"
                  DisposableRoot = root
                  Mode = CanonicalPrepareMode.AttemptOnDisposable
                  Account = a
                  ExpectedAccountHead = accountBefore.Head
                  Projects = targets
                  ReviewedOfflineReportSha256 = String('a',64) }
            use protocol = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            use fence = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            use approval = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let key (s: ECDsa) = s.ExportSubjectPublicKeyInfo()
            let keys: WorkQueueCanonicalMigrationPrepare.PinnedAccountPreparingKeys =
                { ClientProtocolPublicKeySpki = key protocol
                  OldWriterFencePublicKeySpki = key fence
                  OperatorApprovalPublicKeySpki = key approval }
            let digest (bytes: byte array) =
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            let sign (s: ECDsa) role statement evidence =
                { EvidenceSha256 = evidence
                  SignatureBase64 =
                    s.SignData(
                        WorkQueueCanonicalMigrationPrepare.accountPreparingWitnessMessage
                            role statement evidence, HashAlgorithmName.SHA256)
                    |> Convert.ToBase64String
                  SignerPublicKeySha256 = digest(key s) }
            let checkpoints: AccountMigrationOriginalSourceCheckpoint array =
                [| { ProjectId = pA.ProjectId.ToString("D")
                     FrozenProjectHead = targets[0].ExpectedHead
                     OriginalJournalId = ja.ToString("D")
                     OriginalJournalHighWater = journalA.OriginalHighWater
                     OriginalSourceEffectHighWater = journalA.OriginalSourceEffectHighWater }
                   { ProjectId = pB.ProjectId.ToString("D")
                     FrozenProjectHead = targets[1].ExpectedHead
                     OriginalJournalId = jb.ToString("D")
                     OriginalJournalHighWater = journalB.OriginalHighWater
                     OriginalSourceEffectHighWater = journalB.OriginalSourceEffectHighWater } |]
            let blank =
                { EvidenceSha256 = String('0',64)
                  SignatureBase64 = ""
                  SignerPublicKeySha256 = String('0',64) }
            let raw: WorkQueueCanonicalMigrationPrepare.AccountPreparingCommand =
                { Target = request
                  ExpectedAccountStateVersion = accountBefore.State.StateVersion
                  MinConnectedClientProtocol = 3
                  ClientProtocolReportSha256 = String('b',64)
                  OldWriterFenceReportSha256 = String('c',64)
                  OperatorDecisionSha256 = String('d',64)
                  OriginalSourceCheckpoints = checkpoints
                  OriginalSourceCoverageWitness = blank
                  ClientProtocolWitness = blank
                  OldWriterFenceWitness = blank
                  OperatorApprovalWitness = blank }
            let statement = WorkQueueCanonicalMigrationPrepare.accountPreparingStatementSha256 raw
            let coverage = WorkQueueCanonicalMigrationPrepare.accountPreparingCoverageSha256 raw
            let signed =
                { raw with
                    OriginalSourceCoverageWitness =
                        sign fence "original-journal-coverage" statement coverage
                    ClientProtocolWitness =
                        sign protocol "client-protocol" statement raw.ClientProtocolReportSha256
                    OldWriterFenceWitness =
                        sign fence "old-writer-fence" statement raw.OldWriterFenceReportSha256
                    OperatorApprovalWitness =
                        sign approval "operator-approval" statement raw.OperatorDecisionSha256 }
            let plan =
                { AccountCommand = signed; FixtureKeys = keys
                  Projects =
                    [ { Target = targets[0]; OriginalOperationId = Guid.NewGuid(); OriginalJournalId = ja }
                      { Target = targets[1]; OriginalOperationId = Guid.NewGuid(); OriginalJournalId = jb } ] }
            let lab =
                { Root = root; AccountA = a; AccountB = aOther; AccountBare = ar
                  A = { First = pA; Second = pAOther; Bare = pAR
                        JournalId = ja; Journal = journalA; FrozenHistory = historyA
                        HistoricalSourceSha = sourceA; HistoricalSourceRef = refA }
                  B = { First = pB; Second = pBOther; Bare = pBR
                        JournalId = jb; Journal = journalB; FrozenHistory = historyB
                        HistoricalSourceSha = sourceB; HistoricalSourceRef = refB }
                  Plan = plan }
            action lab
        finally
            try Directory.Delete(root,true) with _ -> ()

    let remoteHeads f =
        [ f.AccountBare, AccountCapacityLedger.AccountRef
          f.A.Bare, "refs/heads/jarvis-orchestration"
          f.B.Bare, "refs/heads/jarvis-orchestration" ]
        |> List.map (fun (bare, branch) ->
            git f.Root [ "--git-dir=" + bare; "rev-parse"; branch ])

    let otherClones f =
        let t = f.Plan.AccountCommand.Target
        let targets =
            t.Projects
            |> List.mapi (fun i p ->
                { p with Binding = if i = 0 then f.A.Second else f.B.Second })
        { f.Plan with
            AccountCommand =
                { f.Plan.AccountCommand with
                    Target = { t with Account = f.AccountB; Projects = targets } }
            Projects =
                f.Plan.Projects |> List.mapi (fun i p -> {p with Target = targets[i]}) }

    let succeeded = function
        | Ok result -> result
        | Error reason -> failwithf "Fixture command incorrectly rejected: %s" reason

    let verifyHistorical lab project =
        let snapshot = LedgerStore.fetch project.Second |> ok
        for KeyValue(path, originalOid) in project.FrozenHistory do
            let after = historicalBlob project.Second snapshot path
            Assert.That(after, Is.EqualTo(originalOid), path)
        let source =
            git lab.Root [ "--git-dir=" + project.Bare; "rev-parse"; project.HistoricalSourceRef ]
        Assert.That(source, Is.EqualTo(project.HistoricalSourceSha))
        for KeyValue(id, hash) in project.Journal.OriginalRequestHashes do
            let operation =
                LedgerStore.getOperationAt project.Second snapshot (Guid.Parse id)
                |> ok |> Option.get
            Assert.That(operation.RequestHash, Is.EqualTo(hash), id)

    // Independent later project Git CAS. It does not alter the original WQ10
    // Prepared operation UUID, and intentionally does not fabricate authority.
    let damagePreparedReceipt project =
        let snapshot = LedgerStore.fetch project.Second |> ok
        let _, oid =
            LedgerStore.readAt project.Second snapshot WorkQueueProjectMigrationPrepare.ReceiptPath |> ok
        Assert.That(oid.IsSome, Is.True)
        LedgerStore.putRecord project.Second
            { OperationId = Guid.NewGuid()
              Actor = "independent disposable writer"
              Path = WorkQueueProjectMigrationPrepare.ReceiptPath
              ExpectedBlobOid = oid
              Content = "{}" }
        |> ok |> ignore

    // Replace with a VALID canonical WQ10 receipt through an unrelated second-
    // clone Git CAS. Neither the original UUID nor its signed result is edited.
    let rewriteValidPreparedReceipt project rewriteGraphVersion =
        let snapshot = LedgerStore.fetch project.Second |> ok
        let raw, oid =
            LedgerStore.readAt project.Second snapshot WorkQueueProjectMigrationPrepare.ReceiptPath |> ok
        let originalReceipt = CanonicalCodec.decode<ProjectPreparedReceipt> raw.Value
        let changed =
            if rewriteGraphVersion then
                { originalReceipt with GraphVersion = originalReceipt.GraphVersion + 1 }
            else
                { originalReceipt with OriginalOperationRequestHashes = Map.empty }
        let canonical = CanonicalCodec.encode changed
        Assert.That(canonical, Is.Not.EqualTo(raw.Value))
        LedgerStore.putRecord project.Second
            { OperationId = Guid.NewGuid()
              Actor = "independent valid-receipt fixture writer"
              Path = WorkQueueProjectMigrationPrepare.ReceiptPath
              ExpectedBlobOid = oid
              Content = canonical }
        |> ok |> ignore

    let preparedOperationBlob project (id: Guid) =
        let key = id.ToString("N")
        let snapshot = LedgerStore.fetch project.Second |> ok
        historicalBlob project.Second snapshot ("ops/" + key.Substring(0, 2) + "/" + key + ".json")

    let addUnrelatedAvailableStage project =
        let snapshot = LedgerStore.fetch project.Second |> ok
        let queue = WorkQueueGitLedger.loadAt project.Second snapshot |> ok
        let template = queue.Works |> Map.toList |> List.head |> snd
        let stage =
            { template with
                Id = "unrelated-disposable-runnable-stage"
                SubjectId = "unrelated-disposable-task"
                QueueId = "unrelated:independent-writer"
                State = WorkState.Available
                Gate = GateState.Clear
                AvailableAt = 1L
                AvailableSequence = queue.NextAvailability + 1L }
        let changed =
            { queue with
                NextAvailability = queue.NextAvailability + 1L
                Works = queue.Works.Add(stage.Id, stage) }
        let envelope: ProjectEnvelope =
            { SchemaVersion = 1
              ProjectId = project.Second.ProjectId.ToString("D")
              State = changed
              StateHash = CanonicalCodec.digest (CanonicalCodec.encode changed) }
        let _, oid = LedgerStore.readAt project.Second snapshot WorkQueueGitLedger.RecordPath |> ok
        LedgerStore.putRecord project.Second
            { OperationId = Guid.NewGuid()
              Actor = "independent disposable WQ02 writer"
              Path = WorkQueueGitLedger.RecordPath
              ExpectedBlobOid = oid
              Content = CanonicalCodec.encode envelope }
        |> ok |> ignore
        let observed = WorkQueueGitLedger.load project.Second |> ok |> snd
        Assert.That(Contracts.orderAvailable observed |> ok, Is.Not.Empty)

    let verifyInert project =
        let snapshot = LedgerStore.fetch project.Second |> ok
        let queue = WorkQueueGitLedger.loadAt project.Second snapshot |> ok
        Assert.That(queue.Migration, Is.EqualTo(MigrationEpoch.Preparing))
        Assert.That(Contracts.orderAvailable queue |> ok, Is.Empty)
        Assert.That(queue.Launches.IsEmpty, Is.True)
        Assert.That(queue.Works.Count, Is.EqualTo(1))
        let work = queue.Works |> Map.toList |> List.head |> snd
        Assert.That(work.State, Is.EqualTo(WorkState.Blocked))
        Assert.That(work.Gate, Is.Not.EqualTo(GateState.Clear))
        Assert.That(work.IntegrationAuthorized, Is.False)

    [<Test>]
    member _.AccountThenTwoProjectsUsesThreeOriginalCasAndPreservesHistory() =
        withFixture (fun f ->
            let before = remoteHeads f
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attempt f.Plan |> Result.isError, Is.True)
            Assert.That(remoteHeads f = before, Is.True)
            let run = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(run.Failure.IsNone, Is.True)
            Assert.That(run.AccountWrite.IsSome, Is.True)
            Assert.That(run.AccountWrite.Value.AlreadyApplied, Is.False)
            Assert.That(run.ProjectWrites.Count, Is.EqualTo(2))
            Assert.That(run.ProjectWrites |> Map.forall (fun _ r -> not r.AlreadyApplied), Is.True)
            let after = remoteHeads f
            Assert.That(List.forall2 (<>) before after, Is.True)
            let account = AccountCapacityLedger.fetch f.AccountB |> ok
            Assert.That(account.State.MigrationPhase, Is.EqualTo("Preparing"))
            Assert.That(account.State.Tokens, Is.Empty)
            Assert.That(run.Recovery.AccountHead, Is.EqualTo(Some account.Head))
            Assert.That(run.Recovery.AccountOriginalReceiptVerified, Is.True)
            Assert.That(run.Recovery.AllProjectsInertPrepared, Is.True)
            Assert.That((run.Recovery.Projects |> List.map _.Status) = ["PreparedInert"; "PreparedInert"], Is.True)
            Assert.That(run.Recovery.SchedulerEnabled || run.Recovery.ManagerEnabled ||
                        run.Recovery.ProductionCutoverAuthorized, Is.False)
            for project in [ f.A; f.B ] do
                verifyInert project
                verifyHistorical f project
            let _, accountOriginal = AccountCapacityLedger.readOperation f.AccountB
                                        f.Plan.AccountCommand.Target.OperationId |> ok
            Assert.That(accountOriginal.Value.Kind, Is.EqualTo("AccountMigrationPreparing")))

    [<Test>]
    member _.OriginalUuidReplaysOnOtherClonesWithIdenticalHeadsAndRequestHashes() =
        withFixture (fun f ->
            let initial = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(initial.Failure.IsNone, Is.True)
            let committed = remoteHeads f
            let replay = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture (otherClones f) |> succeeded
            Assert.That(replay.Failure.IsNone, Is.True)
            Assert.That(replay.AccountWrite.Value.AlreadyApplied, Is.True)
            Assert.That(replay.ProjectWrites |> Map.forall (fun _ r -> r.AlreadyApplied), Is.True)
            Assert.That(remoteHeads f = committed, Is.True)
            for id in [f.A.First.ProjectId;f.B.First.ProjectId] do
                Assert.That(replay.ProjectWrites[id].Operation.RequestHash,
                            Is.EqualTo(initial.ProjectWrites[id].Operation.RequestHash))
            let changed =
                { (otherClones f) with
                    Projects =
                        (otherClones f).Projects
                        |> List.mapi (fun i p ->
                            if i = 1 then {p with OriginalOperationId = Guid.NewGuid()} else p) }
            let conflicting = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture changed |> succeeded
            Assert.That(conflicting.Failure.IsSome, Is.True)
            Assert.That(remoteHeads f = committed, Is.True))

    [<Test>]
    member _.ProjectBStaleTwoCloneWriterLeavesPreparedAAndAccurateOperatorRecovery() =
        withFixture (fun f ->
            let competing () =
                LedgerStore.putRecord f.B.Second
                    { OperationId = Guid.NewGuid(); Actor = "competing original clone"
                      Path = "records/wq10-fixture/independent-writer.json"
                      ExpectedBlobOid = None
                      Content = """{"other":"writer"}""" }
                |> ok |> ignore
            let run =
                WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixtureWithInterposition
                    f.Plan competing |> succeeded
            Assert.That(run.Failure.IsSome, Is.True)
            Assert.That(run.ProjectWrites.Count, Is.EqualTo(1))
            Assert.That(run.Recovery.AccountOriginalReceiptVerified, Is.True)
            Assert.That(run.Recovery.AllProjectsInertPrepared, Is.False)
            Assert.That((run.Recovery.Projects |> List.map _.Status) = ["PreparedInert"; "NotPrepared"], Is.True)
            Assert.That(run.Recovery.Projects[1].ObservedHead,
                        Is.EqualTo(Some((LedgerStore.fetch f.B.Second |> ok).Commit)))
            Assert.That(run.Recovery.OperatorRepair |> List.exists (fun s -> s.Contains("force-reset")), Is.True)
            verifyInert f.A
            verifyHistorical f f.A
            verifyHistorical f f.B
            let beforeRetry = remoteHeads f
            let replay = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture (otherClones f) |> succeeded
            Assert.That(replay.Failure.IsSome, Is.True)
            Assert.That(replay.ProjectWrites[f.A.First.ProjectId].AlreadyApplied, Is.True)
            Assert.That(remoteHeads f = beforeRetry, Is.True)
            Assert.That((LedgerStore.fetch f.B.First |> ok).Commit,
                        Is.EqualTo(replay.Recovery.Projects[1].ObservedHead.Value)))

    [<Test>]
    member _.AccountHeadChangedAfterAStopsBAndShowsDivergedProjectReceipt() =
        withFixture (fun f ->
            let changeAccount () =
                let dir = f.AccountA.RepositoryPath
                git dir [ "fetch"; "origin";
                          AccountCapacityLedger.AccountRef + ":refs/heads/wq10-account-after-a" ]
                |> ignore
                git dir [ "checkout"; "wq10-account-after-a" ] |> ignore
                File.AppendAllText(Path.Combine(dir,"state.json"), "\n")
                git dir [ "add"; "state.json" ] |> ignore
                git dir [ "commit"; "-m"; "disposable account changes after A" ] |> ignore
                git dir [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore
            let run =
                WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixtureWithInterposition
                    f.Plan changeAccount |> succeeded
            Assert.That(run.Failure.IsSome, Is.True)
            Assert.That(run.ProjectWrites.Count, Is.EqualTo(1))
            Assert.That(run.Recovery.AllProjectsInertPrepared, Is.False)
            Assert.That((run.Recovery.Projects |> List.map _.Status) = ["AccountHeadDiverged"; "NotPrepared"], Is.True)
            Assert.That(run.Recovery.AccountHead,
                        Is.EqualTo(Some((AccountCapacityLedger.fetch f.AccountB |> ok).Head)))
            Assert.That(run.Recovery.Projects[0].Receipt.IsSome, Is.True)
            verifyInert f.A
            verifyHistorical f f.A)

    [<Test>]
    member _.MismatchUnknownSourceAndWrongMarkerNeverCommitAnyAuthority() =
        withFixture (fun f ->
            let initial = remoteHeads f
            let badProject = { f.Plan.Projects[1] with OriginalJournalId = Guid.NewGuid() }
            let mismatched =
                { f.Plan with Projects = [f.Plan.Projects[0]; badProject] }
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture mismatched
                        |> Result.isError, Is.True)
            let changedTarget =
                { f.Plan.Projects[1].Target with Binding = { f.B.First with Epoch = 2 } }
            let changedEpoch = { f.Plan.Projects[1] with Target = changedTarget }
            let wrongEpoch = { f.Plan with Projects = [ f.Plan.Projects[0]; changedEpoch ] }
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture wrongEpoch
                        |> Result.isError, Is.True)
            let foreignBinding =
                { f.B.First with ApprovedFetchUrl = f.A.Bare; ApprovedPushUrl = f.A.Bare }
            let foreignTarget =
                { f.Plan.Projects[1].Target with Binding = foreignBinding }
            let foreignProject = { f.Plan.Projects[1] with Target = foreignTarget }
            let foreignPlan = { f.Plan with Projects = [ f.Plan.Projects[0]; foreignProject ] }
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture foreignPlan
                        |> Result.isError, Is.True)
            let reusedUuid =
                { f.Plan.Projects[1] with OriginalOperationId = f.Plan.Projects[0].OriginalOperationId }
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture
                            { f.Plan with Projects = [ f.Plan.Projects[0]; reusedUuid ] } |> Result.isError, Is.True)
            let unknownTarget = { f.Plan.AccountCommand.Target with Actor = "unknown operator" }
            let unknownAccount = { f.Plan.AccountCommand with Target = unknownTarget }
            let unknown = { f.Plan with AccountCommand = unknownAccount }
            let denied = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture unknown |> succeeded
            Assert.That(denied.Failure.IsSome, Is.True)
            let badWater =
                { f.Plan.AccountCommand.OriginalSourceCheckpoints[0] with
                    OriginalSourceEffectHighWater = 99L }
            let alteredAccount =
                { f.Plan.AccountCommand with
                    OriginalSourceCheckpoints =
                        [| badWater; f.Plan.AccountCommand.OriginalSourceCheckpoints[1] |] }
            let wrongJournalWater = { f.Plan with AccountCommand = alteredAccount }
            Assert.That(WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture wrongJournalWater
                        |> Result.isError, Is.True)
            File.Delete(Path.Combine(f.Root, ".wq10-disposable-fixture"))
            let unmarked = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(unmarked.Failure.IsSome, Is.True)
            Assert.That(remoteHeads f = initial, Is.True))

    [<Test>]
    member _.ParallelTwoCloneOriginalCasProducesOneInertReceiptPerAuthority() =
        withFixture (fun f ->
            let first =
                Task.Run(fun () -> WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan)
            let second =
                Task.Run(fun () -> WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture (otherClones f))
            Task.WaitAll([|first :> Task;second :> Task|])
            let outcomes = [first.Result;second.Result]
            Assert.That(outcomes |> List.exists (function Ok r when r.AccountWrite.IsSome -> true | _ -> false),
                        Is.True, sprintf "No account Preparing winner: %A" outcomes)
            let repeat = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(repeat.Failure.IsNone, Is.True)
            Assert.That(repeat.AccountWrite.Value.AlreadyApplied, Is.True)
            Assert.That(repeat.ProjectWrites |> Map.forall (fun _ r -> r.AlreadyApplied), Is.True)
            Assert.That(repeat.Recovery.AllProjectsInertPrepared, Is.True)
            for p in [f.A;f.B] do
                verifyInert p
                verifyHistorical f p)

    [<Test>]
    member _.OriginalUuidReplayCannotHideLaterChangedPreparedReceipt() =
        withFixture (fun f ->
            let first = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(first.Failure.IsNone, Is.True)
            let second = otherClones f
            let replay =
                WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixtureWithInterposition
                    second (fun () -> damagePreparedReceipt f.B) |> succeeded
            Assert.That(replay.AccountWrite.Value.AlreadyApplied, Is.True)
            Assert.That(replay.ProjectWrites.Count, Is.EqualTo(2))
            Assert.That(replay.ProjectWrites |> Map.forall (fun _ receipt -> receipt.AlreadyApplied), Is.True)
            Assert.That(replay.Failure.IsSome, Is.True, "AlreadyApplied is not current three-ledger success")
            Assert.That(replay.Recovery.AccountOriginalReceiptVerified, Is.True)
            Assert.That(replay.Recovery.AllProjectsInertPrepared, Is.False)
            Assert.That(replay.Recovery.Projects[1].Status,
                        Is.AnyOf("RecoveryBlocked", "Unreadable"))
            Assert.That(replay.Recovery.Projects[1].ObservedHead,
                        Is.EqualTo(Some((LedgerStore.fetch f.B.Second |> ok).Commit)))
            verifyHistorical f f.A
            verifyHistorical f f.B)

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.OriginalUuidReplayRejectsCanonicalReceiptRewrite(rewriteGraphVersion: bool) =
        withFixture (fun f ->
            let first = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(first.Failure.IsNone, Is.True)
            let originalOperation = preparedOperationBlob f.B f.Plan.Projects[1].OriginalOperationId
            rewriteValidPreparedReceipt f.B rewriteGraphVersion
            // The independent mutation remains fully canonical and passes the
            // narrower WQ02 conversion reader; only original CAS proof rejects it.
            match WorkQueueProjectMigrationPrepare.inspect f.B.Second f.AccountB |> ok with
            | ProjectPreparedInspection.ProjectOnly _ -> ()
            | other -> Assert.Fail(sprintf "Valid independent receipt rewrite was not canonical: %A" other)
            let afterMutation = remoteHeads f
            let replay =
                WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture (otherClones f) |> succeeded
            Assert.That(replay.AccountWrite.Value.AlreadyApplied, Is.True)
            Assert.That(replay.ProjectWrites.Count, Is.EqualTo(2))
            Assert.That(replay.ProjectWrites |> Map.forall (fun _ op -> op.AlreadyApplied), Is.True)
            Assert.That(replay.Failure.IsSome, Is.True)
            Assert.That(replay.Recovery.AllProjectsInertPrepared, Is.False)
            Assert.That(replay.Recovery.Projects[0].Status, Is.EqualTo("PreparedInert"))
            Assert.That(replay.Recovery.Projects[1].Status, Is.EqualTo("RecoveryBlocked"))
            Assert.That(replay.Recovery.Projects[1].Issue.Value,
                        Does.Contain("original immutable CAS result hash"))
            Assert.That(replay.Recovery.Projects[1].ObservedHead,
                        Is.EqualTo(Some((LedgerStore.fetch f.B.Second |> ok).Commit)))
            Assert.That(remoteHeads f = afterMutation, Is.True)
            Assert.That(preparedOperationBlob f.B f.Plan.Projects[1].OriginalOperationId,
                        Is.EqualTo(originalOperation), "Immutable original operation result was rewritten")
            verifyHistorical f f.A
            verifyHistorical f f.B)

    [<Test>]
    member _.UnrelatedRunnableWorkAfterPreparedMustBlockRecovery() =
        withFixture (fun f ->
            let first = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture f.Plan |> succeeded
            Assert.That(first.Failure.IsNone, Is.True)
            addUnrelatedAvailableStage f.A
            // The original conversion receipt/stage remains valid. The old
            // project-only inspector cannot see the unrelated runnable stage.
            match WorkQueueProjectMigrationPrepare.inspect f.A.Second f.AccountB |> ok with
            | ProjectPreparedInspection.ProjectOnly _ -> ()
            | other -> Assert.Fail(sprintf "Expected original conversion to remain intact: %A" other)
            let recovery = WorkQueueMultiLedgerPreparedRecovery.inspect (otherClones f)
            Assert.That(recovery.AccountOriginalReceiptVerified, Is.True)
            Assert.That(recovery.AllProjectsInertPrepared, Is.False)
            Assert.That(recovery.Projects[0].Status, Is.EqualTo("RecoveryBlocked"))
            let replay = WorkQueueMultiLedgerPreparedRecovery.attemptOnDisposableFixture (otherClones f) |> succeeded
            Assert.That(replay.Failure.IsSome, Is.True)
            Assert.That(replay.ProjectWrites |> Map.forall (fun _ write -> write.AlreadyApplied), Is.True)
            Assert.That(replay.Recovery.Projects[0].Status, Is.EqualTo("RecoveryBlocked"))
            verifyHistorical f f.A
            verifyHistorical f f.B)
