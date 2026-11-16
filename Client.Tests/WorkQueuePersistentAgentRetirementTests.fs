module Client.Tests.WorkQueuePersistentAgentRetirementTests

open System
open System.Diagnostics
open System.IO
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueRoleAuthority
open Client.WorkQueuePersistentAgentRetirement
open Jarvis.WorkQueues
open NUnit.Framework

[<TestFixture>]
type WorkQueuePersistentAgentRetirementTests() =
    let verified = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected original Git evidence failure: %A" error

    let git directory arguments =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        runner.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
        arguments |> List.iter runner.StartInfo.ArgumentList.Add
        Assert.That(runner.Start(), Is.True)
        let stdout = runner.StandardOutput.ReadToEnd()
        let stderr = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Original Git evidence fixture timed out."
        Assert.That(runner.ExitCode, Is.EqualTo(0), stderr)
        stdout.Trim()

    let withTwoClonesFormat objectFormat setup action =
        let root = Path.Combine(Path.GetTempPath(), "work-agent-retirement-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let remote = Path.Combine(root, "source.git")
            let a = Path.Combine(root, "first")
            let b = Path.Combine(root, "second")
            let format = if objectFormat = "sha256" then ["--object-format=sha256"] else []
            git root (["init"; "--bare"; "--initial-branch=main"] @ format @ [remote]) |> ignore
            git root ["clone"; remote; a] |> ignore
            git a ["config"; "user.name"; "Retirement Fixture"] |> ignore
            git a ["config"; "user.email"; "retirement@test.invalid"] |> ignore
            File.WriteAllText(Path.Combine(a, "source.txt"), "Keep original source")
            git a ["add"; "source.txt"] |> ignore
            git a ["commit"; "-m"; "Original source"] |> ignore
            git a ["push"; "-u"; "origin"; "HEAD"] |> ignore
            git root ["clone"; remote; b] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding checkout =
                { RepositoryPath = checkout
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let first, second = binding a, binding b
            LedgerStore.initialize first true |> verified |> ignore
            if setup then
                WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid())
                    (Map.ofList ["Lead", Set.singleton "planning"])
                |> verified |> ignore
                WorkQueueRoleAuthority.initializeCatalog first "Fixture" (Guid.NewGuid())
                |> verified |> ignore
                WorkQueueRoleAuthority.configure first "Fixture" (Guid.NewGuid()) 0 0
                    { RoleId = "Lead"; Version = 1
                      Instructions = "Pinned Work role, not a legacy Agent"
                      Enabled = true
                      QueueSubscriptions = Set.singleton "planning" }
                |> verified |> ignore
            action first second
        finally
            try Directory.Delete(root, true) with _ -> ()

    let withTwoClones setup action =
        withTwoClonesFormat "sha1" setup action

    let publish (binding: LedgerBinding) id =
        let projectId = binding.ProjectId.ToString("D")
        let stage =
            { Id = id; ProjectId = projectId; SubjectId = "task-" + id
              Kind = WorkKind.Planning; QueueId = "planning"
              StageGeneration = 1; State = WorkState.Available
              Domain = DomainState.Undecided; Gate = GateState.Clear
              ClaimGeneration = 0
              Dependencies = Set.empty; WaivedDependencies = Map.empty
              AvailableAt = 100L; AvailableSequence = 1L
              SourceLineage = None; Push = None
              IntegrationAuthorized = false }
        WorkQueueGitLedger.executeInertFixtureAt binding "Fixture" (Guid.NewGuid())
            0 1000L (PublishStage stage) |> verified |> ignore

    [<Test>]
    member _.PinnedFullTreePreservesLegacyAgentHistoryAndOperationsWithoutWriting() =
        withTwoClones true (fun first second ->
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Legacy Worker"
            |> verified |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "legacy-retirement-task" "Preserve original review"
            |> verified |> ignore
            LedgerStore.putRecord first
                { OperationId = Guid.NewGuid(); Actor = "fixture"
                  Path = "records/agent-instructions/legacy.json"
                  ExpectedBlobOid = None; Content = "{}" }
            |> verified |> ignore
            LedgerStore.putRecord first
                { OperationId = Guid.NewGuid(); Actor = "fixture"
                  Path = "records/source-journal/legacy.json"
                  ExpectedBlobOid = None; Content = "{\"status\":\"original\"}" }
            |> verified |> ignore
            let head = (LedgerStore.fetch first |> verified).Commit
            let fromFirst = WorkQueuePersistentAgentRetirement.inspect first |> verified
            let fromSecond = WorkQueuePersistentAgentRetirement.inspect second |> verified
            Assert.That(fromFirst.VerifiedHead, Is.EqualTo(head))
            Assert.That(fromSecond.VerifiedHead, Is.EqualTo(head))
            Assert.That(fromFirst.HistoricalManifestSha256, Is.EqualTo(fromSecond.HistoricalManifestSha256))
            Assert.That(fromFirst.Entries = fromSecond.Entries, Is.True)
            Assert.That(fromFirst.Entries |> List.exists (fun entry ->
                entry.Family = "LegacyAgentIdentity" && entry.HistoricalOnly), Is.True)
            Assert.That(fromFirst.Entries |> List.exists (fun entry ->
                entry.Family = "LegacyAgentInstructions" && entry.HistoricalOnly), Is.True)
            Assert.That(fromFirst.Entries |> List.exists (fun entry ->
                entry.Family = "ImmutableOperationReceipt" && entry.HistoricalOnly), Is.True)
            Assert.That(fromFirst.Entries |> List.exists (fun entry ->
                entry.Path = "records/source-journal/legacy.json"), Is.True)
            Assert.That(fromFirst.LegacyAgentRecords, Is.EqualTo(1))
            Assert.That(fromFirst.CutoverAuthorized, Is.False)
            Assert.That(fromFirst.ReadOnly, Is.True)
            Assert.That((LedgerStore.fetch first |> verified).Commit, Is.EqualTo(head))
            Assert.That((LedgerStore.fetch second |> verified).Commit, Is.EqualTo(head)))

    [<Test>]
    member _.WorkOnlyProjectionDoesNotCreatePersistentAgentOrAuthorizeDispatch() =
        withTwoClones true (fun first second ->
            publish first "work-without-agent"
            let head = (LedgerStore.fetch first |> verified).Commit
            let result = WorkQueuePersistentAgentRetirement.inspect second |> verified
            Assert.That(result.VerifiedHead, Is.EqualTo(head))
            Assert.That(result.LegacyAgentRecords, Is.EqualTo(0))
            Assert.That(LedgerStore.listAt second (LedgerStore.fetch second |> verified) "agents"
                        |> verified, Is.Empty)
            match result.WorkProjection with
            | Error reason -> Assert.Fail("Work must be observable without an Agent roster: " + reason)
            | Ok view ->
                Assert.That((view.Work |> List.map _.WorkId) = ["work-without-agent"], Is.True)
                Assert.That(view.ReadOnly, Is.True)
                Assert.That(view.Work |> List.forall (fun work -> not work.ClaimEnabled), Is.True)
                Assert.That(view.Work |> List.forall (fun work -> work.ClaimLaunchId.IsNone), Is.True)
            Assert.That(result.CutoverAuthorized, Is.False)
            Assert.That(result.UnattestedRequirements.Length, Is.GreaterThan(0))
            Assert.That((LedgerStore.fetch first |> verified).Commit, Is.EqualTo(head)))

    [<Test>]
    member _.LegacyOnlyProjectRemainsHistoricalAndWorkUnavailable() =
        withTwoClones false (fun first second ->
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Historical Reviewer"
            |> verified |> ignore
            let original = (LedgerStore.fetch first |> verified).Commit
            let preview = WorkQueuePersistentAgentRetirement.inspect second |> verified
            Assert.That(preview.LegacyAgentRecords, Is.EqualTo(1))
            Assert.That(preview.CutoverAuthorized, Is.False)
            Assert.That(Result.isError preview.WorkProjection, Is.True)
            Assert.That(preview.UnattestedRequirements |> List.exists (fun issue ->
                issue.Contains("WQ02 Work snapshot")), Is.True)
            Assert.That((LedgerStore.fetch first |> verified).Commit, Is.EqualTo(original)))

    [<Test>]
    member _.Sha256GitHistoryIsPinnedWithoutLegacyAgentOrClaimFallback() =
        withTwoClonesFormat "sha256" true (fun first second ->
            publish first "sha256-work"
            let inventory = WorkQueuePersistentAgentRetirement.inspect second |> verified
            Assert.That(inventory.VerifiedHead.Length, Is.EqualTo(64))
            Assert.That(inventory.Entries |> List.forall (fun item -> item.BlobOid.Length = 64), Is.True)
            Assert.That(inventory.LegacyAgentRecords, Is.EqualTo(0))
            Assert.That(inventory.CutoverAuthorized, Is.False)
            match inventory.WorkProjection with
            | Error reason -> Assert.Fail("SHA256 Work must remain a read-only observation: " + reason)
            | Ok view -> Assert.That(view.ReadOnly && (view.Work |> List.forall (fun item -> not item.ClaimEnabled)), Is.True))

    [<Test>]
    member _.NewHistoricalWriteMovesHeadButCannotRetroactivelyChangeFrozenManifest() =
        withTwoClones true (fun first second ->
            let earlier = WorkQueuePersistentAgentRetirement.inspect second |> verified
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Legacy After Snapshot"
            |> verified |> ignore
            let later = WorkQueuePersistentAgentRetirement.inspect second |> verified
            Assert.That(later.VerifiedHead, Is.Not.EqualTo(earlier.VerifiedHead))
            Assert.That(later.HistoricalManifestSha256, Is.Not.EqualTo(earlier.HistoricalManifestSha256))
            Assert.That(earlier.LegacyAgentRecords, Is.EqualTo(0))
            Assert.That(later.LegacyAgentRecords, Is.EqualTo(1))
            Assert.That(earlier.CutoverAuthorized || later.CutoverAuthorized, Is.False))
