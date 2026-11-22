module Client.Tests.WorkQueueProjectMigrationPrepareTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Client.LedgerStore
open Client.AccountCapacityLedger
open Client.WorkQueueGitLedger
open Client.WorkCompositionGitLedger
open Client.WorkQueueProjectMigrationPrepare
open Client.ReviewWorkflow
open Jarvis.WorkQueues

[<TestFixture>]
type WorkQueueProjectMigrationPrepareTests() =
    let ok = function
        | Ok value -> value
        | Error err -> failwithf "Fixture operation rejected: %A" err

    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        runner.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
        args |> List.iter runner.StartInfo.ArgumentList.Add
        Assert.That(runner.Start(), Is.True)
        let stdout = runner.StandardOutput.ReadToEnd()
        let stderr = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Test Git command timed out."
        Assert.That(runner.ExitCode, Is.EqualTo(0), stderr)
        stdout.Trim()

    let repo root name =
        let bare, first, second =
            Path.Combine(root, name + ".git"),
            Path.Combine(root, name + "-first"),
            Path.Combine(root, name + "-second")
        git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
        git root [ "clone"; bare; first ] |> ignore
        git first [ "config"; "user.name"; "WQ10 Test" ] |> ignore
        git first [ "config"; "user.email"; "wq10@test.invalid" ] |> ignore
        File.WriteAllText(Path.Combine(first, "legacy.txt"), "do not overwrite original source")
        git first [ "add"; "legacy.txt" ] |> ignore
        git first [ "commit"; "-m"; "original legacy source" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; bare; second ] |> ignore
        bare, first, second

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "wq10-positive-" + Guid.NewGuid().ToString("N"))
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
            let ab, a1, a2 = repo root "account"
            let pb, p1, p2 = repo root "project"
            let accountId, accountLedger = Guid.NewGuid(), Guid.NewGuid()
            let projectId, projectLedger = Guid.NewGuid(), Guid.NewGuid()
            let account path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = ab; ApprovedPushUrl = ab
                  AccountId = accountId; LedgerId = accountLedger; Epoch = 1 }
            let project path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = pb; ApprovedPushUrl = pb
                  ProjectId = projectId; LedgerId = projectLedger; Epoch = 1 }
            let first, second, a, aOther = project p1, project p2, account a1, account a2
            AccountCapacityLedger.initialize a 2 |> ok |> ignore
            LedgerStore.initialize first true |> ok |> ignore
            WorkQueueGitLedger.initialize first "WQ10 test" (Guid.NewGuid()) Map.empty |> ok |> ignore
            // A fixture author creates a disposable Frozen account state: this is
            // NOT an operational account migration authority or verified old-writer fence.
            git a.RepositoryPath [ "fetch"; "origin"; AccountCapacityLedger.AccountRef + ":refs/heads/wq10-frozen" ] |> ignore
            git a.RepositoryPath [ "checkout"; "wq10-frozen" ] |> ignore
            let state = Path.Combine(a.RepositoryPath, "state.json")
            let previous = File.ReadAllText state
            let frozen = previous.Replace("\"migrationPhase\":\"LegacyActive\"", "\"migrationPhase\":\"Frozen\"")
            Assert.That(frozen, Is.Not.EqualTo(previous))
            File.WriteAllText(state, frozen)
            git a.RepositoryPath [ "add"; "state.json" ] |> ignore
            git a.RepositoryPath [ "commit"; "-m"; "disposable frozen account fixture only" ] |> ignore
            git a.RepositoryPath [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore
            // Immutable legacy Agent/task/independent review history: do not
            // rewrite this evidence while preparing the Work conversion.
            let worker, reviewer, taskId = "Legacy Worker", "Legacy Reviewer", "wq10-legacy-task"
            let agentOp, taskOp, reviewOp = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            ReviewWorkflow.registerAgent first agentOp worker |> ok |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> ok |> ignore
            ReviewWorkflow.createTask first taskOp taskId "Preserve history" |> ok |> ignore
            git first.RepositoryPath [ "checkout"; "-b"; "feature/wq10-historical-source" ] |> ignore
            File.WriteAllText(Path.Combine(first.RepositoryPath, "reviewed.txt"), "original reviewed source")
            git first.RepositoryPath [ "add"; "reviewed.txt" ] |> ignore
            git first.RepositoryPath [ "commit"; "-m"; "frozen historical source" ] |> ignore
            let sourceSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
            git first.RepositoryPath [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            ReviewWorkflow.submitBranchReview first reviewOp taskId worker
                (Guid.NewGuid()) "refs/heads/feature/wq10-historical-source"
                sourceSha "Preserve original review" "Original history"
                "refs/heads/main" |> ok |> ignore
            let originalJournalId = Guid.NewGuid()
            let snapshot = LedgerStore.fetch first |> ok
            let originals =
                [ agentOp; taskOp; reviewOp ]
                |> List.map (fun id ->
                    let operation = LedgerStore.getOperationAt first snapshot id |> ok |> Option.get
                    id.ToString("D"), operation.RequestHash, operation.Sequence)
            let journal: DisposableOriginalJournal =
                { SchemaVersion = 1
                  JournalId = originalJournalId.ToString("D")
                  OriginalHighWater = originals |> List.map (fun (_, _, seq) -> seq) |> List.max
                  OriginalSourceEffectHighWater = 0L
                  OriginalRequestHashes =
                    originals |> List.map (fun (id, hash, _) -> id, hash) |> Map.ofList }
            LedgerStore.putRecord first
                { OperationId = Guid.NewGuid(); Actor = "disposable-test-producer"
                  Path = WorkQueueProjectMigrationPrepare.FixtureJournalPath
                  ExpectedBlobOid = None
                  Content = CanonicalCodec.encode journal }
            |> ok |> ignore
            let current = LedgerStore.fetch first |> ok
            let accountNow = AccountCapacityLedger.fetch a |> ok
            let command =
                { OriginalOperationId = Guid.NewGuid()
                  Actor = "WQ10 test-only host"
                  Project = first
                  ExpectedProjectHead = current.Commit
                  Account = a
                  ExpectedAccountHead = accountNow.Head
                  ExpectedAccountStateVersion = accountNow.State.StateVersion
                  ReviewedReportSha256 = String('a',64)
                  OriginalJournalId = originalJournalId
                  DisposableRoot = root }
            action root first second a aOther command sourceSha
        finally
            try Directory.Delete(root,true) with _ -> ()

    [<Test>]
    member _.GroupOrWorldWritableDisposableRootCannotPrepareProject() =
        if not (OperatingSystem.IsWindows()) then
            withFixture (fun root first _ _ _ command _ ->
                let originalHead = (LedgerStore.fetch first |> ok).Commit
                let privateMode = File.GetUnixFileMode root
                try
                    for unsafeBit in [ UnixFileMode.GroupWrite; UnixFileMode.OtherWrite ] do
                        File.SetUnixFileMode(root, privateMode ||| unsafeBit)
                        match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command with
                        | Error(InvalidBinding _) -> ()
                        | other -> Assert.Fail(sprintf "Writable disposable root accepted: %A" other)
                        Assert.That((LedgerStore.fetch first |> ok).Commit, Is.EqualTo(originalHead))
                finally
                    File.SetUnixFileMode(root, privateMode))

    [<Test>]
    member _.PositiveProjectPreparedIsOneAtomicInertGitCasWithOriginalHistory() =
        withFixture (fun root first second account accountOther command originalSha ->
            let before = LedgerStore.fetch second |> ok
            let legacyPaths = LedgerStore.listAt first before "agents" |> ok
            Assert.That(legacyPaths.Length, Is.GreaterThan(0))
            let preimages = legacyPaths |> List.map (fun path ->
                let _, blob = LedgerStore.readAt first before path |> ok
                path, blob)
            let noGo = WorkQueueProjectMigrationPrepare.attempt command
            Assert.That(Result.isError noGo, Is.True)
            Assert.That((LedgerStore.fetch first |> ok).Commit, Is.EqualTo(before.Commit))
            let write = WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command |> ok
            Assert.That(write.AlreadyApplied, Is.False)
            Assert.That(write.Operation.Paths |> Array.toList,
                        Is.EquivalentTo(
                            [ WorkQueueProjectMigrationPrepare.ReceiptPath
                              WorkCompositionGitLedger.RecordPath
                              WorkQueueGitLedger.RecordPath ]))
            let after = LedgerStore.fetch second |> ok
            Assert.That(after.Commit, Is.Not.EqualTo(before.Commit))
            let queue = WorkQueueGitLedger.loadAt second after |> ok
            let graph, _ = WorkCompositionGitLedger.loadAt second after |> ok
            Assert.That(queue.Migration, Is.EqualTo(MigrationEpoch.Preparing))
            Assert.That(Contracts.orderAvailable queue |> ok, Is.Empty)
            let stage = queue.Works |> Map.toSeq |> Seq.exactlyOne |> snd
            Assert.That(stage.State, Is.EqualTo(WorkState.Blocked))
            Assert.That(stage.Gate, Is.Not.EqualTo(GateState.Clear))
            Assert.That(stage.QueueId, Is.EqualTo("wq10:inert-conversion"))
            let unit = graph.Units |> Map.toSeq |> Seq.map snd
                       |> Seq.find (fun unit -> unit.Spec.LinkedTaskId.IsSome)
            Assert.That(unit.Spec.LinkedTaskId, Is.EqualTo(Some "wq10-legacy-task"))
            for path, blob in preimages do
                let _, observed = LedgerStore.readAt second after path |> ok
                Assert.That(observed, Is.EqualTo(blob), path)
            Assert.That(git root [ "--git-dir=" + first.ApprovedPushUrl; "rev-parse";
                                  "refs/heads/feature/wq10-historical-source" ], Is.EqualTo(originalSha))
            match WorkQueueProjectMigrationPrepare.inspect second accountOther |> ok with
            | ProjectPreparedInspection.ProjectOnly receipt ->
                Assert.That(receipt.RuntimeAuthorized, Is.False)
                Assert.That(receipt.FrozenBaseHead, Is.EqualTo(before.Commit))
                Assert.That(receipt.Conversion.Count, Is.EqualTo(1))
                Assert.That(receipt.LegacyPaths.ContainsKey(legacyPaths.Head), Is.True)
                Assert.That(receipt.Conversion.["wq10-legacy-task"].WorkId, Is.EqualTo(stage.Id))
                Assert.That(receipt.OriginalOperationRequestHashes.Count, Is.EqualTo(3))
            | state -> Assert.Fail(sprintf "Expected inert project-only Prepared: %A" state))

    [<Test>]
    member _.ExactUuidReplaysAcrossClonesWhileStaleOrConflictingCommandsDoNotWrite() =
        withFixture (fun _ first second _ _ command _ ->
            let original = WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command |> ok
            let head = (LedgerStore.fetch first |> ok).Commit
            let replay = WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                            { command with Project = second } |> ok
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.OriginalResultHash, Is.EqualTo(original.Operation.OriginalResultHash))
            Assert.That((LedgerStore.fetch second |> ok).Commit, Is.EqualTo(head))
            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                     { command with Project = second; ReviewedReportSha256 = String('b',64) } with
            | Error OperationIdReused -> ()
            | failure -> Assert.Fail(sprintf "UUID reused with modified intent: %A" failure)
            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                     { command with Project = second; OriginalOperationId = Guid.NewGuid() } with
            | Error(StaleEntityVersion _) | Error(InvalidBinding _) -> ()
            | failure -> Assert.Fail(sprintf "Stale competing original operation was not rejected: %A" failure)
            Assert.That((LedgerStore.fetch first |> ok).Commit, Is.EqualTo(head)))

    [<Test>]
    member _.UnverifiedOriginalJournalAndPartialAccountFailureAreFailClosed() =
        withFixture (fun _ first second account accountOther command _ ->
            let before = (LedgerStore.fetch first |> ok).Commit
            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                     { command with OriginalJournalId = Guid.NewGuid() } with
            | Error(InvalidBinding _) -> ()
            | failure -> Assert.Fail(sprintf "Unverified journal accepted: %A" failure)
            Assert.That((LedgerStore.fetch second |> ok).Commit, Is.EqualTo(before))
            let write = WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command |> ok
            Assert.That(write.AlreadyApplied, Is.False)
            // Project and account ledgers cannot commit atomically. A later
            // account head change requires reconciliation, never a rollback or GO.
            let phase = Path.Combine(account.RepositoryPath, "state.json")
            File.AppendAllText(phase, "\n")
            git account.RepositoryPath [ "add"; "state.json" ] |> ignore
            git account.RepositoryPath [ "commit"; "-m"; "account changed after project only" ] |> ignore
            git account.RepositoryPath [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore
            match WorkQueueProjectMigrationPrepare.inspect second accountOther |> ok with
            | ProjectPreparedInspection.AccountHeadDiverged(receipt, _) ->
                Assert.That(receipt.RuntimeAuthorized, Is.False)
            | state -> Assert.Fail(sprintf "Cross-ledger partial recovery not surfaced: %A" state)
            Assert.That((LedgerStore.fetch first |> ok).Commit, Is.EqualTo(write.CurrentHead)))

    [<Test>]
    member _.StaleHeadAfterIndependentCloneWriterAndUnmarkedAuthorityNeverPrepare() =
        withFixture (fun root first second _ _ command _ ->
            let before = (LedgerStore.fetch first |> ok).Commit
            let path = "records/wq10-fixture/competing.json"
            LedgerStore.putRecord second
                { OperationId = Guid.NewGuid(); Actor = "competing clone"
                  Path = path; ExpectedBlobOid = None; Content = """{"writer":"competing"}""" }
            |> ok |> ignore
            let moved = (LedgerStore.fetch first |> ok).Commit
            Assert.That(moved, Is.Not.EqualTo(before))
            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command with
            | Error(StaleEntityVersion _) -> ()
            | failure -> Assert.Fail(sprintf "Unexpected stale head handling: %A" failure)
            Assert.That((LedgerStore.fetch second |> ok).Commit, Is.EqualTo(moved))
            File.Delete(Path.Combine(root, ".wq10-disposable-fixture"))
            match WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                     { command with ExpectedProjectHead = moved; OriginalOperationId = Guid.NewGuid() } with
            | Error(InvalidBinding _) -> ()
            | failure -> Assert.Fail(sprintf "Unmarked authority was not blocked: %A" failure)
            Assert.That((LedgerStore.fetch first |> ok).Commit, Is.EqualTo(moved)))


    [<Test>]
    member _.ConcurrentTwoClonePrepareHasOneWinnerAndNoRunnableWork() =
        withFixture (fun _ first second account accountOther command _ ->
            let one =
                Task.Run(fun () ->
                    WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture command)
            let two =
                Task.Run(fun () ->
                    WorkQueueProjectMigrationPrepare.attemptOnDisposableFixture
                        { command with Project = second
                                       Account = accountOther
                                       OriginalOperationId = Guid.NewGuid() })
            Task.WaitAll([| one :> Task; two :> Task |])
            let outcomes = [ one.Result; two.Result ]
            let winner = outcomes |> List.choose (function Ok receipt -> Some receipt | _ -> None)
            Assert.That(winner.Length, Is.EqualTo(1), sprintf "Expected one CAS winner, got %A" outcomes)
            Assert.That(outcomes |> List.exists (function
                | Error(StaleEntityVersion _) -> true
                | _ -> false), Is.True, "The competing Frozen project writer must not silently publish.")
            let snapshot = LedgerStore.fetch first |> ok
            let queue = WorkQueueGitLedger.loadAt first snapshot |> ok
            Assert.That(queue.Receipts.Count, Is.EqualTo(1))
            Assert.That(queue.Works.Count, Is.EqualTo(1))
            Assert.That(Contracts.orderAvailable queue |> ok, Is.Empty)
            match WorkQueueProjectMigrationPrepare.inspect second accountOther |> ok with
            | ProjectPreparedInspection.ProjectOnly receipt ->
                Assert.That(receipt.RuntimeAuthorized, Is.False)
            | state -> Assert.Fail(sprintf "Inert winner not recoverable: %A" state))
