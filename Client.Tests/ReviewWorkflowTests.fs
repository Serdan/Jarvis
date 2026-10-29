module Client.Tests.ReviewWorkflowTests

open System
open System.Diagnostics
open System.IO
open Client.LedgerStore
open Client.ReviewWorkflow
open NUnit.Framework

[<TestFixture>]
type ReviewWorkflowTests() =
    let git dir args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- dir
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Cannot launch Git fixture."
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then failwithf "Git fixture error: %s" stderr
        stdout.Trim()

    let success = function
        | Ok value -> value
        | Error failure -> failwithf "Unexpected ledger result: %A" failure

    let expectStale = function
        | Error(StaleEntityVersion _) | Error(ReviewVersionConflict _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale/version conflict: %A" other)

    let withFixture action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-review-v2-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let one = Path.Combine(root, "owner")
            let two = Path.Combine(root, "reviewer")
            git root [ "init"; "--bare"; bare ] |> ignore
            git root [ "clone"; bare; one ] |> ignore
            for clone in [ one ] do
                git clone [ "config"; "user.name"; "V2 Fixture" ] |> ignore
                git clone [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "base")
            git one [ "add"; "source.txt" ] |> ignore
            git one [ "commit"; "-m"; "task base" ] |> ignore
            git one [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; two ] |> ignore
            git two [ "config"; "user.name"; "V2 Fixture" ] |> ignore
            git two [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding path =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let first = binding one
            let second = binding two
            LedgerStore.initialize first true |> success |> ignore
            for id in [ "Owner"; "Reviewer" ] do
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) id |> success |> ignore
            let baseOid = git one [ "rev-parse"; "HEAD" ]
            let sourceRef = "refs/heads/" + git one [ "branch"; "--show-current" ]
            let newCommit (title: string) (content: string) =
                File.WriteAllText(Path.Combine(one, "source.txt"), content)
                git one [ "add"; "source.txt" ] |> ignore
                git one [ "commit"; "-m"; title ] |> ignore
                git one [ "rev-parse"; "HEAD" ]
            let initTask taskId =
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId taskId |> success |> ignore
                let attemptId = Guid.NewGuid()
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
                |> success |> ignore
                ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
                ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner" 2 1 baseOid sourceRef
                |> success |> ignore
            action one two first second baseOid sourceRef newCommit initTask
        finally
            for checkout in [ "owner"; "reviewer" ] do
                let pendingConfig =
                    Client.V2ReviewApi.ReviewV2Binding.pathForRoot(
                        Path.Combine(root, checkout))
                let pendingDir =
                    Path.Combine(Path.GetDirectoryName pendingConfig, "pending",
                                 Path.GetFileNameWithoutExtension pendingConfig)
                if Directory.Exists pendingDir then Directory.Delete(pendingDir, true)
            Directory.Delete(root, true)

    [<Test>]
    member _.AgentStateTimestampsAndImmutableWorkflowEventsSurviveAcrossClients() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "turn-timing"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Timed task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            let assignedTime = DateTimeOffset.Parse assigned.Agent.StateEnteredAtUtc
            Assert.That(assignedTime, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1
            |> success |> ignore
            let working = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(working.Agent.State, Is.EqualTo("Working"))
            let startTime = DateTimeOffset.Parse working.Agent.StateEnteredAtUtc
            Assert.That(startTime, Is.GreaterThanOrEqualTo(assignedTime))
            let agentSnapshot = LedgerStore.fetch second |> success
            let agentContent, _ = LedgerStore.readAt second agentSnapshot "agents/T3duZXI.json" |> success
            use agentJson = System.Text.Json.JsonDocument.Parse(agentContent.Value)
            for redundant in [ "lastTurnDurationSeconds"; "totalWorkingSeconds"; "workingTurnCount" ] do
                let mutable ignored = Unchecked.defaultof<System.Text.Json.JsonElement>
                Assert.That(agentJson.RootElement.TryGetProperty(redundant, &ignored), Is.False, redundant)
            ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) taskId "Owner" 2 3 1
                "Mid-turn checkpoint" "Continue" null null
            |> success |> ignore
            let checkpointed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(checkpointed.Agent.StateEnteredAtUtc, Is.EqualTo(working.Agent.StateEnteredAtUtc))
            let snapshot = LedgerStore.fetch second |> success
            let eventPaths = LedgerStore.listAt second snapshot "tasks" |> success
            let eventPaths = eventPaths |> List.filter (fun path ->
                path.Contains("/checkpoints/") || path.Contains("/assignments/"))
            Assert.That(eventPaths.Length, Is.GreaterThanOrEqualTo(2))
            for path in eventPaths do
                let content, _ = LedgerStore.readAt second snapshot path |> success
                use doc = System.Text.Json.JsonDocument.Parse(content.Value)
                let occurred = doc.RootElement.GetProperty("occurredAtUtc").GetString()
                Assert.That(DateTimeOffset.Parse occurred, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 3 3 1
                "Done one unit" "Continue" |> success |> ignore
            let ready = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(DateTimeOffset.Parse ready.Agent.StateEnteredAtUtc,
                        Is.GreaterThanOrEqualTo(startTime))
            ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 4 4 1
            |> success |> ignore
            let resumed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(resumed.Agent.State, Is.EqualTo("Working"))
            Assert.That(DateTimeOffset.Parse resumed.Agent.StateEnteredAtUtc,
                        Is.GreaterThanOrEqualTo(DateTimeOffset.Parse ready.Agent.StateEnteredAtUtc))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 5 5 1 "Finished"
            |> success |> ignore
            let completed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(completed.Agent.State, Is.EqualTo("Completed"))
            Assert.That(DateTimeOffset.Parse completed.Agent.StateEnteredAtUtc,
                        Is.GreaterThanOrEqualTo(DateTimeOffset.Parse resumed.Agent.StateEnteredAtUtc)))

    [<Test>]
    member _.WorkingSessionRecoveryRetainsHistoryResetsOnlyTurnAndSurvivesClone() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "session-recovery"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Session recovery"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            ReviewWorkflow.beginAttemptWithSession first (Guid.NewGuid()) taskId "Owner" 1 1 "session-A"
            |> success |> ignore
            let initial = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(initial.AssociatedSessionIds = [ "session-A" ], Is.True)
            Assert.That(initial.ActiveSessionId, Is.EqualTo("session-A"))
            Assert.That(initial.WorkingTurnStartedAtUtc, Is.EqualTo(initial.StateEnteredAtUtc))
            ReviewWorkflow.resumeAgentWithSession first (Guid.NewGuid()) taskId "Owner" 2 3 1 "session-B"
            |> success |> ignore
            let recovered = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(recovered.State, Is.EqualTo("Working"))
            Assert.That(recovered.StateEnteredAtUtc, Is.EqualTo(initial.StateEnteredAtUtc))
            Assert.That(DateTimeOffset.Parse recovered.WorkingTurnStartedAtUtc,
                        Is.GreaterThanOrEqualTo(DateTimeOffset.Parse initial.WorkingTurnStartedAtUtc))
            Assert.That(recovered.AssociatedSessionIds = [ "session-A"; "session-B" ], Is.True)
            Assert.That(recovered.ActiveSessionId, Is.EqualTo("session-B"))
            // The second Git clone has no in-memory session cache; resolving
            // the association and its response guidance must work anyway.
            let coldActive = ReviewWorkflow.findWorkingAgentBySession second "session-B"
                             |> success |> Option.get
            Assert.That(coldActive.AgentId, Is.EqualTo("Owner"))
            let coldOld = ReviewWorkflow.findWorkingAgentBySession second "session-A"
                          |> success |> Option.get
            let atCutoff = DateTimeOffset.Parse(recovered.WorkingTurnStartedAtUtc).AddMinutes(26)
            let activeNotice = Client.WorkingTurnGuidance.forSession atCutoff "session-B" coldActive
                               |> Option.get
            Assert.That(fst activeNotice, Is.EqualTo("working-turn"))
            Assert.That(snd activeNotice, Does.Contain("SOFT STOP"))
            let oldNotice = Client.WorkingTurnGuidance.forSession atCutoff "session-A" coldOld
                            |> Option.get
            Assert.That(fst oldNotice, Is.EqualTo("session-status"))
            Assert.That(snd oldNotice, Does.Contain("session-B"))
            Assert.That(ReviewWorkflow.findWorkingAgentBySession second "unrelated-session" |> success,
                        Is.EqualTo(None))
            let snapshot = LedgerStore.fetch second |> success
            let paths = LedgerStore.listAt second snapshot "tasks" |> success
            let recoveryEvents = paths |> List.filter (fun path -> path.Contains("/events/"))
            Assert.That(recoveryEvents.Length, Is.EqualTo(1))
            let eventJson, _ = LedgerStore.readAt second snapshot recoveryEvents.Head |> success
            use eventDoc = System.Text.Json.JsonDocument.Parse(eventJson.Value)
            Assert.That(eventDoc.RootElement.GetProperty("kind").GetString(), Is.EqualTo("AgentStallRecovered"))
            Assert.That(DateTimeOffset.Parse(eventDoc.RootElement.GetProperty("occurredAtUtc").GetString()),
                        Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
            // The low-level mutation refuses to write a duplicate recovery.
            // The MCP API presents it as a read-only AlreadyActive response.
            match ReviewWorkflow.resumeAgentWithSession first (Guid.NewGuid()) taskId "Owner" 2 4 1 "session-B" with
            | Error(StaleEntityVersion message) ->
                Assert.That(message, Does.Contain("already associated"))
            | outcome -> Assert.Fail(sprintf "Duplicate recovery incorrectly wrote an agent record: %A" outcome)
            let repeated = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(repeated.Version, Is.EqualTo(recovered.Version))
            Assert.That(repeated.WorkingTurnStartedAtUtc, Is.EqualTo(recovered.WorkingTurnStartedAtUtc))
            Assert.That(repeated.AssociatedSessionIds = recovered.AssociatedSessionIds, Is.True)
            let snapshot2 = LedgerStore.fetch second |> success
            let events2 = LedgerStore.listAt second snapshot2 "tasks" |> success
                          |> List.filter (fun path -> path.Contains("/events/"))
            Assert.That(events2.Length, Is.EqualTo(1))
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 2 4 1
                "Stopped correctly" "Continue later" |> success |> ignore
            let ready = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(ready.State, Is.EqualTo("Ready"))
            Assert.That(ready.ActiveSessionId, Is.Null)
            Assert.That(ready.WorkingTurnStartedAtUtc, Is.Null)
            Assert.That(ready.AssociatedSessionIds = [ "session-A"; "session-B" ], Is.True)
            ReviewWorkflow.resumeAgentWithSession first (Guid.NewGuid()) taskId "Owner" 3 5 1 "session-C"
            |> success |> ignore
            let resumed = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(resumed.AssociatedSessionIds = [ "session-A"; "session-B"; "session-C" ], Is.True)
            Assert.That(resumed.ActiveSessionId, Is.EqualTo("session-C"))
            Assert.That(resumed.State, Is.EqualTo("Working"))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 4 6 1 "Done"
            |> success |> ignore
            let complete = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(complete.State, Is.EqualTo("Completed"))
            ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" complete.Version "Idle"
            |> success |> ignore
            let parked = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(parked.State, Is.EqualTo("Parked"))
            Assert.That(parked.AssociatedSessionIds = [ "session-A"; "session-B"; "session-C" ], Is.True)
            Assert.That(parked.ActiveSessionId, Is.Null))

    [<Test>]
    member _.ResumeApiReportsCurrentAndSupersededSessionWithoutExtraCommits() =
        withFixture (fun one _ first second _ _ _ _ ->
            let taskId = "resume-advisory"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Resume advisory"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            ReviewWorkflow.beginAttemptWithSession first (Guid.NewGuid()) taskId "Owner" 1 1 "session-A"
            |> success |> ignore
            let approved : Client.V2ReviewApi.ReviewV2OptIn =
                { SchemaVersion = 1
                  Enabled = true
                  ProjectRoot = one
                  ProjectId = first.ProjectId.ToString("D")
                  LedgerId = first.LedgerId.ToString("D")
                  Epoch = first.Epoch
                  RemoteName = first.RemoteName
                  ApprovedFetchUrl = first.ApprovedFetchUrl
                  ApprovedPushUrl = first.ApprovedPushUrl
                  HighestTrustedHead = (LedgerStore.fetch first |> success).Commit }
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(approved))
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let resume (sessionId: string) expectedAgentVersion : Common.V2TaskWriteCommand =
                { ProjectName = "owner"; Action = "ResumeAgent"
                  OperationId = Guid.NewGuid().ToString("D")
                  TaskId = taskId; AttemptId = ""; ActorId = "Owner"; OwnerId = ""; ReviewerId = ""
                  Title = ""; Html = ""; ExpectedVersion = 2
                  ExpectedAgentVersion = expectedAgentVersion; ExpectedTaskVersion = 0
                  AssignmentGeneration = 1; Summary = ""; NextAction = ""; Reason = ""
                  BaseOid = ""; TargetRef = ""; SourceRef = ""; AllowDetached = false
                  AllowUncommitted = false; DecisionSequence = 0L; Evidence = ""
                  SessionId = sessionId }
            let api cmd =
                match Client.V2ReviewApi.V2TaskApi.write cmd ctx with
                | Ok value -> value
                | Error failure -> failwithf "Unexpected resume response: %A" failure
            let before = LedgerStore.fetch second |> success
            let active = api (resume "session-A" 3)
            use activeJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(active))
            Assert.That(activeJson.RootElement.GetProperty("status").GetString(), Is.EqualTo("AlreadyActive"))
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before.Commit))
            let oldAgent = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(oldAgent.Version, Is.EqualTo(3))
            ReviewWorkflow.resumeAgentWithSession first (Guid.NewGuid()) taskId "Owner" 2 3 1 "session-B"
            |> success |> ignore
            let afterRecovery = LedgerStore.fetch second |> success
            let stale = api (resume "session-A" 4)
            use staleJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(stale))
            Assert.That(staleJson.RootElement.GetProperty("status").GetString(), Is.EqualTo("SupersededSession"))
            Assert.That(staleJson.RootElement.GetProperty("activeSessionId").GetString(),
                        Is.EqualTo("session-B"))
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(afterRecovery.Commit))
            let current = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            Assert.That(current.ActiveSessionId, Is.EqualTo("session-B"))
            Assert.That(current.Version, Is.EqualTo(4))
            Assert.That(current.AssociatedSessionIds = [ "session-A"; "session-B" ], Is.True))

    [<Test>]
    member _.CanonicalHtmlAcceptsExact512KiBAndRejectsLargerOrActiveMarkup() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let html = "<h1>" + String('x', 512 * 1024 - 4)
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(html), Is.EqualTo(512 * 1024))
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "spec-max" "Big task" html
            |> success |> ignore
            let firstPage = ReviewWorkflow.getTaskSpecPage second "spec-max" "" 32768 |> success
            Assert.That(firstPage.TotalBytes, Is.EqualTo(512 * 1024))
            Assert.That(firstPage.Content, Is.EqualTo(html.Substring(0, 32768)))
            Assert.That(firstPage.HasMore, Is.True)
            Assert.That(firstPage.SpecBlobOid.Length, Is.GreaterThanOrEqualTo(40))
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "spec-too-large"
                    "Too large" (html + "x") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Over-limit spec accepted: %A" other)
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "spec-active"
                    "Active script" "<h1>x</h1><script>alert(1)</script>" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Active script accepted: %A" other))

    [<Test>]
    member _.UnicodePagedSpecIsLosslessAndPinnedAcrossRevision() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "spec-unicode"
            let original = "<h1>æøå 🦊</h1>\n<p>UTF-8 ✓ ñ 漢字</p>\n"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId "Unicode" original
            |> success |> ignore
            let initial = ReviewWorkflow.getTaskSpecPage second taskId "" 11 |> success
            let firstOid = initial.SpecBlobOid
            let firstSnapshot = initial.SnapshotCommit
            Assert.That(initial.PageBytes, Is.EqualTo(11))
            Assert.That(initial.Content, Does.StartWith("<h1>æø"))
            ReviewWorkflow.reviseTaskSpecPinned first (Guid.NewGuid()) taskId 1 firstOid
                "Unicode updated" "<h1>new version</h1>\n" |> success |> ignore
            let mutable cursor = initial.NextCursor
            let mutable collected = initial.Content
            let mutable finished = false
            let mutable segments = 1
            while not finished do
                let page = ReviewWorkflow.getTaskSpecPage second taskId cursor 11 |> success
                Assert.That(page.SpecBlobOid, Is.EqualTo(firstOid))
                Assert.That(page.SnapshotCommit, Is.EqualTo(firstSnapshot))
                Assert.That(page.PageBytes, Is.LessThanOrEqualTo(11))
                collected <- collected + page.Content
                segments <- segments + 1
                finished <- not page.HasMore
                cursor <- page.NextCursor
            Assert.That(collected, Is.EqualTo(original))
            Assert.That(segments, Is.GreaterThan(3))
            let current = ReviewWorkflow.getTaskSpecPage second taskId "" 32768 |> success
            Assert.That(current.Content, Is.EqualTo("<h1>new version</h1>\n"))
            Assert.That(current.SpecBlobOid, Is.Not.EqualTo(firstOid))
            let diff = ReviewWorkflow.diffTaskSpec second taskId firstOid current.SpecBlobOid |> success
            Assert.That(diff.Diff, Does.Contain("new version"))
            Assert.That(diff.Diff, Does.Contain("UTF-8"))
            Assert.That(diff.Truncated, Is.False)
            let staleToken : TaskSpecCursor =
                { ProjectId = first.ProjectId.ToString("D"); LedgerId = first.LedgerId.ToString("D")
                  TaskId = taskId; SnapshotCommit = firstSnapshot; SpecBlobOid = firstOid
                  Offset = initial.PageBytes
                  ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1.0).ToString("O") }
            let futureCursor =
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(staleToken)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_')
            match ReviewWorkflow.getTaskSpecPage first taskId futureCursor 11 with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Unbounded forged cursor expiry accepted: %A" other)
            match ReviewWorkflow.getTaskSpecPage first taskId "invalid-cursor" 11 with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Invalid cursor accepted: %A" other)
            match ReviewWorkflow.diffTaskSpec first taskId
                    (String('0', 40)) current.SpecBlobOid with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Non-canonical blob diff accepted: %A" other))

    [<Test>]
    member _.SourceImportAbove64KiBRoundTripsWithoutOversizedAuditRecord() =
        withFixture (fun one _ first second _ _ _ _ ->
            let taskId = "spec-large-import"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId "Before import"
                "<h1>Initial</h1>" |> success |> ignore
            let before = ReviewWorkflow.getTaskSpecPage first taskId "" 256 |> success
            let source = "<h1>Large import 🦊</h1>\\n"
                         + String.replicate (80 * 1024) "a"
                         + "\\n"
            let path = "large-draft.html"
            File.WriteAllText(Path.Combine(one,path),source,System.Text.UTF8Encoding(false))
            let original = LedgerStore.readRegisteredSourceSpec first path |> success
            ReviewWorkflow.importTaskSpec first (Guid.NewGuid()) taskId 1
                before.SpecBlobOid original.BlobOid "Large imported task" path
            |> success |> ignore
            let mutable cursor = ""
            let mutable completed = false
            let mutable assembled = ""
            let mutable segmentCount = 0
            while not completed do
                let page = ReviewWorkflow.getTaskSpecPage second taskId cursor 32768 |> success
                assembled <- assembled + page.Content
                segmentCount <- segmentCount + 1
                completed <- not page.HasMore
                cursor <- page.NextCursor
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(source), Is.GreaterThan(64 * 1024))
            Assert.That(assembled, Is.EqualTo(source))
            Assert.That(segmentCount, Is.GreaterThan(2))
            let _, _, _, direct, _ =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(direct, Is.EqualTo(source)))

    [<Test>]
    member _.ImportRejectsSourceMutationBetweenPreflightAndLedgerCAS() =
        withFixture (fun one _ first second _ _ _ _ ->
            let taskId = "spec-source-race"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId "Original"
                "<h1>Keep me</h1>\n" |> success |> ignore
            let baseline = ReviewWorkflow.getTaskSpecPage second taskId "" 512 |> success
            let path = Path.Combine(one, "race.html")
            File.WriteAllText(path, "<h2>Preflight snapshot</h2>\n")
            let pinned = LedgerStore.readRegisteredSourceSpec first "race.html" |> success
            File.WriteAllText(path, "<h2>Mutated after preflight</h2>\n")
            match ReviewWorkflow.importTaskSpecPrepared first (Guid.NewGuid()) taskId 1
                    baseline.SpecBlobOid pinned.BlobOid "Attempted stale import" "race.html" pinned with
            | Error(SourceUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Mutable source was published after preflight: %A" other)
            let _, task, _ = ReviewWorkflow.getTask second taskId |> success
            Assert.That(task.Version, Is.EqualTo(1))
            Assert.That((ReviewWorkflow.getTaskSpecPage second taskId "" 512 |> success).SpecBlobOid,
                        Is.EqualTo(baseline.SpecBlobOid))
            Assert.That((LedgerStore.listAt second (LedgerStore.fetch second |> success) "tasks"
                         |> success |> List.filter (fun path ->
                             path.StartsWith("tasks/spec-source-race/imports/"))).Length,
                        Is.EqualTo(0)))

    [<Test>]
    member _.AssignedSpecChangeNeedsExplicitNewGenerationAdoption() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "spec-adoption"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId "Initial"
                "<h1>Old assigned contract</h1>" |> success |> ignore
            let previous = ReviewWorkflow.getTaskSpecPage second taskId "" 1024 |> success
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            ReviewWorkflow.reviseTaskSpecPinned first (Guid.NewGuid()) taskId 2 previous.SpecBlobOid
                "Updated" "<h1>New canonical contract</h1>" |> success |> ignore
            let newPage = ReviewWorkflow.getTaskSpecPage second taskId "" 1024 |> success
            let _, task, option = ReviewWorkflow.getTask first taskId |> success
            let oldAttempt = option.Value
            let owner = (ReviewWorkflow.getAgentInbox first "Owner" |> success).Agent
            Assert.That(task.SpecVersion, Is.EqualTo(2))
            Assert.That(oldAttempt.SpecBlobOid, Is.EqualTo(previous.SpecBlobOid))
            Assert.That(newPage.SpecBlobOid, Is.Not.EqualTo(oldAttempt.SpecBlobOid))
            let _, _, _, firstIssue, _ = ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(firstIssue.SpecBlobOid, Is.EqualTo(previous.SpecBlobOid))
            expectStale (ReviewWorkflow.adoptTaskSpec first (Guid.NewGuid()) taskId "Owner"
                             task.Version oldAttempt.Version owner.Version 1 previous.SpecBlobOid
                             "Wrong spec version")
            ReviewWorkflow.adoptTaskSpec first (Guid.NewGuid()) taskId "Owner"
                task.Version oldAttempt.Version owner.Version 1 newPage.SpecBlobOid
                "Explicitly adopt the revised spec" |> success |> ignore
            let _, unchanged, adopted = ReviewWorkflow.getTask second taskId |> success
            Assert.That(unchanged.Version, Is.EqualTo(task.Version))
            Assert.That(adopted.Value.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(adopted.Value.SpecBlobOid, Is.EqualTo(newPage.SpecBlobOid))
            let _, _, _, previousIssue, _ = ReviewWorkflow.getAssignment second taskId 1 |> success
            let _, _, _, updatedIssue, _ = ReviewWorkflow.getAssignment second taskId 2 |> success
            Assert.That(previousIssue.SpecBlobOid, Is.EqualTo(previous.SpecBlobOid))
            Assert.That(updatedIssue.SpecBlobOid, Is.EqualTo(newPage.SpecBlobOid))
            Assert.That(updatedIssue.Reason, Is.EqualTo("SpecAdoption"))
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1)
            let _, _, ready = ReviewWorkflow.getTask first taskId |> success
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" ready.Value.Version 2
            |> success |> ignore
            let working = (ReviewWorkflow.getAgentInbox first "Owner" |> success).Agent
            let _, currentTask, workingAttempt = ReviewWorkflow.getTask first taskId |> success
            expectStale (ReviewWorkflow.adoptTaskSpec first (Guid.NewGuid()) taskId "Owner"
                             currentTask.Version workingAttempt.Value.Version working.Version 2
                             previous.SpecBlobOid "Cannot preempt Working"))

    [<Test>]
    member _.RegisteredWorktreeImportRecordsBlobCommitAndGuardsExportPaths() =
        withFixture (fun one _ first second _ _ _ _ ->
            let taskId = "spec-import"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId "Initial"
                "<h1>Unimported</h1>\n" |> success |> ignore
            let oldPage = ReviewWorkflow.getTaskSpecPage first taskId "" 512 |> success
            let relative = "draft-source.html"
            let source = "<h1>Imported 🦊</h1>\n<p>file provenance</p>\n"
            File.WriteAllText(Path.Combine(one, relative), source)
            let stable = LedgerStore.readRegisteredSourceSpec first relative |> success
            let id = Guid.NewGuid()
            let import () =
                ReviewWorkflow.importTaskSpec first id taskId 1 oldPage.SpecBlobOid stable.BlobOid
                    "Imported HTML" relative
            Assert.That((import () |> success).AlreadyApplied, Is.False)
            Assert.That((import () |> success).AlreadyApplied, Is.True)
            let page = ReviewWorkflow.getTaskSpecPage second taskId "" 32768 |> success
            Assert.That(page.Content, Is.EqualTo(source))
            Assert.That(page.SpecBlobOid, Is.EqualTo(stable.BlobOid))
            let snapshot = LedgerStore.fetch second |> success
            let paths = LedgerStore.listAt second snapshot "tasks" |> success
            let provenancePath =
                paths |> List.find (fun path -> path.StartsWith("tasks/spec-import/imports/"))
            let content, _ = LedgerStore.readAt second snapshot provenancePath |> success
            let provenance = System.Text.Json.JsonSerializer.Deserialize<TaskSpecImport>(
                                 content.Value,
                                 System.Text.Json.JsonSerializerOptions(PropertyNameCaseInsensitive = true))
            Assert.That(provenance.SourceRef, Is.EqualTo(stable.SourceRef))
            Assert.That(provenance.SourceCommitOid, Is.EqualTo(stable.CommitOid))
            Assert.That(provenance.SourceRepositoryId, Is.EqualTo(stable.RepositoryId))
            Assert.That(provenance.SourceBlobOid, Is.EqualTo(stable.BlobOid))
            let result = ReviewWorkflow.exportTaskSpec first taskId
                             "exported-spec.html" page.SpecBlobOid |> success
            Assert.That(result.Authority, Is.EqualTo("NonAuthoritativeSourceCopy"))
            let exported = File.ReadAllText(Path.Combine(one, "exported-spec.html"))
            Assert.That(exported, Does.Contain("NON-AUTHORITATIVE JARVIS V2 EXPORT"))
            Assert.That(exported, Does.EndWith(source))
            match ReviewWorkflow.exportTaskSpec first taskId "exported-spec.html" page.SpecBlobOid with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Existing export was overwritten: %A" other)
            Assert.That(File.ReadAllText(Path.Combine(one, "exported-spec.html")), Is.EqualTo(exported))
            for path in [ "../escape.html"; "/tmp/escape.html"; ".git/config.html"; "a/../../b.html" ] do
                match LedgerStore.readRegisteredSourceSpec first path with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Traversal import accepted (%s): %A" path other)
                match ReviewWorkflow.exportTaskSpec first taskId path page.SpecBlobOid with
                | Error(InvalidBinding _) -> ()
                | other -> Assert.Fail(sprintf "Traversal export accepted (%s): %A" path other)
            File.CreateSymbolicLink(Path.Combine(one, "symlink.html"),
                                    Path.Combine(one, relative)) |> ignore
            match LedgerStore.readRegisteredSourceSpec first "symlink.html" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Symlink source accepted: %A" other)
            match ReviewWorkflow.exportTaskSpec first taskId "symlink.html" page.SpecBlobOid with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Symlink export accepted: %A" other)
            let originalContent = File.ReadAllText(Path.Combine(one, relative))
            File.WriteAllText(Path.Combine(one, relative), "<h1>Changed after observation</h1>")
            match ReviewWorkflow.importTaskSpec first (Guid.NewGuid()) taskId 2 page.SpecBlobOid
                    stable.BlobOid "Outdated import" relative with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Changed source blob imported: %A" other)
            Assert.That(File.ReadAllText(Path.Combine(one, relative)), Is.Not.EqualTo(originalContent))
            Assert.That((ReviewWorkflow.getTaskSpecPage second taskId "" 512 |> success).SpecBlobOid,
                        Is.EqualTo(page.SpecBlobOid)))

    [<Test>]
    member _.AdministrativeAssignedReadyAndAcknowledgedStateMatrix() =
        for phase in [ "Queued"; "Acknowledged"; "Paused" ] do
            withFixture (fun _ _ first second _ _ _ _ ->
                let taskId = "state-" + phase.ToLowerInvariant()
                ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Lifecycle states" |> success |> ignore
                ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid())
                    "Owner" "Reviewer" |> success |> ignore
                if phase = "Acknowledged" then
                    ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid()) taskId "Owner" 1 1
                    |> success |> ignore
                elif phase = "Paused" then
                    ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1
                    |> success |> ignore
                    ReviewWorkflow.yieldAgent second (Guid.NewGuid()) taskId "Owner" 2 3 1
                        "Paused at checkpoint" "Continue after administrative hold"
                    |> success |> ignore
                let _, task, maybeAttempt = ReviewWorkflow.getTask second taskId |> success
                let attempt = maybeAttempt.Value
                let agent = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
                Assert.That(attempt.Phase, Is.EqualTo(phase))
                let operation = if phase = "Acknowledged" then "BlockAttempt" else "ParkAttempt"
                ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) taskId operation
                    task.Version attempt.Version agent.Version attempt.AssignmentGeneration
                    ("Administrative hold from " + phase) |> success |> ignore
                let _, _, parked = ReviewWorkflow.getTask second taskId |> success
                Assert.That(parked.Value.Phase,
                            Is.EqualTo(if operation = "ParkAttempt" then "Parked" else "Blocked"))
                let updated = ReviewWorkflow.getAgentInbox second "Owner" |> success
                Assert.That(updated.Agent.State, Is.EqualTo("Completed"))
                Assert.That(updated.Agent.ActiveAttemptId, Is.Null)
                Assert.That(updated.Agent.FocusAttemptId, Is.Null)
                Assert.That(updated.SlotAvailable, Is.True)
                Assert.That(updated.NeedsOrchestratorAssignment, Is.True)
                expectStale (ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) taskId operation
                                 task.Version attempt.Version agent.Version attempt.AssignmentGeneration
                                 "Stale repeated hold"))

    [<Test>]
    member _.OpenCancellationAndAbandonmentRequireExplicitReopening() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "admin-open"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Unassigned" |> success |> ignore
            let cancelId = Guid.NewGuid()
            let cancel () = ReviewWorkflow.cancelOpenTask first cancelId taskId 1 "Not needed"
            Assert.That((cancel () |> success).AlreadyApplied, Is.False)
            Assert.That((cancel () |> success).AlreadyApplied, Is.True)
            let _, cancelled, none = ReviewWorkflow.getTask second taskId |> success
            Assert.That(cancelled.Lifecycle, Is.EqualTo("Cancelled"))
            Assert.That(none.IsNone, Is.True)
            expectStale (ReviewWorkflow.cancelOpenTask second (Guid.NewGuid()) taskId 1 "Stale cancel")
            ReviewWorkflow.reopenTask first (Guid.NewGuid()) taskId cancelled.Version "Scope revived"
            |> success |> ignore
            ReviewWorkflow.assignTask second (Guid.NewGuid()) taskId (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let _, assigned, option = ReviewWorkflow.getTask first taskId |> success
            let agent = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) taskId "AbandonAttempt"
                assigned.Version option.Value.Version agent.Version 1 "Owner cannot continue"
            |> success |> ignore
            let _, reopened, abandoned = ReviewWorkflow.getTask second taskId |> success
            Assert.That(reopened.Lifecycle, Is.EqualTo("Open"))
            Assert.That(abandoned.IsNone, Is.True)
            let _, history = ReviewWorkflow.getTaskLifecycleHistory second taskId |> success
            Assert.That(history |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "CancelTask"; "ReopenTask"; "AbandonAttempt" ])))

    [<Test>]
    member _.AdministrativeParkResumeBlockIsIndependentOfAgentParking() =
        withFixture (fun _ _ first second _ _ _ initTask ->
            initTask "admin-park"
            let taskId = "admin-park"
            let read () =
                let _, task, attempt = ReviewWorkflow.getTask second taskId |> success
                let inbox = ReviewWorkflow.getAgentInbox second "Owner" |> success
                task, attempt.Value, inbox.Agent
            let task, attempt, agent = read ()
            Assert.That(task.Version, Is.EqualTo(2))
            Assert.That(attempt.Phase, Is.EqualTo("Working"))
            let parkId = Guid.NewGuid()
            let park () = ReviewWorkflow.changeAttemptAdministration first parkId taskId "ParkAttempt"
                              task.Version attempt.Version agent.Version attempt.AssignmentGeneration
                              "Hold for dependency"
            Assert.That((park () |> success).AlreadyApplied, Is.False)
            Assert.That((park () |> success).AlreadyApplied, Is.True)
            let _, parkedAttempt, idle = read ()
            Assert.That(parkedAttempt.Phase, Is.EqualTo("Parked"))
            Assert.That(idle.State, Is.EqualTo("Completed"))
            Assert.That(idle.FocusAttemptId, Is.Null)
            Assert.That(idle.ActiveAttemptId, Is.Null)
            // Agent-level parking is a separate explicit operation.
            ReviewWorkflow.parkAgent second (Guid.NewGuid()) "Owner" idle.Version
                "Agent idle" |> success |> ignore
            let _, parked, agentParked = read ()
            Assert.That(parked.Phase, Is.EqualTo("Parked"))
            Assert.That(agentParked.State, Is.EqualTo("Parked"))
            ReviewWorkflow.changeAttemptAdministration second (Guid.NewGuid()) taskId "ResumeAttempt"
                task.Version parked.Version agentParked.Version 1 "Dependency resolved"
            |> success |> ignore
            let _, queued, stillParked = read ()
            Assert.That(queued.Phase, Is.EqualTo("Queued"))
            Assert.That(queued.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(stillParked.State, Is.EqualTo("Parked"))
            // Old generations and stale versions cannot resume or begin.
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner"
                             queued.Version 1)
            expectStale (ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) taskId
                             "ResumeAttempt" task.Version parked.Version agentParked.Version 1 "Stale")
            ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) taskId "Owner"
                queued.Version stillParked.Version 2 "Restart administratively resumed attempt"
            |> success |> ignore
            let _, reassigned, assignedAgent = read ()
            Assert.That(assignedAgent.State, Is.EqualTo("Assigned"))
            ReviewWorkflow.beginAttempt second (Guid.NewGuid()) taskId "Owner"
                reassigned.Version 2 |> success |> ignore
            let _, working, workingAgent = read ()
            Assert.That(working.Phase, Is.EqualTo("Working"))
            ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) taskId "BlockAttempt"
                task.Version working.Version workingAgent.Version 2 "External blocker"
            |> success |> ignore
            let _, blocked, finishedAgent = read ()
            Assert.That(blocked.Phase, Is.EqualTo("Blocked"))
            Assert.That(finishedAgent.State, Is.EqualTo("Completed"))
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Blocked.Length,
                        Is.EqualTo(1))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory second taskId |> success
            Assert.That(history |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "ParkAttempt"; "ResumeAttempt"; "BlockAttempt" ]))
            Assert.That(history[1].NewGeneration, Is.EqualTo(2))
            Assert.That(history[0].PreviousGeneration, Is.EqualTo(1)))

    [<Test>]
    member _.CancellationSettlesClaimedReviewAndReopenNeverRestoresOldGeneration() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "admin-cancel"
            let head = newCommit "Administrative claimed review" "review-to-cancel"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "admin-cancel" "Owner"
                3 1 requestId sourceRef head "Ready for review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "admin-cancel" requestId
                "Reviewer" 1 1 |> success |> ignore
            let _, task, attemptOption = ReviewWorkflow.getTask first "admin-cancel" |> success
            let attempt = attemptOption.Value
            let agent = (ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent
            let cancelId = Guid.NewGuid()
            let cancel () = ReviewWorkflow.changeAttemptAdministration first cancelId "admin-cancel"
                                "CancelTask" task.Version attempt.Version agent.Version 1
                                "Explicitly cancelled after external approval removed"
            Assert.That((cancel () |> success).AlreadyApplied, Is.False)
            Assert.That((cancel () |> success).AlreadyApplied, Is.True)
            let _, closedTask, archivedAttempt = ReviewWorkflow.getTask second "admin-cancel" |> success
            Assert.That(closedTask.Lifecycle, Is.EqualTo("Cancelled"))
            Assert.That(archivedAttempt.Value.Phase, Is.EqualTo("Abandoned"))
            Assert.That(archivedAttempt.Value.ActiveReviewRequestId, Is.Null)
            let _, withdrawn, revision = ReviewWorkflow.getReview second "admin-cancel" requestId |> success
            Assert.That(withdrawn.Status, Is.EqualTo("Withdrawn"))
            Assert.That(withdrawn.ClaimedRevision, Is.EqualTo(1))
            Assert.That(revision.HeadCommit, Is.EqualTo(head))
            let _, events = ReviewWorkflow.getReviewClaimEvents second "admin-cancel" requestId |> success
            Assert.That(events |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "Claim"; "Withdraw" ]))
            let owner = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(owner.Agent.State, Is.EqualTo("Completed"))
            Assert.That(owner.SlotAvailable, Is.True)
            expectStale (ReviewWorkflow.decide second (Guid.NewGuid()) "admin-cancel" requestId
                             "Reviewer" 3 Accept "Stale")
            ReviewWorkflow.reopenTask first (Guid.NewGuid()) "admin-cancel"
                closedTask.Version "New task scope approved" |> success |> ignore
            let _, reopened, noAttempt = ReviewWorkflow.getTask second "admin-cancel" |> success
            Assert.That(reopened.Lifecycle, Is.EqualTo("Open"))
            Assert.That(noAttempt.IsNone, Is.True)
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "admin-cancel" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let _, reassigned, newAttempt = ReviewWorkflow.getTask second "admin-cancel" |> success
            Assert.That(newAttempt.Value.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(newAttempt.Value.AttemptId, Is.Not.EqualTo(attempt.AttemptId))
            Assert.That(reassigned.Lifecycle, Is.EqualTo("InProgress"))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory second "admin-cancel" |> success
            Assert.That(history |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "CancelTask"; "ReopenTask" ])))

    [<Test>]
    member _.OwnerTransferRevokesOldAssignmentAndPreservesFrozenReviewEvidence() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "admin-transfer"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "NewOwner" |> success |> ignore
            let head = newCommit "Transfer review" "prior-work"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "admin-transfer" "Owner"
                3 1 requestId sourceRef head "Work before handoff" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "admin-transfer" requestId "Reviewer"
                1 1 |> success |> ignore
            let _, task, oldOption = ReviewWorkflow.getTask second "admin-transfer" |> success
            let oldAttempt = oldOption.Value
            let previousOwner = (ReviewWorkflow.getAgentInbox first "Owner" |> success).Agent
            let transferredAttemptId = Guid.NewGuid()
            let transferId = Guid.NewGuid()
            let transfer () =
                ReviewWorkflow.transferAttemptOwner first transferId "admin-transfer"
                    transferredAttemptId "NewOwner" "Reviewer" task.Version oldAttempt.Version
                    previousOwner.Version oldAttempt.AssignmentGeneration "Team ownership changed"
            Assert.That((transfer () |> success).AlreadyApplied, Is.False)
            Assert.That((transfer () |> success).AlreadyApplied, Is.True)
            let _, afterTask, afterAttempt = ReviewWorkflow.getTask second "admin-transfer" |> success
            Assert.That(afterTask.AttemptId, Is.EqualTo(transferredAttemptId.ToString("N")))
            Assert.That(afterAttempt.Value.OwnerAgentId, Is.EqualTo("NewOwner"))
            Assert.That(afterAttempt.Value.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(afterAttempt.Value.Phase, Is.EqualTo("Queued"))
            Assert.That(afterAttempt.Value.ActiveReviewRequestId, Is.Null)
            Assert.That(afterAttempt.Value.SpecBlobOid, Is.EqualTo(oldAttempt.SpecBlobOid))
            Assert.That(afterAttempt.Value.SourceRef, Is.Null)
            Assert.That(afterAttempt.Value.BaseCommit, Is.Null)
            Assert.That((ReviewWorkflow.getAgentInbox first "Owner" |> success).Agent.State,
                        Is.EqualTo("Completed"))
            let newOwner = ReviewWorkflow.getAgentInbox second "NewOwner" |> success
            Assert.That(newOwner.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(newOwner.Agent.FocusAttemptId, Is.EqualTo(transferredAttemptId.ToString("N")))
            let _, withdrawn, oldRevision = ReviewWorkflow.getReview first "admin-transfer" requestId |> success
            Assert.That(withdrawn.Status, Is.EqualTo("Withdrawn"))
            Assert.That(oldRevision.HeadCommit, Is.EqualTo(head))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory first "admin-transfer" |> success
            Assert.That(history.Head.Action, Is.EqualTo("TransferOwner"))
            Assert.That(history.Head.PreviousOwnerId, Is.EqualTo("Owner"))
            Assert.That(history.Head.NewOwnerId, Is.EqualTo("NewOwner"))
            expectStale (ReviewWorkflow.transferAttemptOwner second (Guid.NewGuid()) "admin-transfer"
                             (Guid.NewGuid()) "Owner" "Reviewer" task.Version oldAttempt.Version
                             previousOwner.Version 1 "Stale transfer")
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "admin-transfer"
                             "Owner" oldAttempt.Version 1))

    [<Test>]
    member _.CancellingPriorTaskDoesNotPreemptNewerWorkingAssignmentOrEraseLateFeedback() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            let oldTask = "late-feedback"
            initTask oldTask
            let head = newCommit "Review returned late" "needs changes"
            let id = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) oldTask "Owner" 3 1
                id sourceRef head "Needs review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) oldTask id "Reviewer"
                1 1 |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) oldTask id "Reviewer"
                2 RequestChanges "Please amend acceptance notes" |> success |> ignore
            let inbox = ReviewWorkflow.getAgentInbox first "Owner" |> success
            let notice = inbox.UnacknowledgedFeedback.Head
            Assert.That(notice.Acknowledged, Is.False)
            let _, _, beforeFinish = ReviewWorkflow.getTask first oldTask |> success
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) oldTask "Owner"
                beforeFinish.Value.Version inbox.Agent.Version 1 "Completed current slice"
            |> success |> ignore
            ReviewWorkflow.createTask second (Guid.NewGuid()) "new-work" "A newer assignment"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "new-work" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt second (Guid.NewGuid()) "new-work" "Owner" 1 1
            |> success |> ignore
            let newer = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(newer.Agent.State, Is.EqualTo("Working"))
            let newFocused = newer.Agent.FocusAttemptId
            let _, oldState, oldAttempt = ReviewWorkflow.getTask first oldTask |> success
            Assert.That(oldAttempt.Value.Phase, Is.EqualTo("Queued"))
            ReviewWorkflow.changeAttemptAdministration first (Guid.NewGuid()) oldTask "CancelTask"
                oldState.Version oldAttempt.Value.Version newer.Agent.Version
                oldAttempt.Value.AssignmentGeneration "Cancelled older task without preempting newer work"
            |> success |> ignore
            let after = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(after.Agent.State, Is.EqualTo("Working"))
            Assert.That(after.Agent.FocusAttemptId, Is.EqualTo(newFocused))
            Assert.That(after.Agent.ActiveAttemptId, Is.EqualTo(newFocused))
            Assert.That(after.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            Assert.That(after.UnacknowledgedFeedback.Head.DecisionSequence, Is.EqualTo(notice.DecisionSequence))
            ReviewWorkflow.acknowledgeReviewFeedback second (Guid.NewGuid()) oldTask "Owner"
                notice.DecisionSequence |> success |> ignore
            let acknowledged = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(acknowledged.UnacknowledgedFeedback.Length, Is.EqualTo(0))
            Assert.That(acknowledged.Agent.State, Is.EqualTo("Working"))
            Assert.That(acknowledged.Agent.ActiveAttemptId, Is.EqualTo(newFocused)))

    [<Test>]
    member _.NonSourceClosureCannotBypassExistingSourceBinding() =
        withFixture (fun _ _ first second _ _ _ initTask ->
            let taskId = "source-must-integrate"
            initTask taskId
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner"
                3 3 1 "Source implementation finished" |> success |> ignore
            let _, task, maybe = ReviewWorkflow.getTask second taskId |> success
            Assert.That(maybe.Value.Phase, Is.EqualTo("Finished"))
            Assert.That(maybe.Value.BaseCommit, Is.Not.Null)
            match ReviewWorkflow.closeNonSourceTask second (Guid.NewGuid()) taskId "Reviewer"
                    task.Version maybe.Value.Version "Approved source work" "Reviewed source code" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Source task must not take non-source close shortcut: %A" other)
            let _, unchanged, _ = ReviewWorkflow.getTask first taskId |> success
            Assert.That(unchanged.Lifecycle, Is.EqualTo("InProgress")))

    [<Test>]
    member _.IndependentNonSourceClosureIsNotIntegrationAndCanReopen() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "admin-doc"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Non-source task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1
            |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 2 3 1
                "Completed the documentation deliverable" |> success |> ignore
            let _, task, finishedOption = ReviewWorkflow.getTask second taskId |> success
            let finished = finishedOption.Value
            Assert.That(finished.Phase, Is.EqualTo("Finished"))
            match ReviewWorkflow.closeNonSourceTask second (Guid.NewGuid()) taskId "Owner"
                    task.Version finished.Version "Document approved" "Validated manual" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Implementation owner self-approval must fail: %A" other)
            match ReviewWorkflow.closeNonSourceTask second (Guid.NewGuid()) taskId "Reviewer"
                    task.Version finished.Version "" "" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Empty evidence must fail: %A" other)
            ReviewWorkflow.closeNonSourceTask first (Guid.NewGuid()) taskId "Reviewer"
                task.Version finished.Version "Documentation matches acceptance criteria"
                "Reviewed published content against agreed checklist" |> success |> ignore
            let _, closed, preserved = ReviewWorkflow.getTask second taskId |> success
            Assert.That(closed.Lifecycle, Is.EqualTo("Closed"))
            Assert.That(preserved.Value.IntegrationActionId, Is.Null)
            Assert.That(preserved.Value.AcceptedReviewRequestId, Is.Null)
            Assert.That(preserved.Value.Phase, Is.EqualTo("Finished"))
            let _, history = ReviewWorkflow.getTaskLifecycleHistory second taskId |> success
            Assert.That(history.Head.Action, Is.EqualTo("CloseNonSourceTask"))
            Assert.That(history.Head.ActorId, Is.EqualTo("Reviewer"))
            Assert.That(history.Head.Evidence, Does.Contain("Reviewed published content"))
            let _, approval =
                ReviewWorkflow.getNonSourceClosureApproval second taskId history.Head.Sequence |> success
            Assert.That(approval.NoSourceIntegration, Is.True)
            Assert.That(approval.SpecBlobOid, Is.EqualTo(finished.SpecBlobOid))
            Assert.That(approval.ApproverId, Is.EqualTo("Reviewer"))
            Assert.That(approval.Evidence, Does.Contain("Reviewed published content"))
            ReviewWorkflow.reopenTask first (Guid.NewGuid()) taskId closed.Version
                "Updated requirements require a new attempt" |> success |> ignore
            let _, reopened, archived = ReviewWorkflow.getTask second taskId |> success
            Assert.That(reopened.Lifecycle, Is.EqualTo("Open"))
            Assert.That(archived.IsNone, Is.True)
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let _, taskAgain, newAttempt = ReviewWorkflow.getTask second taskId |> success
            Assert.That(taskAgain.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That(newAttempt.Value.AttemptId, Is.Not.EqualTo(finished.AttemptId)))

    [<Test>]
    member _.SourceBindingSurvivesWorktreeRemovalAndBranchRecreationWithoutFreeingOwner() =
        withFixture (fun one _ first second baseOid targetRef _ _ ->
            let taskId = "960"
            let sourceRef = "refs/heads/feature/source-960"
            let path = Path.Combine(Path.GetDirectoryName(one), "source-960")
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Source identity" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            git one [ "branch"; "feature/source-960"; baseOid ] |> ignore
            git one [ "worktree"; "add"; path; "feature/source-960" ] |> ignore
            let worktreeBinding = { first with RepositoryPath = path }
            let sourceIdentity = LedgerStore.sourceRepositoryIdentity worktreeBinding |> success
            Assert.That(sourceIdentity.RepositoryId, Is.Not.EqualTo(first.ProjectId.ToString("D")))
            Assert.That((LedgerStore.sourceRepositoryIdentity second |> success).RepositoryId,
                        Is.EqualTo(sourceIdentity.RepositoryId))
            let initial = LedgerStore.inspectSourceWorktree worktreeBinding sourceRef |> success
            Assert.That(initial.State, Is.EqualTo("CheckedOut"))
            ReviewWorkflow.bindAttemptSource worktreeBinding (Guid.NewGuid()) taskId "Owner" 2 1
                sourceRef baseOid targetRef false false |> success |> ignore
            expectStale (ReviewWorkflow.bindAttemptSource worktreeBinding (Guid.NewGuid()) taskId
                             "Owner" 2 1 sourceRef baseOid targetRef false false)
            expectStale (ReviewWorkflow.rebindAttemptSource worktreeBinding (Guid.NewGuid()) taskId
                             "Owner" 3 2 sourceRef false false)
            let _, _, attemptOption = ReviewWorkflow.getTask second taskId |> success
            let attempt = attemptOption.Value
            Assert.That(attempt.SourceRepositoryId, Is.EqualTo(sourceIdentity.RepositoryId))
            Assert.That(attempt.SourceRef, Is.EqualTo(sourceRef))
            Assert.That(attempt.SourceRemote, Is.EqualTo(first.ApprovedFetchUrl))
            Assert.That(attempt.SourcePushRemote, Is.EqualTo(first.ApprovedPushUrl))
            // Filesystem moves leave prunable Git registrations; they must not
            // be reported as a usable checkout until Git registration is repaired.
            let movedPath = Path.Combine(Path.GetDirectoryName(one), "source-960-moved")
            Directory.Move(path, movedPath)
            let _, _, displaced = ReviewWorkflow.inspectAttemptSource first taskId |> success
            Assert.That(displaced.Value.State, Is.EqualTo("AvailableNoWorktree"))
            git one [ "worktree"; "repair"; movedPath ] |> ignore
            let repaired = { first with RepositoryPath = movedPath }
            let _, _, repairedView = ReviewWorkflow.inspectAttemptSource repaired taskId |> success
            Assert.That(repairedView.Value.State, Is.EqualTo("CheckedOut"))
            git one [ "worktree"; "remove"; movedPath ] |> ignore
            let _, _, removed = ReviewWorkflow.inspectAttemptSource first taskId |> success
            Assert.That(removed.Value.State, Is.EqualTo("AvailableNoWorktree"))
            git one [ "branch"; "-D"; "feature/source-960" ] |> ignore
            let _, _, missing = ReviewWorkflow.inspectAttemptSource first taskId |> success
            Assert.That(missing.Value.State, Is.EqualTo("SourceUnavailable"))
            let inbox = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.SlotAvailable, Is.False)
            git one [ "branch"; "feature/source-960"; baseOid ] |> ignore
            git one [ "worktree"; "add"; path; "feature/source-960" ] |> ignore
            File.WriteAllText(Path.Combine(path, "source.txt"), "base then restored worktree")
            git path [ "add"; "source.txt" ] |> ignore
            git path [ "commit"; "-m"; "Review restored feature" ] |> ignore
            let head = git path [ "rev-parse"; "HEAD" ]
            let request = Guid.NewGuid()
            ReviewWorkflow.submitReview worktreeBinding (Guid.NewGuid()) taskId "Owner" 3 1
                request sourceRef head "Restored tree" |> success |> ignore
            let _, _, revision = ReviewWorkflow.getReview second taskId request |> success
            Assert.That(revision.SourceRepositoryId, Is.EqualTo(sourceIdentity.RepositoryId))
            Assert.That(revision.SourcePushRemote, Is.EqualTo(sourceIdentity.PushUrl))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) taskId request "Reviewer" 1 1 |> success |> ignore
            // Renaming a checked-out source branch changes neither the task owner
            // nor the immutable review revision's original source-ref evidence.
            git path [ "branch"; "-m"; "feature/source-960-renamed" ] |> ignore
            ReviewWorkflow.rebindAttemptSource worktreeBinding (Guid.NewGuid()) taskId "Owner" 4 1
                "refs/heads/feature/source-960-renamed" false false |> success |> ignore
            let _, _, afterRename = ReviewWorkflow.inspectAttemptSource worktreeBinding taskId |> success
            Assert.That(afterRename.Value.State, Is.EqualTo("CheckedOut"))
            let _, _, immutableRevision = ReviewWorkflow.getReview second taskId request |> success
            Assert.That(immutableRevision.SourceRef, Is.EqualTo(sourceRef))
            ReviewWorkflow.decide second (Guid.NewGuid()) taskId request "Reviewer" 2 Accept
                "Reviewed before source rename" |> success |> ignore)

    [<Test>]
    member _.DetachedSourceCheckoutRequiresExplicitApproval() =
        withFixture (fun one _ first _ baseOid targetRef newCommit _ ->
            let taskId = "961"
            let refName = "refs/heads/feature/source-961"
            let path = Path.Combine(Path.GetDirectoryName(one), "detached-961")
            git one [ "branch"; "feature/source-961"; baseOid ] |> ignore
            git one [ "worktree"; "add"; "--detach"; path; baseOid ] |> ignore
            let detached = { first with RepositoryPath = path }
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Detached source" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            match ReviewWorkflow.bindAttemptSource detached (Guid.NewGuid()) taskId "Owner" 2 1
                    refName baseOid targetRef false false with
            | Error(SourceUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Detached checkout must require consent: %A" other)
            let state = LedgerStore.inspectSourceWorktree detached refName |> success
            Assert.That(state.SelectedWorktreeDetached, Is.True)
            // A detached checkout still needs to point at the exact selected
            // source branch commit; allowing detached does not accept stale HEAD.
            let newer = newCommit "Advance source" "newer source"
            git one [ "branch"; "-f"; "feature/source-961"; newer ] |> ignore
            let stale = LedgerStore.inspectSourceWorktree detached refName |> success
            Assert.That(stale.State, Is.EqualTo("StaleDetachedCheckout"))
            Assert.That(stale.SelectedHead, Is.EqualTo(baseOid))
            match ReviewWorkflow.bindAttemptSource detached (Guid.NewGuid()) taskId "Owner" 2 1
                    refName baseOid targetRef true false with
            | Error(SourceUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Explicitly allowed detached HEAD must still reject stale commit: %A" other)
            git path [ "checkout"; "--detach"; newer ] |> ignore
            ReviewWorkflow.bindAttemptSource detached (Guid.NewGuid()) taskId "Owner" 2 1
                refName baseOid targetRef true false |> success |> ignore)

    [<Test>]
    member _.DirtySourceAndUnapprovedRemoteChangesAreRejected() =
        withFixture (fun one _ first _ baseOid targetRef _ _ ->
            let taskId = "962"
            let refName = "refs/heads/feature/source-962"
            let path = Path.Combine(Path.GetDirectoryName(one), "dirty-962")
            git one [ "branch"; "feature/source-962"; baseOid ] |> ignore
            git one [ "worktree"; "add"; path; "feature/source-962" ] |> ignore
            let selected = { first with RepositoryPath = path }
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Dirty source" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid()) "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            File.WriteAllText(Path.Combine(path, "source.txt"), "uncommitted edit")
            match ReviewWorkflow.bindAttemptSource selected (Guid.NewGuid()) taskId "Owner" 2 1
                    refName baseOid targetRef false false with
            | Error(SourceUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Dirty checkout must require consent: %A" other)
            ReviewWorkflow.bindAttemptSource selected (Guid.NewGuid()) taskId "Owner" 2 1
                refName baseOid targetRef false true |> success |> ignore
            let fork = Path.Combine(Path.GetDirectoryName(one), "unapproved.git")
            git (Path.GetDirectoryName(one)) [ "init"; "--bare"; fork ] |> ignore
            let altered = { selected with ApprovedPushUrl = fork }
            match LedgerStore.sourceRepositoryIdentity altered with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Unapproved push remote must fail closed: %A" other)
            git one [ "remote"; "set-url"; "--push"; "origin"; fork ] |> ignore
            match ReviewWorkflow.inspectAttemptSource first taskId with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Changed configured source endpoint must fail: %A" other)
            let inbox = ReviewWorkflow.getAgentInbox first "Owner"
            match inbox with
            | Error(InvalidBinding _) -> () // Remote drift blocks authority; it never frees the slot.
            | other -> Assert.Fail(sprintf "Unapproved ledger endpoint must be rejected: %A" other))
    [<Test>]
    member _.CompetingReviewersAndReleasedWithdrawalNeverReopenRevision() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "974"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer2" |> success |> ignore
            let c = newCommit "Review race" "review-race"
            let request = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "974" "Owner" 3 1
                request sourceRef c "Two reviewers" |> success |> ignore
            let claim1 = ReviewWorkflow.claimReview first (Guid.NewGuid()) "974" request "Reviewer" 1 1
            let claim2 = ReviewWorkflow.claimReview second (Guid.NewGuid()) "974" request "Reviewer2" 1 1
            let successes = [ claim1; claim2 ] |> List.filter Result.isOk
            Assert.That(successes.Length, Is.EqualTo(1))
            match claim2 with
            | Error(ReviewVersionConflict _) -> ()
            | other -> Assert.Fail(sprintf "Second reviewer should lose claim race: %A" other)
            // Removing a source review ref must block new claim/acceptance,
            // but it must not permanently trap the reviewer occupancy lease.
            let frozenRef = $"refs/heads/jarvis-review/974/{request:N}/r1"
            git one [ "push"; "origin"; $":{frozenRef}" ] |> ignore
            ReviewWorkflow.releaseReviewClaim second (Guid.NewGuid()) "974" request "Reviewer" 2
                "Reviewer stepped down" |> success |> ignore
            match ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "974" request
                    "Owner" 3 "Try reopening frozen review" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Owner withdrew frozen review: %A" other)
            ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "974" request
                "Orchestrator" 3 "Explicit cancelled review" |> success |> ignore
            let _, archived, revision = ReviewWorkflow.getReview second "974" request |> success
            Assert.That(archived.Status, Is.EqualTo("Withdrawn"))
            Assert.That(archived.ClaimedRevision, Is.EqualTo(1))
            Assert.That(revision.HeadCommit, Is.EqualTo(c))
            let _, history = ReviewWorkflow.getReviewClaimEvents second "974" request |> success
            Assert.That(history |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "Claim"; "Release"; "Withdraw" ]))
            let inbox = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.SlotAvailable, Is.False))

    [<Test>]
    member _.OrphanCleanupReservationBlocksLateSubmissionAndOnlyDeletesPinnedOid() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "970"
            let c = newCommit "Orphan candidate" "orphan-C"
            let orphanRequest = Guid.NewGuid()
            let orphanRef = $"refs/heads/jarvis-review/970/{orphanRequest:N}/r1"
            LedgerStore.publishReviewRef first orphanRef c |> success |> ignore
            let _, candidates = ReviewWorkflow.inspectOrphanReviewRefs second "970" |> success
            Assert.That(candidates.Length, Is.EqualTo(1))
            Assert.That(candidates.Head.ReviewRef, Is.EqualTo(orphanRef))
            Assert.That(candidates.Head.ObservedHead, Is.EqualTo(c))
            Assert.That(candidates.Head.Reserved, Is.False)
            let invalidHead = String.replicate 40 "f"
            match ReviewWorkflow.reserveOrphanReviewRef first (Guid.NewGuid()) "970"
                    orphanRequest 1 invalidHead "Orchestrator" "Cleanup pending failed submission" with
            | Error(SourceReviewRefUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Wrong SHA reservation must fail: %A" other)
            let reservationId = Guid.NewGuid()
            let reserve () = ReviewWorkflow.reserveOrphanReviewRef first reservationId "970"
                                orphanRequest 1 c "Orchestrator" "Abandoned review publish"
            Assert.That((reserve () |> success).AlreadyApplied, Is.False)
            Assert.That((reserve () |> success).AlreadyApplied, Is.True)
            let _, pending = ReviewWorkflow.inspectOrphanReviewRefs second "970" |> success
            Assert.That(pending.Head.Reserved, Is.True)
            match ReviewWorkflow.submitReview first (Guid.NewGuid()) "970" "Owner" 3 1
                    orphanRequest sourceRef c "Late owner submission" with
            | Error(SourceReviewRefUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Tombstoned ref must not become a review: %A" other)
            let _, result = ReviewWorkflow.deleteReservedOrphanReviewRef second "970" orphanRequest 1 c |> success
            Assert.That(result, Is.EqualTo("Deleted"))
            let _, repeat = ReviewWorkflow.deleteReservedOrphanReviewRef first "970" orphanRequest 1 c |> success
            Assert.That(repeat, Is.EqualTo("AlreadyAbsent"))
            let _, remaining = ReviewWorkflow.inspectOrphanReviewRefs second "970" |> success
            Assert.That(remaining.Length, Is.EqualTo(1))
            Assert.That(remaining.Head.RemoteState, Is.EqualTo("Absent"))
            Assert.That(remaining.Head.ObservedHead, Is.Null)
            Assert.That(remaining.Head.ExpectedHead, Is.EqualTo(c))
            Assert.That(remaining.Head.ReservationSequence, Is.GreaterThan(0L))
            Assert.That(git one [ "ls-remote"; "origin"; orphanRef ], Is.Empty)
            let inbox = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.SlotAvailable, Is.False))

    [<Test>]
    member _.OrphanCleanupRefusesClaimedAndRetargetedRevisionRefs() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "971"
            let c = newCommit "C" "claimed-C"
            let id = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "971" "Owner" 3 1
                id sourceRef c "Live review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "971" id "Reviewer" 1 1
            |> success |> ignore
            let protectedRef = $"refs/heads/jarvis-review/971/{id:N}/r1"
            match ReviewWorkflow.reserveOrphanReviewRef first (Guid.NewGuid()) "971" id 1 c
                    "Orchestrator" "Would accidentally delete claimed revision" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Claimed ref cleanup must be rejected: %A" other)
            let orphanRequest = Guid.NewGuid()
            let orphanRef = $"refs/heads/jarvis-review/971/{orphanRequest:N}/r1"
            LedgerStore.publishReviewRef first orphanRef c |> success |> ignore
            ReviewWorkflow.reserveOrphanReviewRef first (Guid.NewGuid()) "971" orphanRequest 1 c
                "Orchestrator" "Orphan test" |> success |> ignore
            let d = newCommit "D" "retargeted-D"
            git one [ "push"; "--force"; "origin"; $"{d}:{orphanRef}" ] |> ignore
            let _, retargeted = ReviewWorkflow.inspectOrphanReviewRefs second "971" |> success
            Assert.That(retargeted.Head.RemoteState, Is.EqualTo("Retargeted"))
            Assert.That(retargeted.Head.ExpectedHead, Is.EqualTo(c))
            match ReviewWorkflow.deleteReservedOrphanReviewRef first "971" orphanRequest 1 c with
            | Error(SourceReviewRefUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Changed review ref must remain intact: %A" other)
            Assert.That(git one [ "ls-remote"; "origin"; orphanRef ], Does.Contain(d))
            Assert.That(git one [ "ls-remote"; "origin"; protectedRef ], Does.Contain(c))
            let _, review, _ = ReviewWorkflow.getReview first "971" id |> success
            Assert.That(review.Status, Is.EqualTo("Claimed"))
            ReviewWorkflow.decide second (Guid.NewGuid()) "971" id "Reviewer" 2 Accept "Approved" |> success |> ignore
            match ReviewWorkflow.reserveOrphanReviewRef first (Guid.NewGuid()) "971" id 1 c
                    "Orchestrator" "Would accidentally delete accepted revision" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Accepted source ref cleanup must be rejected: %A" other))

    [<Test>]
    member _.ClaimReleaseAndReassignmentPreserveFrozenReviewAndAudit() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "603"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer2" |> success |> ignore
            let head = newCommit "Claimable patch" "claimable"
            let id = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "603" "Owner" 3 1 id sourceRef head "Frozen review"
            |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "603" id "Reviewer" 1 1
            |> success |> ignore
            let _, before, revision = ReviewWorkflow.getReview second "603" id |> success
            Assert.That(before.ClaimedRevision, Is.EqualTo(1))
            let releaseId = Guid.NewGuid()
            let release () = ReviewWorkflow.releaseReviewClaim second releaseId "603" id "Reviewer" 2 "Leaving review"
            Assert.That((release () |> success).AlreadyApplied, Is.False)
            Assert.That((release () |> success).AlreadyApplied, Is.True)
            let _, released, frozen = ReviewWorkflow.getReview first "603" id |> success
            Assert.That(released.Status, Is.EqualTo("Claimed"))
            Assert.That(released.ClaimantId, Is.Null)
            Assert.That(frozen.HeadCommit, Is.EqualTo(revision.HeadCommit))
            Assert.That((ReviewWorkflow.awaitingReviewReassignment first |> success |> snd).Length, Is.EqualTo(1))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            match ReviewWorkflow.updateReview first (Guid.NewGuid()) "603" "Owner" 1 id 3 sourceRef head "Try replacement" with
            | Error(ReviewAlreadyClaimed _) -> ()
            | other -> Assert.Fail(sprintf "Frozen review became replaceable: %A" other)
            match ReviewWorkflow.claimReview first (Guid.NewGuid()) "603" id "Reviewer2" 3 1 with
            | Error(ReviewVersionConflict _) -> ()
            | other -> Assert.Fail(sprintf "Released review became claimable: %A" other)
            match ReviewWorkflow.decide first (Guid.NewGuid()) "603" id "Reviewer" 3 Accept "Stale" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Released claimant can still decide: %A" other)
            match ReviewWorkflow.reassignReviewerWithReason first (Guid.NewGuid()) "603" id
                    "Reviewer2" "Reviewer2" 3 "Self-reclaim" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Self-directed reclaim must not bypass orchestrator: %A" other)
            ReviewWorkflow.reassignReviewerWithReason first (Guid.NewGuid()) "603" id
                "Orchestrator" "Reviewer2" 3 "Reviewer disconnected" |> success |> ignore
            let _, handed, retained = ReviewWorkflow.getReview second "603" id |> success
            Assert.That(handed.ClaimantId, Is.EqualTo("Reviewer2"))
            Assert.That(handed.ClaimedRevision, Is.EqualTo(1))
            Assert.That(retained.HeadCommit, Is.EqualTo(head))
            ReviewWorkflow.decide second (Guid.NewGuid()) "603" id "Reviewer2" 4 Accept "Verified"
            |> success |> ignore
            let _, history = ReviewWorkflow.getReviewClaimEvents first "603" id |> success
            Assert.That(history |> List.map (fun e -> e.Action),
                        Is.EqualTo(box [ "Claim"; "Release"; "Reassign"; "Decision" ]))
            Assert.That(history |> List.map (fun e -> e.Revision),
                        Is.EqualTo(box [ 1; 1; 1; 1 ]))
            Assert.That(history[1].Reason, Is.EqualTo("Leaving review"))
            Assert.That(history[1].OperationId, Is.EqualTo(releaseId.ToString("D")))
            let inbox = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.SlotAvailable, Is.False))

    [<Test>]
    member _.OwnerUpdatesUntilClaimThenContinuesWorking() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "315"
            let c = newCommit "C" "base\nC"
            let req = Guid.NewGuid()
            let submitOp = Guid.NewGuid()
            let submit binding =
                ReviewWorkflow.submitReview binding submitOp "315" "Owner" 3 1 req sourceRef c "Please review C"
            let submitted = submit first |> success
            Assert.That(submitted.AlreadyApplied, Is.False)
            use originalSubmission = System.Text.Json.JsonDocument.Parse(submitted.Operation.OriginalResultJson)
            let publishedRevision =
                originalSubmission.RootElement.GetProperty("entityResults").EnumerateArray()
                |> Seq.find (fun value ->
                    value.GetProperty("entityKind").GetString() = "ReviewRevision")
            Assert.That(publishedRevision.GetProperty("currentReviewRevision").GetInt32(), Is.EqualTo(1))
            Assert.That(publishedRevision.GetProperty("headCommit").GetString(), Is.EqualTo(c))
            Assert.That(publishedRevision.GetProperty("reviewRef").GetString(),
                        Is.EqualTo($"refs/heads/jarvis-review/315/{req:N}/r1"))
            Assert.That((submit second |> success).AlreadyApplied, Is.True)
            let _, _, attemptAtSubmission = ReviewWorkflow.getTask first "315" |> success
            Assert.That((Option.get attemptAtSubmission).Phase, Is.EqualTo("Working"))
            let d = newCommit "D" "base\nC\nD"
            let updateOp = Guid.NewGuid()
            let update binding =
                ReviewWorkflow.updateReview binding updateOp "315" "Owner" 1 req 1 sourceRef d "Review C and D"
            Assert.That((update first |> success).AlreadyApplied, Is.False)
            Assert.That((update second |> success).AlreadyApplied, Is.True)
            let _, changedReq, revision = ReviewWorkflow.getReview second "315" req |> success
            Assert.That(changedReq.CurrentRevision, Is.EqualTo(2))
            Assert.That(revision.BaseCommit, Is.EqualTo(baseOid))
            Assert.That(revision.HeadCommit, Is.EqualTo(d))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(1))
            expectStale (ReviewWorkflow.claimReview second (Guid.NewGuid()) "315" req "Reviewer" 1 1)
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "315" req "Reviewer" 2 2 |> success |> ignore
            Assert.That((update second |> success).AlreadyApplied, Is.True)
            Assert.That(git one [ "ls-remote"; "origin"; $"refs/heads/jarvis-review/315/{req:N}/r3" ], Is.Empty)
            match ReviewWorkflow.updateReview first (Guid.NewGuid()) "315" "Owner" 1 req 3 sourceRef d "new" with
            | Error(ReviewAlreadyClaimed _) -> ()
            | other -> Assert.Fail(sprintf "Claim must freeze owner updates: %A" other)
            ReviewWorkflow.decide second (Guid.NewGuid()) "315" req "Reviewer" 3 Accept "Verified" |> success |> ignore
            let reviewSnapshot = LedgerStore.fetch first |> success
            let reviewPaths = LedgerStore.listAt first reviewSnapshot "tasks" |> success
            let immutableReviewPaths = reviewPaths |> List.filter (fun path ->
                path.Contains("/revisions/") || path.Contains("/events/")
                || path.Contains("/decisions/"))
            Assert.That(immutableReviewPaths.Length, Is.GreaterThanOrEqualTo(4))
            for path in immutableReviewPaths do
                let content, _ = LedgerStore.readAt first reviewSnapshot path |> success
                use doc = System.Text.Json.JsonDocument.Parse(content.Value)
                let occurred = doc.RootElement.GetProperty("occurredAtUtc").GetString()
                Assert.That(DateTimeOffset.Parse occurred, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
            let _, task, liveAttempt = ReviewWorkflow.getTask first "315" |> success
            let attempt = Option.get liveAttempt
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That(attempt.Phase, Is.EqualTo("Working"))
            Assert.That(attempt.ActiveReviewRequestId, Is.Null)
            Assert.That(attempt.AcceptedHead, Is.EqualTo(d))
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "315" "Owner" attempt.FeedbackSequence
            |> success |> ignore
            let _, _, acknowledged = ReviewWorkflow.getTask first "315" |> success
            Assert.That((Option.get acknowledged).AcknowledgedFeedbackSequence, Is.EqualTo(attempt.FeedbackSequence))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "315" "Owner" 6 3 1
                "Implementation finished" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "315" "Owner" 7 req
            |> success |> ignore
            let _, completedTask, completedAttempt = ReviewWorkflow.getTask first "315" |> success
            Assert.That(completedTask.Lifecycle, Is.EqualTo("Implemented"))
            Assert.That((Option.get completedAttempt).Phase, Is.EqualTo("Accepted"))
            let expectedReviewRef = $"refs/heads/jarvis-review/315/{req:N}/r2"
            Assert.That(git one [ "ls-remote"; "origin"; expectedReviewRef ], Does.Contain(d))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0)))

    [<Test>]
    member _.RejectedReviewDoesNotChangeCumulativeBase() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "500"
            let c = newCommit "unapproved C" "base\nC"
            let firstId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "500" "Owner" 3 1 firstId sourceRef c "Original"
            |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "500" firstId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "500" firstId "Reviewer" 2 RequestChanges "Fix C"
            |> success |> ignore
            let d = newCommit "fixed D" "base\nC\nD"
            let nextId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "500" "Owner" 5 1 nextId sourceRef d "C and D"
            |> success |> ignore
            let _, _, revision = ReviewWorkflow.getReview second "500" nextId |> success
            Assert.That(revision.BaseCommit, Is.EqualTo(baseOid))
            Assert.That(revision.HeadCommit, Is.EqualTo(d))
            Assert.That(revision.BaseCommit, Is.Not.EqualTo(c))
            let _, _, attemptOption = ReviewWorkflow.getTask first "500" |> success
            Assert.That((Option.get attemptOption).Phase, Is.EqualTo("Working"))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "500" nextId "Reviewer" 1 1 |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "500" nextId "Reviewer" 2 Accept "Now includes all work"
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0)))

    [<Test>]
    member _.SourceRefIsCreateOnlyAndNoDuplicateActiveRequest() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "700"
            let c = newCommit "C" "C"
            let request = Guid.NewGuid()
            let firstOp = Guid.NewGuid()
            let submit id operation =
                ReviewWorkflow.submitReview first operation "700" "Owner" 3 1 id sourceRef c "Review"
            submit request firstOp |> success |> ignore
            let originalRef = $"refs/heads/jarvis-review/700/{request:N}/r1"
            let d = newCommit "D" "D"
            match LedgerStore.publishReviewRef first originalRef d with
            | Error(SourceReviewRefUnavailable _) -> ()
            | other -> Assert.Fail(sprintf "Ref must be immutable: %A" other)
            let count = git one [ "ls-remote"; "origin"; originalRef ]
            Assert.That(count, Does.StartWith(c))
            match submit (Guid.NewGuid()) (Guid.NewGuid()) with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Second active review must be rejected: %A" other)
            let _, _, current = ReviewWorkflow.getTask first "700" |> success
            Assert.That((Option.get current).ActiveReviewRequestId, Is.EqualTo(request.ToString("N")))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(1)))

    [<Test>]
    member _.SingleOwnerCanWorkOnlyOneTaskWhileReviewIsPending() =
        withFixture (fun one _ first _ _ sourceRef newCommit initTask ->
            initTask "100"
            ReviewWorkflow.createTask first (Guid.NewGuid()) "101" "101" |> success |> ignore
            match ReviewWorkflow.assignTask first (Guid.NewGuid()) "101" (Guid.NewGuid()) "Owner" "Reviewer" with
            | Error(AgentBusy _) -> ()
            | other -> Assert.Fail(sprintf "Working agent must not be assigned a second task: %A" other)
            let c = newCommit "C" "C"
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "100" "Owner" 3 1
                (Guid.NewGuid()) sourceRef c "Snapshot" |> success |> ignore
            let current = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(current.Agent.State, Is.EqualTo("Working"))
            Assert.That(current.SlotAvailable, Is.False))

    [<Test>]
    member _.WithdrawalAndClaimedHandoffPreserveWorkingOwner() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "800"
            let c = newCommit "First patch" "C"
            let original = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "800" "Owner" 3 1
                original sourceRef c "Original patch" |> success |> ignore
            ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "800" original "Owner" 1 "Superseded"
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            let d = newCommit "Second patch" "D"
            let replacement = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "800" "Owner" 5 1
                replacement sourceRef d "Replacement patch" |> success |> ignore
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer 2" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "800" replacement "Reviewer" 1 1
            |> success |> ignore
            match ReviewWorkflow.withdrawReview first (Guid.NewGuid()) "800" replacement "Owner" 2 "No" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Owner must not withdraw claimed revision: %A" other)
            ReviewWorkflow.reassignReviewer second (Guid.NewGuid()) "800" replacement "Reviewer" "Reviewer 2" 2
            |> success |> ignore
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer" |> success |> snd).Length, Is.EqualTo(0))
            Assert.That((ReviewWorkflow.reviewerQueue second "Reviewer 2" |> success |> snd).Length, Is.EqualTo(1))
            match ReviewWorkflow.decide second (Guid.NewGuid()) "800" replacement "Reviewer" 3 Accept "Old" with
            | Error(ReviewAccessDenied _) -> ()
            | other -> Assert.Fail(sprintf "Old reviewer should not decide after handoff: %A" other)
            ReviewWorkflow.decide second (Guid.NewGuid()) "800" replacement "Reviewer 2" 3 Accept "Accepted"
            |> success |> ignore
            let _, task, attemptOption = ReviewWorkflow.getTask first "800" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("InProgress"))
            Assert.That((Option.get attemptOption).Phase, Is.EqualTo("Working"))
            Assert.That((Option.get attemptOption).AcceptedHead, Is.EqualTo(d)))

    [<Test>]
    member _.AnyRegisteredReviewerCanClaimWithoutPreassignment() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "900"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer 2" |> success |> ignore
            let c = newCommit "Source" "C"
            let req = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "900" "Owner" 3 1
                req sourceRef c "Needs review" |> success |> ignore
            let _, available = ReviewWorkflow.reviewerQueue second "Reviewer 2" |> success
            Assert.That(available.Length, Is.EqualTo(1))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "900" req "Reviewer 2" 1 1
            |> success |> ignore
            expectStale (ReviewWorkflow.claimReview first (Guid.NewGuid()) "900" req "Reviewer" 1 1)
            ReviewWorkflow.decide second (Guid.NewGuid()) "900" req "Reviewer 2" 2 Accept "Verified"
            |> success |> ignore)

    [<Test>]
    member _.OlderAcceptanceCannotFinalizeAfterNewerRejectedReview() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "901"
            let c = newCommit "First" "C"
            let accepted = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "901" "Owner" 3 1
                accepted sourceRef c "Complete?" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "901" accepted "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "901" accepted "Reviewer" 2 Accept "Fine"
            |> success |> ignore
            let d = newCommit "More changes" "D"
            let later = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "901" "Owner" 5 1
                later sourceRef d "Extra work" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "901" later "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "901" later "Reviewer" 2 RequestChanges "Not ready"
            |> success |> ignore
            match ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "901" "Owner" 7 accepted with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old approval must not finalize newer rejected work: %A" other))

    [<Test>]
    member _.ClaimAndUpdateRaceHasOneAuthoritativeRevision() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "999"
            let c = newCommit "First" "C"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "999" "Owner" 3 1
                requestId sourceRef c "Original" |> success |> ignore
            let d = newCommit "Second" "D"
            let updateOp = Guid.NewGuid()
            let claimOp = Guid.NewGuid()
            let update =
                System.Threading.Tasks.Task.Run(fun () ->
                    ReviewWorkflow.updateReview first updateOp "999" "Owner" 1
                        requestId 1 sourceRef d "Replacement")
            let claim =
                System.Threading.Tasks.Task.Run(fun () ->
                    ReviewWorkflow.claimReview second claimOp "999" requestId "Reviewer" 1 1)
            System.Threading.Tasks.Task.WaitAll(
                [| update :> System.Threading.Tasks.Task; claim :> System.Threading.Tasks.Task |])
            let updated = update.Result
            let claimed = claim.Result
            let winners =
                [ updated; claimed ]
                |> List.filter (function | Ok _ -> true | _ -> false)
                |> List.length
            Assert.That(winners, Is.EqualTo(1))
            let _, request, revision = ReviewWorkflow.getReview first "999" requestId |> success
            if request.Status = "Claimed" then
                Assert.That(request.CurrentRevision, Is.EqualTo(1))
                Assert.That(revision.HeadCommit, Is.EqualTo(c))
                match updated with
                | Error(ReviewAlreadyClaimed _) -> ()
                | other -> Assert.Fail(sprintf "Claim should lock the update: %A" other)
            else
                Assert.That(request.Status, Is.EqualTo("Unclaimed"))
                Assert.That(request.CurrentRevision, Is.EqualTo(2))
                Assert.That(revision.HeadCommit, Is.EqualTo(d))
                expectStale claimed)


    [<Test>]
    member _.AgentInboxSeparatesSlotQueueAndReviewResponsibilities() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit _ ->
            ReviewWorkflow.createTask first (Guid.NewGuid()) "102" "Queued task" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "102" (Guid.NewGuid()) "Owner" "Reviewer"
            |> success |> ignore
            let fresh = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(fresh.SlotAvailable, Is.True)
            Assert.That(fresh.Working.IsNone, Is.True)
            Assert.That(fresh.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(fresh.RunnableQueue.Head.Task.TaskId, Is.EqualTo("102"))
            Assert.That(fresh.Unassigned, Is.False)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "102" "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) "102" "Owner" 2 1 baseOid sourceRef
            |> success |> ignore
            let working = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(working.SlotAvailable, Is.False)
            Assert.That(working.Working.Value.Task.TaskId, Is.EqualTo("102"))
            Assert.That(working.RunnableQueue.Length, Is.EqualTo(0))
            let c = newCommit "Needs review" "C"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "102" "Owner" 3 1
                requestId sourceRef c "Review C" |> success |> ignore
            let submitting = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(submitting.PendingReviews.Length, Is.EqualTo(1))
            Assert.That(submitting.Working.IsSome, Is.True)
            let reviewer = ReviewWorkflow.getAgentInbox second "Reviewer" |> success
            Assert.That(reviewer.AvailableReviews.Length, Is.EqualTo(1))
            Assert.That(reviewer.PendingReviews.Length, Is.EqualTo(0))
            Assert.That(reviewer.SlotAvailable, Is.True)
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "102" requestId "Reviewer" 1 1
            |> success |> ignore
            let claimed = ReviewWorkflow.getAgentInbox first "Reviewer" |> success
            Assert.That(claimed.AvailableReviews.Length, Is.EqualTo(0))
            Assert.That(claimed.PendingReviews.Length, Is.EqualTo(1))
            ReviewWorkflow.decide second (Guid.NewGuid()) "102" requestId "Reviewer" 2 Accept "Passed"
            |> success |> ignore
            let decided = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(decided.Working.IsSome, Is.True)
            Assert.That(decided.PendingReviews.Length, Is.EqualTo(0))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "102" "Owner" 5 3 1
                "Ready for integration" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "102" "Owner" 6 requestId
            |> success |> ignore
            let finalized = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finalized.SlotAvailable, Is.True)
            Assert.That(finalized.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finalized.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(finalized.RunnableQueue.Length, Is.EqualTo(0))
            Assert.That(finalized.Unassigned, Is.False))

    [<Test>]
    member _.AgentInboxRejectsUnknownAgent() =
        withFixture (fun _ _ first _ _ _ _ _ ->
            match ReviewWorkflow.getAgentInbox first "Not registered" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Unknown agent should not appear in inbox: %A" other))


    [<Test>]
    member _.CanonicalSpecIsAtomicVersionedAndPinnedByAssignment() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "600"
            let initialHtml = "<h1>First version</h1><p>Original requirements</p>"
            let revisedHtml = "<h1>Second version</h1><p>Acceptance criteria</p>"
            let initialOp = Guid.NewGuid()
            let create () =
                ReviewWorkflow.createTaskWithSpec first initialOp taskId "First version" initialHtml
            Assert.That((create () |> success).AlreadyApplied, Is.False)
            Assert.That((create () |> success).AlreadyApplied, Is.True)
            let _, taskOne, _, specOne, oidOne =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(taskOne.SpecVersion, Is.EqualTo(1))
            Assert.That(specOne, Is.EqualTo(initialHtml))
            let editOp = Guid.NewGuid()
            ReviewWorkflow.reviseTaskSpec first editOp taskId 1 "Second version" revisedHtml
            |> success |> ignore
            let _, taskTwo, _, specTwo, oidTwo =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(taskTwo.Version, Is.EqualTo(2))
            Assert.That(taskTwo.SpecVersion, Is.EqualTo(2))
            Assert.That(specTwo, Is.EqualTo(revisedHtml))
            Assert.That(oidTwo, Is.Not.EqualTo(oidOne))
            expectStale (
                ReviewWorkflow.reviseTaskSpec second (Guid.NewGuid()) taskId 1
                    "Stale" "<h1>Stale</h1>")
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let _, _, assigned = ReviewWorkflow.getTask second taskId |> success
            Assert.That(assigned.Value.SpecBlobOid, Is.EqualTo(oidTwo))
            expectStale (
                ReviewWorkflow.reviseTaskSpec second (Guid.NewGuid()) taskId 3
                    "Too late" "<h1>Too late</h1>"))


    [<Test>]
    member _.OptedInTaskApiCanCreateAssignBeginBindAndSubmit() =
        withFixture (fun one _ first _ baseOid sourceRef newCommit _ ->
            let configPath = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            let configDirectory = Path.GetDirectoryName configPath
            Directory.CreateDirectory configDirectory |> ignore
            let approved : Client.V2ReviewApi.ReviewV2OptIn =
                { SchemaVersion = 1
                  Enabled = true
                  ProjectRoot = one
                  ProjectId = first.ProjectId.ToString("D")
                  LedgerId = first.LedgerId.ToString("D")
                  Epoch = first.Epoch
                  RemoteName = first.RemoteName
                  ApprovedFetchUrl = first.ApprovedFetchUrl
                  ApprovedPushUrl = first.ApprovedPushUrl
                  HighestTrustedHead = (LedgerStore.fetch first |> success).Commit }
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(approved))
            try
                let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
                let template (action: string) (taskId: string) : Common.V2TaskWriteCommand =
                    { ProjectName = "owner"
                      Action = action
                      OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId
                      AttemptId = ""
                      ActorId = "Owner"
                      OwnerId = ""
                      ReviewerId = ""
                      Title = ""
                      Html = ""
                      ExpectedVersion = 0
                      ExpectedAgentVersion = 0; ExpectedTaskVersion = 0
                      AssignmentGeneration = 0
                      Summary = ""
                      NextAction = ""
                      Reason = ""
                      BaseOid = ""
                      TargetRef = ""
                      SourceRef = ""; AllowDetached = false; AllowUncommitted = false
                      DecisionSequence = 0L; Evidence = ""; SessionId = "" }
                let run cmd =
                    Client.V2ReviewApi.V2TaskApi.write cmd ctx
                    |> function
                        | Ok value -> value
                        | Error error -> failwithf "V2 task API failed: %A" error
                let taskId = "751"
                let html = "<h1>Full task specification</h1><p>Acceptance: one review</p>"
                let createCommand =
                    { template "CreateTask" taskId with Title = "Full task"; Html = html }
                let created = run { createCommand with SessionId = "session-A" }
                // A new execution must be able to replay the same immutable task
                // mutation without session routing metadata changing its identity.
                let replayed = run { createCommand with SessionId = "session-B" }
                let replayJson = System.Text.Json.JsonSerializer.Serialize(replayed)
                use replayDoc = System.Text.Json.JsonDocument.Parse(replayJson)
                Assert.That(replayDoc.RootElement.GetProperty("alreadyApplied").GetBoolean(), Is.True)
                let creationJson = System.Text.Json.JsonSerializer.Serialize(created)
                use creation = System.Text.Json.JsonDocument.Parse(creationJson)
                let creationHead = creation.RootElement.GetProperty("head").GetString()
                let createdEffect =
                    creation.RootElement.GetProperty("originalResult").GetProperty("entityResults")
                        .EnumerateArray()
                    |> Seq.find (fun item -> item.GetProperty("entityKind").GetString() = "Task")
                Assert.That(createdEffect.GetProperty("entityVersion").GetInt32(), Is.EqualTo(1))
                Assert.That(createdEffect.GetProperty("lifecycle").GetString(), Is.EqualTo("Open"))
                let attemptId = Guid.NewGuid()
                run { template "AssignTask" taskId with
                          AttemptId = attemptId.ToString("D")
                          OwnerId = "Owner"; ReviewerId = "Reviewer" } |> ignore
                let queued =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 inbox failed: %A" error
                Assert.That(queued.RunnableQueue.Length, Is.EqualTo(1))
                Assert.That(queued.RunnableQueue.Head.Attempt.SpecBlobOid.Length, Is.EqualTo(40))
                let generationLookup : Common.V2TaskReadCommand =
                    { ProjectName = "owner"; AgentId = ""; Action = "GetAssignment"
                      TaskId = taskId; AssignmentGeneration = 1; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null }
                let snapshotAssignment =
                    Client.V2ReviewApi.V2TaskApi.read generationLookup ctx
                    |> function
                        | Ok value -> System.Text.Json.JsonSerializer.Serialize(value)
                        | Error error -> failwithf "V2 immutable assignment read failed: %A" error
                Assert.That(snapshotAssignment, Does.Contain("Implement task"))
                Assert.That(snapshotAssignment, Does.Contain("Owner"))

                let operationStatus : Common.V2LedgerReadCommand =
                    { ProjectName = "owner"; Action = "GetOperationStatus"
                      OperationId = createCommand.OperationId }
                let operationResponse =
                    Client.V2ReviewApi.V2LedgerApi.read operationStatus ctx
                    |> function
                        | Ok value -> value
                        | Error error -> failwithf "V2 operation status failed: %A" error
                let operationJson = System.Text.Json.JsonSerializer.Serialize(operationResponse)
                use operationDoc = System.Text.Json.JsonDocument.Parse(operationJson)
                Assert.That(operationDoc.RootElement.GetProperty("originatingCommit").GetString(),
                            Is.EqualTo(creationHead))
                Assert.That(operationDoc.RootElement.GetProperty("snapshotCommit").GetString(),
                            Is.Not.EqualTo(creationHead))
                let storedEffect =
                    operationDoc.RootElement.GetProperty("originalResult").GetProperty("entityResults")
                        .EnumerateArray()
                    |> Seq.find (fun item -> item.GetProperty("entityKind").GetString() = "Task")
                Assert.That(storedEffect.GetProperty("lifecycle").GetString(), Is.EqualTo("Open"))
                run { template "BeginAttempt" taskId with
                          ExpectedVersion = 1; AssignmentGeneration = 1 } |> ignore
                run { template "BindReviewBase" taskId with
                          ExpectedVersion = 2; AssignmentGeneration = 1
                          BaseOid = baseOid; TargetRef = sourceRef } |> ignore
                let getTask : Common.V2ReviewReadCommand =
                    { ProjectName = "owner"; Action = "GetTask"; TaskId = taskId
                      RequestId = ""; ReviewerId = "" }
                match Client.V2ReviewApi.V2ReviewApi.read getTask ctx with
                | Error error -> Assert.Fail(sprintf "V2 task read failed: %A" error)
                | Ok value ->
                    let resultJson = System.Text.Json.JsonSerializer.Serialize(value)
                    use parsed = System.Text.Json.JsonDocument.Parse(resultJson)
                    Assert.That(parsed.RootElement.GetProperty("specHtml").GetString(), Is.EqualTo(html))
                    Assert.That(parsed.RootElement.GetProperty("specBlobOid").GetString(),
                                Is.EqualTo(queued.RunnableQueue.Head.Attempt.SpecBlobOid))
                run { template "YieldAgent" taskId with
                          ExpectedVersion = 3; ExpectedAgentVersion = 3
                          AssignmentGeneration = 1; Summary = "Checkpoint"
                          NextAction = "Resume work" } |> ignore
                let ready =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 Ready inbox failed: %A" error
                Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
                Assert.That(ready.OperatorActionRequired, Is.True)
                run { template "ResumeAgent" taskId with
                          ExpectedVersion = 4; ExpectedAgentVersion = 4
                          AssignmentGeneration = 1 } |> ignore
                let head = newCommit "Task 751 implementation" "code for 751"
                let requestId = Guid.NewGuid()
                let reviewCmd : Common.V2ReviewWriteCommand =
                    { ProjectName = "owner"; Action = "Submit"
                      OperationId = Guid.NewGuid().ToString("D")
                      TaskId = taskId; RequestId = requestId.ToString("D")
                      ActorId = "Owner"; ReviewerId = ""
                      ExpectedVersion = 5; Generation = 1
                      SourceRef = sourceRef; HeadOid = head; Summary = "Implementation ready"
                      Verdict = ""; Feedback = ""; TestEvidence = ""; Inspected = false; Tested = false; TestDetails = "" }
                match Client.V2ReviewApi.V2ReviewApi.write reviewCmd ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 submit API failed: %A" error)
                // Exercise the client protocol rather than only invoking the
                // workflow directly. Read/write must honor the V2 opt-in and
                // use caller-stable operation identities for recovery.
                ReviewWorkflow.claimReview first (Guid.NewGuid()) taskId requestId
                    "Reviewer" 1 1 |> success |> ignore
                let released =
                    { reviewCmd with Action = "ReleaseClaim"; ActorId = "Reviewer"
                                     ExpectedVersion = 2; Generation = 0
                                     Feedback = "Reviewer unavailable"
                                     OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write released ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 release API failed: %A" error)
                let queueRead : Common.V2ReviewReadCommand =
                    { ProjectName = "owner"; Action = "AwaitingReassignment"
                      TaskId = ""; RequestId = ""; ReviewerId = "" }
                match Client.V2ReviewApi.V2ReviewApi.read queueRead ctx with
                | Ok value ->
                    let _, requests = unbox<string * ReviewRequest list> value
                    Assert.That(requests.Length, Is.EqualTo(1))
                    Assert.That(requests.Head.ClaimantId, Is.Null)
                | Error error -> Assert.Fail(sprintf "V2 awaiting-reassignment API failed: %A" error)
                ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Reviewer 2" |> success |> ignore
                let handoff =
                    { released with Action = "Reassign"; ActorId = "Orchestrator"
                                    ReviewerId = "Reviewer 2"; ExpectedVersion = 3
                                    Feedback = "Explicit recovery"
                                    OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write handoff ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 reviewer reassign API failed: %A" error)
                let historyRead = { queueRead with Action = "ClaimHistory"; TaskId = taskId
                                                   RequestId = requestId.ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.read historyRead ctx with
                | Ok value ->
                    let _, events = unbox<string * ReviewClaimEvent list> value
                    Assert.That(events.Length, Is.EqualTo(3))
                    Assert.That(events[2].Reason, Is.EqualTo("Explicit recovery"))
                | Error error -> Assert.Fail(sprintf "V2 immutable event API failed: %A" error)
                // The source-ref cleanup path also goes through the opted-in
                // V2 client API, including a persistent tombstone and confirmed
                // guarded removal of the remote ref.
                let orphanId = Guid.NewGuid()
                let orphanRef = $"refs/heads/jarvis-review/{taskId}/{orphanId:N}/r1"
                LedgerStore.publishReviewRef first orphanRef head |> success |> ignore
                let orphanRead =
                    { queueRead with Action = "OrphanRefs"; TaskId = taskId }
                match Client.V2ReviewApi.V2ReviewApi.read orphanRead ctx with
                | Ok value ->
                    let _, candidates = unbox<string * OrphanReviewRef list> value
                    Assert.That(candidates |> List.exists (fun entry -> entry.ReviewRef = orphanRef), Is.True)
                | Error error -> Assert.Fail(sprintf "V2 orphan inspection API failed: %A" error)
                let reserve =
                    { released with Action = "ReserveOrphan"; ActorId = "Orchestrator"
                                    RequestId = orphanId.ToString("D"); Generation = 1
                                    HeadOid = head; Feedback = "Abandoned publication"
                                    OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write reserve ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 orphan reserve API failed: %A" error)
                match Client.V2ReviewApi.V2ReviewApi.deleteReservedOrphan
                        { reserve with Action = "DeleteReservedOrphan" } ctx with
                | Ok value ->
                    let json = System.Text.Json.JsonSerializer.Serialize(value)
                    Assert.That(json, Does.Contain("Deleted"))
                | Error error -> Assert.Fail(sprintf "V2 guarded orphan delete API failed: %A" error)
                match Client.V2ReviewApi.V2ReviewApi.read orphanRead ctx with
                | Ok value ->
                    let _, candidates = unbox<string * OrphanReviewRef list> value
                    let removed = candidates |> List.find (fun candidate -> candidate.ReviewRef = orphanRef)
                    Assert.That(removed.RemoteState, Is.EqualTo("Absent"))
                | Error error -> Assert.Fail(sprintf "V2 orphan tombstone inspection API failed: %A" error)
                let after =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 inbox failed: %A" error
                Assert.That(after.SlotAvailable, Is.False)
                Assert.That(after.Agent.State, Is.EqualTo("Working"))
                run { template "CompleteAgentWork" taskId with
                          ExpectedVersion = 6; ExpectedAgentVersion = 5
                          AssignmentGeneration = 1; Summary = "Finished the implementation" } |> ignore
                let finished = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(finished.Agent.State, Is.EqualTo("Completed"))
                run { template "ParkAgent" taskId with
                          ExpectedAgentVersion = 6; Reason = "No other work" } |> ignore
                let parked = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
                Assert.That(parked.Focused.IsNone, Is.True)
                Assert.That(parked.PendingReviews.Length, Is.EqualTo(1))
                Assert.That(after.PendingReviews.Length, Is.EqualTo(1))
                // Additional typed V2 administrative API flow is independent
                // of source publication and the agent's previous work.
                let adminTask = "admin-api"
                run { template "CreateTask" adminTask with Title = "Administrative API task";
                                                             Html = "<h1>Administrative API task</h1>" } |> ignore
                run { template "CancelOpenTask" adminTask with
                          ExpectedTaskVersion = 1; Reason = "Explicit cancellation" } |> ignore
                let adminHistory : Common.V2TaskReadCommand =
                    { ProjectName = "owner"; Action = "LifecycleHistory"; TaskId = adminTask
                      AgentId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null }
                match Client.V2ReviewApi.V2TaskApi.read adminHistory ctx with
                | Ok value ->
                    let _, events = unbox<string * TaskLifecycleEvent list> value
                    Assert.That(events.Length, Is.EqualTo(1))
                    Assert.That(events.Head.Action, Is.EqualTo("CancelTask"))
                | Error error -> Assert.Fail(sprintf "Typed V2 lifecycle history failed: %A" error)
                run { template "ReopenTask" adminTask with
                          ExpectedTaskVersion = 2; Reason = "Explicit reopening" } |> ignore
                let _, reopened, _ = ReviewWorkflow.getTask first adminTask |> success
                Assert.That(reopened.Lifecycle, Is.EqualTo("Open"))
                // V2-09 spec commands through the opted-in client protocol:
                // read pages/diff on read route, export on write route.
                let specTask = "spec-api"
                run { template "CreateTask" specTask with
                          Title = "Spec API"; Html = "<h1>API revision one</h1>\\n" } |> ignore
                let pageCmd : Common.V2TaskReadCommand =
                    { ProjectName = "owner"; Action = "GetSpecPage"; TaskId = specTask
                      AgentId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L
                      Cursor = ""; PageSize = 128; BeforeOid = null; AfterOid = null; RelativePath = null }
                let pageBefore =
                    Client.V2ReviewApi.V2TaskApi.read pageCmd ctx
                    |> function
                       | Ok value -> unbox<TaskSpecPage> value
                       | Error error -> failwithf "V2 spec page API failed: %A" error
                Assert.That(pageBefore.TotalBytes, Is.GreaterThan(10))
                let revisedHtml = "<h1>API revised 🦊</h1>\\n"
                let revisionCmd =
                    { template "ReviseTaskSpecPinned" specTask with
                        ExpectedVersion = 1; BaseOid = pageBefore.SpecBlobOid
                        Title = "Spec API updated"; Html = revisedHtml }
                let _ = run revisionCmd
                let retried = run revisionCmd
                let retriedJson = System.Text.Json.JsonSerializer.Serialize(retried)
                Assert.That(retriedJson, Does.Contain("alreadyApplied"))
                let pageAfter =
                    Client.V2ReviewApi.V2TaskApi.read pageCmd ctx
                    |> function
                       | Ok value -> unbox<TaskSpecPage> value
                       | Error error -> failwithf "V2 revised page API failed: %A" error
                Assert.That(pageAfter.Content, Is.EqualTo(revisedHtml))
                let diffCmd = { pageCmd with Action = "DiffTaskSpec"; BeforeOid = pageBefore.SpecBlobOid
                                             AfterOid = pageAfter.SpecBlobOid }
                match Client.V2ReviewApi.V2TaskApi.read diffCmd ctx with
                | Ok value -> Assert.That((unbox<TaskSpecDiff> value).Diff, Does.Contain("API revised"))
                | Error error -> Assert.Fail(sprintf "V2 diff API failed: %A" error)
                let exportCmd = { template "ExportTaskSpec" specTask with
                                    SourceRef = "spec-api-export.html"
                                    BaseOid = pageAfter.SpecBlobOid }
                let exportReceipt = run exportCmd
                Assert.That((unbox<TaskSpecExport> exportReceipt).AlreadyApplied, Is.False)
                Assert.That((unbox<TaskSpecExport> exportReceipt).Authority,
                            Is.EqualTo("NonAuthoritativeSourceCopy"))
                let exportRetry = run exportCmd
                Assert.That((unbox<TaskSpecExport> exportRetry).AlreadyApplied, Is.True)
                let otherOperation = { exportCmd with OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2TaskApi.write otherOperation ctx with
                | Error _ -> ()
                | Ok _ -> Assert.Fail("Unrelated operation overwrote existing exported HTML")
                Assert.That(File.ReadAllText(Path.Combine(one, "spec-api-export.html")),
                            Does.Contain("NON-AUTHORITATIVE JARVIS"))
                match Client.V2ReviewApi.V2TaskApi.read
                        { pageCmd with Action = "ExportTaskSpec"; RelativePath = "should-not-write.html" } ctx with
                | Error _ -> ()
                | Ok _ -> Assert.Fail("Export incorrectly accessible through read-only V2 API")
                Assert.That(File.Exists(Path.Combine(one, "should-not-write.html")), Is.False)
                File.WriteAllText(Path.Combine(one, "spec-api-import.html"),
                                  "<h1>API imported safely</h1>\\n")
                let sourceDraft = LedgerStore.readRegisteredSourceSpec first "spec-api-import.html" |> success
                let importCommand =
                    { template "ImportTaskSpec" specTask with
                        ExpectedVersion = 2; BaseOid = pageAfter.SpecBlobOid
                        TargetRef = sourceDraft.BlobOid; SourceRef = "spec-api-import.html"
                        Title = "Spec API imported" }
                run importCommand |> ignore
                let latest =
                    Client.V2ReviewApi.V2TaskApi.read pageCmd ctx
                    |> function
                       | Ok value -> unbox<TaskSpecPage> value
                       | Error error -> failwithf "V2 imported page API failed: %A" error
                Assert.That(latest.Content, Does.Contain("API imported safely"))
                Assert.That(after.PendingReviews.Head.RequestId, Is.EqualTo(requestId.ToString("N")))
                Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                            Is.EqualTo(0), "Verified V2 mutations must clear pending local intents.")
                // A failed write leaves a pending journal entry: retrying it from
                // another session must retain the original domain error, not fail
                // because the session metadata changed.
                let invalid =
                    { template "CreateTask" taskId with
                        Title = "Duplicate"; Html = html; SessionId = "session-A" }
                let firstError = Client.V2ReviewApi.V2TaskApi.write invalid ctx
                let secondError =
                    Client.V2ReviewApi.V2TaskApi.write { invalid with SessionId = "session-B" } ctx
                match firstError, secondError with
                | Error first, Error second ->
                    Assert.That(sprintf "%A" second, Is.EqualTo(sprintf "%A" first))
                    Assert.That(sprintf "%A" second, Does.Not.Contain("Pending V2 operation UUID reused"))
                | _ -> Assert.Fail("Cross-session retry of a rejected operation changed its result.")
            finally
                if File.Exists configPath then File.Delete configPath)


    [<Test>]
    member _.AgentStateLifecycleAndCheckpointAreExplicit() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit _ ->
            let initial = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(initial.Agent.State, Is.EqualTo("Parked"))
            Assert.That(initial.OperatorActionRequired, Is.False)
            let taskId = "state1"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "State test"
            |> success |> ignore
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
            |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Agent.FocusAttemptId, Is.EqualTo(attemptId.ToString("N")))
            Assert.That(assigned.OperatorActionRequired, Is.True)
            Assert.That(assigned.Focused.IsSome, Is.True)
            Assert.That(assigned.Working.IsNone, Is.True)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) taskId "Owner" 2 1 baseOid sourceRef
            |> success |> ignore
            let executing = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(executing.Agent.State, Is.EqualTo("Working"))
            Assert.That(executing.OperatorActionRequired, Is.False)
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 3 3 1
                "Implemented initial behavior" "Continue with edge cases" |> success |> ignore
            let ready = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(ready.Agent.State, Is.EqualTo("Ready"))
            Assert.That(ready.OperatorActionRequired, Is.True)
            Assert.That(ready.SlotAvailable, Is.True)
            Assert.That(ready.Focused.Value.Attempt.Phase, Is.EqualTo("Paused"))
            let checkpointPath =
                sprintf "tasks/%s/attempts/%s/checkpoints/%d.json"
                    taskId (attemptId.ToString("N")) ready.Agent.StateSequence
            let checkpoint, _ =
                LedgerStore.readAt first (LedgerStore.fetch first |> success) checkpointPath |> success
            Assert.That(checkpoint.Value, Does.Contain("Continue with edge cases"))
            match ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 4 3 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale resume must fail: %A" other)
            ReviewWorkflow.resumeAgent first (Guid.NewGuid()) taskId "Owner" 4 4 1
            |> success |> ignore
            let resumed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(resumed.Agent.State, Is.EqualTo("Working"))
            let c = newCommit "Finished work" "state implementation"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) taskId "Owner" 5 1 reviewId
                sourceRef c "Full cumulative review" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 6 5 1
                "Submitted implementation for review" |> success |> ignore
            let completed = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(completed.Agent.State, Is.EqualTo("Completed"))
            Assert.That(completed.NeedsOrchestratorAssignment, Is.True)
            Assert.That(completed.SlotAvailable, Is.True)
            Assert.That(completed.Focused.Value.Attempt.Phase, Is.EqualTo("Finished"))
            Assert.That(completed.PendingReviews.Length, Is.EqualTo(1))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) taskId reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) taskId reviewId "Reviewer" 2 Accept "Pass"
            |> success |> ignore
            let _, _, attempt = ReviewWorkflow.getTask second taskId |> success
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) taskId "Owner" attempt.Value.Version reviewId
            |> success |> ignore
            let finalized = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finalized.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finalized.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 6 "No more tasks"
            |> success |> ignore
            let parked = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
            Assert.That(parked.Focused.IsNone, Is.True)
            Assert.That(parked.OperatorActionRequired, Is.False)
            ReviewWorkflow.createTask first (Guid.NewGuid()) "state2" "New task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "state2" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Assigned")))

    [<Test>]
    member _.ParkDoesNotStealAssignedOrWorkingAgent() =
        withFixture (fun _ _ first _ _ _ _ _ ->
            let initial = ReviewWorkflow.getAgentInbox first "Owner" |> success
            ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" initial.Agent.Version
                "Still no work" |> success |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "park1" "Park test"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "park1" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            match ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 3 "Interrupt" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Parking assigned work should be rejected: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "park1" "Owner" 1 1
            |> success |> ignore
            match ReviewWorkflow.parkAgent first (Guid.NewGuid()) "Owner" 4 "Interrupt" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Parking Working agent should be rejected: %A" other))


    [<Test>]
    member _.ReviewOfCompletedTaskDoesNotInterruptNewAssignment() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "oldTask"
            let head = newCommit "Implementation" "Old task changes"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "oldTask" "Owner" 3 1
                reviewId sourceRef head "Finished source patch" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "oldTask" "Owner" 4 3 1
                "Submitted and finished implementation" |> success |> ignore
            let completed = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(completed.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.createTask first (Guid.NewGuid()) "newTask" "Next task"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "newTask" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Focused.Value.Task.TaskId, Is.EqualTo("newTask"))
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "newTask" "Owner" 1 1
            |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "oldTask" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "oldTask" reviewId "Reviewer" 2 Accept "Good"
            |> success |> ignore
            let _, _, oldAttempt = ReviewWorkflow.getTask first "oldTask" |> success
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "oldTask" "Owner"
                oldAttempt.Value.Version reviewId |> success |> ignore
            let after = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(after.Agent.State, Is.EqualTo("Working"))
            Assert.That(after.Working.Value.Task.TaskId, Is.EqualTo("newTask"))
            Assert.That(after.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(after.AcceptedPendingIntegration.Head.Task.TaskId, Is.EqualTo("oldTask")))


    [<Test>]
    member _.LateRequestedChangesQueueSameAttemptWithoutPreemptingNewWork() =
        withFixture (fun _ _ first second baseOid sourceRef newCommit initTask ->
            initTask "late1"
            let firstAttempt = (ReviewWorkflow.getTask first "late1" |> success |> fun (_,_,a) -> a.Value)
            let head = newCommit "Review candidate" "candidate A"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "late1" "Owner" 3 1
                requestId sourceRef head "Initial cumulative patch" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "late1" "Owner" 4 3 1
                "Submitted and finished" |> success |> ignore
            ReviewWorkflow.createTask first (Guid.NewGuid()) "late2" "Next assignment" |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "late2" (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late2" "Owner" 1 1 |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "late1" requestId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "late1" requestId "Reviewer"
                2 RequestChanges "Please fix A" |> success |> ignore
            let busy = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(busy.Agent.State, Is.EqualTo("Working"))
            Assert.That(busy.Working.Value.Task.TaskId, Is.EqualTo("late2"))
            Assert.That(busy.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(busy.RunnableQueue.Head.Task.TaskId, Is.EqualTo("late1"))
            Assert.That(busy.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(busy.RunnableQueue.Head.Attempt.AttemptId, Is.EqualTo(firstAttempt.AttemptId))
            Assert.That(busy.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            let notice = busy.UnacknowledgedFeedback.Head
            Assert.That(notice.Status, Is.EqualTo("ChangesRequested"))
            Assert.That(notice.Feedback, Is.EqualTo("Please fix A"))
            Assert.That(notice.DecisionSequence, Is.GreaterThan(0L))
            match ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "late1" "Owner" 6 6 2 "Ready to fix" with
            | Error(AgentBusy _) -> ()
            | other -> Assert.Fail(sprintf "Reissue must not preempt another task: %A" other)
            let acknowledgeOp = Guid.NewGuid()
            let ack binding =
                ReviewWorkflow.acknowledgeReviewFeedback binding acknowledgeOp
                    "late1" "Owner" notice.DecisionSequence
            Assert.That((ack first |> success).AlreadyApplied, Is.False)
            Assert.That((ack second |> success).AlreadyApplied, Is.True)
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).UnacknowledgedFeedback.Length,
                        Is.EqualTo(0))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "late2" "Owner" 2 6 1
                "Other task done" |> success |> ignore
            let idle = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(idle.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "late1" "Owner" 7 7 2
                "Reissue requested changes" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(assigned.Focused.Value.Task.TaskId, Is.EqualTo("late1"))
            Assert.That(assigned.Focused.Value.Attempt.AttemptId, Is.EqualTo(firstAttempt.AttemptId))
            Assert.That(assigned.Focused.Value.Attempt.AssignmentGeneration, Is.EqualTo(2))
            match ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late1" "Owner" 7 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old assignment generation must be rejected: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "late1" "Owner" 8 2
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Working")))

    [<Test>]
    member _.FeedbackDecisionsStayIndividuallyActionableAcrossNewReviews() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "multi"
            let firstHead = newCommit "A" "A"
            let firstId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "multi" "Owner" 3 1 firstId
                sourceRef firstHead "First review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "multi" firstId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "multi" firstId "Reviewer" 2
                RequestChanges "First feedback" |> success |> ignore
            let secondHead = newCommit "B" "A and B"
            let secondId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "multi" "Owner" 5 1 secondId
                sourceRef secondHead "Cumulative revised review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "multi" secondId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "multi" secondId "Reviewer" 2
                Accept "Second approval" |> success |> ignore
            let inbox = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(inbox.UnacknowledgedFeedback.Length, Is.EqualTo(2))
            Assert.That(inbox.UnacknowledgedFeedback.Head.Feedback, Is.EqualTo("First feedback"))
            Assert.That(inbox.UnacknowledgedFeedback.Tail.Head.Feedback, Is.EqualTo("Second approval"))
            let oldest = inbox.UnacknowledgedFeedback.Head
            let latest = inbox.UnacknowledgedFeedback.Tail.Head
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "multi"
                "Owner" latest.DecisionSequence |> success |> ignore
            let remaining = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(remaining.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            Assert.That(remaining.UnacknowledgedFeedback.Head.DecisionSequence,
                        Is.EqualTo(oldest.DecisionSequence))
            ReviewWorkflow.acknowledgeReviewFeedback first (Guid.NewGuid()) "multi"
                "Owner" oldest.DecisionSequence |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).UnacknowledgedFeedback.Length,
                        Is.EqualTo(0)))

    [<Test>]
    member _.ExplicitRecoveryReissuesSameAttemptGenerationWithCheckpoint() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "recover"
            let before = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(before.Agent.State, Is.EqualTo("Working"))
            let attemptId = before.Working.Value.Attempt.AttemptId
            let operationId = Guid.NewGuid()
            let recover binding =
                ReviewWorkflow.recoverAgentAssignment binding operationId
                    "recover" "Owner" 3 3 1 "Explicitly recovering a lost frontend session"
            Assert.That((recover first |> success).AlreadyApplied, Is.False)
            Assert.That((recover second |> success).AlreadyApplied, Is.True)
            let recovered = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(recovered.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(recovered.OperatorActionRequired, Is.True)
            Assert.That(recovered.SlotAvailable, Is.True)
            Assert.That(recovered.Focused.Value.Attempt.AttemptId, Is.EqualTo(attemptId))
            Assert.That(recovered.Focused.Value.Attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(recovered.Focused.Value.Attempt.Phase, Is.EqualTo("Queued"))
            let cpPath = $"tasks/recover/attempts/{attemptId}/checkpoints/{recovered.Agent.StateSequence}.json"
            let checkpoint, _ =
                LedgerStore.readAt second (LedgerStore.fetch second |> success) cpPath |> success
            Assert.That(checkpoint.Value, Does.Contain("Recovery"))
            match ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "recover" "Owner" 4 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Old generation must not start: %A" other)
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "recover" "Owner" 4 2
            |> success |> ignore
            let head = newCommit "Continued implementation" "continued changes"
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "recover" "Owner" 5 2
                (Guid.NewGuid()) sourceRef head "Same task, recovered generation"
            |> success |> ignore
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Working")))


    [<Test>]
    member _.ChangesRequestedBeforeCompletionAreAlsoQueued() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "race"
            let head = newCommit "Candidate" "Candidate"
            let request = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "race" "Owner" 3 1
                request sourceRef head "Review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "race" request "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "race" request "Reviewer" 2
                RequestChanges "Needs correction" |> success |> ignore
            let mid = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(mid.Agent.State, Is.EqualTo("Working"))
            Assert.That(mid.Working.Value.Attempt.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(mid.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "race" "Owner" 5 3 1
                "Stopped before correction" |> success |> ignore
            let finished = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(finished.Agent.State, Is.EqualTo("Completed"))
            Assert.That(finished.RunnableQueue.Length, Is.EqualTo(1))
            Assert.That(finished.RunnableQueue.Head.Task.TaskId, Is.EqualTo("race"))
            Assert.That(finished.RunnableQueue.Head.Attempt.AssignmentGeneration, Is.EqualTo(2))
            ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid()) "race" "Owner" 6 4 2
                "Reissue before-completion rejection" |> success |> ignore
            let assigned = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(assigned.Agent.State, Is.EqualTo("Assigned")))


    [<Test>]
    member _.IntegrationRequiresExactRemoteApprovedHeadAndSeparateClosure() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "integrate"
            let approvedHead = newCommit "Accepted source" "accepted source tree"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "integrate" "Owner" 3 1
                reviewId sourceRef approvedHead "Cumulative patch" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "integrate" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "integrate" reviewId "Reviewer" 2
                Accept "Accepted exact change" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "integrate" "Owner" 5 3 1
                "Completed source" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "integrate" "Owner" 6 reviewId
            |> success |> ignore
            let actionId = Guid.NewGuid()
            let planOp = Guid.NewGuid()
            let plan binding =
                ReviewWorkflow.planIntegration binding planOp "integrate" actionId reviewId 7
                    sourceRef baseOid
            Assert.That((plan first |> success).AlreadyApplied, Is.False)
            Assert.That((plan second |> success).AlreadyApplied, Is.True)
            let pending = ReviewWorkflow.inspectIntegration second "integrate" actionId |> success
            Assert.That(pending.Integration.Status, Is.EqualTo("PendingSourceAction"))
            Assert.That(pending.Integration.ApprovedHead, Is.EqualTo(approvedHead))
            Assert.That(pending.Integration.ExpectedTargetHead, Is.EqualTo(baseOid))
            Assert.That(pending.Integration.VerificationMethod, Is.Null)
            Assert.That(DateTimeOffset.Parse pending.Integration.PlannedAtUtc,
                        Is.LessThanOrEqualTo(DateTimeOffset.UtcNow))
            Assert.That(pending.RemoteState, Is.EqualTo("PendingPush"))
            Assert.That(pending.ObservedTargetHead, Is.EqualTo(baseOid))
            Assert.That(git one [ "ls-remote"; "origin"; sourceRef ], Does.StartWith(baseOid))
            match ReviewWorkflow.recordIntegration first (Guid.NewGuid()) "integrate" actionId 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Local source commit must not count as remote integration: %A" other)
            match ReviewWorkflow.closeIntegratedTask first (Guid.NewGuid()) "integrate" actionId 3 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Cannot close before recorded integration: %A" other)
            ReviewWorkflow.markIntegrationPendingPush second (Guid.NewGuid()) "integrate" actionId 1
            |> success |> ignore
            let waiting = ReviewWorkflow.inspectIntegration first "integrate" actionId |> success
            Assert.That(waiting.Integration.Status, Is.EqualTo("PendingPush"))
            Assert.That(waiting.RemoteState, Is.EqualTo("PendingPush"))
            git one [ "push"; "origin"; $"HEAD:{sourceRef}" ] |> ignore
            let ready = ReviewWorkflow.inspectIntegration second "integrate" actionId |> success
            Assert.That(ready.RemoteState, Is.EqualTo("ReadyToRecord"))
            Assert.That(ready.ObservedTargetHead, Is.EqualTo(approvedHead))
            let recordId = Guid.NewGuid()
            let record binding =
                ReviewWorkflow.recordIntegration binding recordId "integrate" actionId 2
            Assert.That((record second |> success).AlreadyApplied, Is.False)
            Assert.That((record first |> success).AlreadyApplied, Is.True)
            let verified = ReviewWorkflow.inspectIntegration first "integrate" actionId |> success
            Assert.That(verified.Integration.Status, Is.EqualTo("Recorded"))
            Assert.That(verified.Integration.Version, Is.EqualTo(3))
            Assert.That(verified.Integration.VerificationMethod, Is.EqualTo("RemoteExactFastForwardCommit"))
            Assert.That(verified.Integration.ObservedTargetHead, Is.EqualTo(approvedHead))
            Assert.That(verified.Integration.VerifiedSequence, Is.GreaterThan(0L))
            Assert.That(verified.Integration.VerifiedAtUtc, Is.Not.Null)
            let _, integratedTask, _ = ReviewWorkflow.getTask second "integrate" |> success
            Assert.That(integratedTask.Lifecycle, Is.EqualTo("Integrated"))
            Assert.That((ReviewWorkflow.getAgentInbox first "Owner" |> success).AcceptedPendingIntegration.Length,
                        Is.EqualTo(0))
            let agentBefore = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(agentBefore.Agent.State, Is.EqualTo("Completed"))
            ReviewWorkflow.closeIntegratedTask first (Guid.NewGuid()) "integrate" actionId 4
            |> success |> ignore
            let _, closed, _ = ReviewWorkflow.getTask second "integrate" |> success
            Assert.That(closed.Lifecycle, Is.EqualTo("Closed"))
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Completed"))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))

    [<Test>]
    member _.IntegrationRejectsUnapprovedRemoteHeadAndStaleTargetPlan() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "unexpected"
            let approvedHead = newCommit "Reviewed patch" "reviewed changes"
            let reviewId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "unexpected" "Owner" 3 1
                reviewId sourceRef approvedHead "Pinned review" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "unexpected" reviewId "Reviewer" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "unexpected" reviewId "Reviewer" 2
                Accept "Approved" |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) "unexpected" "Owner" 5 3 1
                "Completed" |> success |> ignore
            ReviewWorkflow.finalizeImplementation first (Guid.NewGuid()) "unexpected" "Owner" 6 reviewId
            |> success |> ignore
            match ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected"
                    (Guid.NewGuid()) reviewId 7 sourceRef approvedHead with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Target OID must match remote at planning: %A" other)
            match ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected"
                    (Guid.NewGuid()) reviewId 7 "refs/heads/jarvis-orchestration" baseOid with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Ledger orphan ref cannot be source target: %A" other)
            let actionId = Guid.NewGuid()
            ReviewWorkflow.planIntegration first (Guid.NewGuid()) "unexpected" actionId reviewId 7
                sourceRef baseOid |> success |> ignore
            let unapproved = newCommit "Unreviewed extra" "reviewed changes plus unapproved extra"
            git one [ "push"; "origin"; $"HEAD:{sourceRef}" ] |> ignore
            let observed = ReviewWorkflow.inspectIntegration second "unexpected" actionId |> success
            Assert.That(observed.RemoteState, Is.EqualTo("TargetChanged"))
            Assert.That(observed.ObservedTargetHead, Is.EqualTo(unapproved))
            match ReviewWorkflow.recordIntegration second (Guid.NewGuid()) "unexpected" actionId 1 with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Unreviewed advanced remote must be rejected: %A" other)
            let _, task, attempt = ReviewWorkflow.getTask first "unexpected" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("Implemented"))
            Assert.That(attempt.Value.IntegrationActionId, Is.EqualTo(actionId.ToString("N")))
            Assert.That((ReviewWorkflow.inspectIntegration first "unexpected" actionId |> success).Integration.Status,
                        Is.EqualTo("PendingSourceAction"))
            Assert.That(git one [ "ls-remote"; "origin"; $"refs/heads/jarvis-review/unexpected/{reviewId:N}/r1" ],
                        Does.StartWith(approvedHead)))


    [<Test>]
    member _.CanonicalSpecAccepts512KiBAndRejectsOversizeWithoutLedgerWrite() =
        withFixture (fun _ _ first second _ _ _ _ ->
            // The exact UTF-8 ceiling is measured on the committed HTML bytes.
            let exact = "<p>" + String.replicate (512 * 1024 - 7) "x" + "</p>"
            Assert.That(System.Text.Encoding.UTF8.GetByteCount exact, Is.EqualTo(512 * 1024))
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "largehtml"
                "Large canonical task" exact |> success |> ignore
            let _, task, _, received, _ =
                ReviewWorkflow.getTaskWithSpec second "largehtml" |> success
            Assert.That(task.Lifecycle, Is.EqualTo("Open"))
            Assert.That(received, Is.EqualTo(exact))
            let before = (LedgerStore.fetch first |> success).Commit
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "tooLarge"
                    "Oversize" (exact + "X") with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Oversized HTML must fail before mutation: %A" other)
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before))
            match ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) "activeHtml"
                    "Active HTML" "<article onclick='alert(1)'>unsafe</article>" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Active HTML must not be published: %A" other)
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before)))

    [<Test>]
    member _.EntityReadersRejectUnknownPhaseAndMissingSchema() =
        let mutateDocument transform =
            withFixture (fun _ _ first second _ _ _ _ ->
                ReviewWorkflow.createTask first (Guid.NewGuid()) "broken" "Broken" |> success |> ignore
                let snapshot = LedgerStore.fetch first |> success
                let path = "tasks/broken/task.json"
                let text, blob = LedgerStore.readAt first snapshot path |> success
                let corrupted = transform text.Value
                let intent =
                    { OperationId = Guid.NewGuid(); Actor = "Corruption fixture"
                      Kind = "CorruptTaskForReadTest"; RequestJson = "{}" }
                LedgerStore.transact first intent (fun _ ->
                    Ok [ { Path = path; ExpectedBlobOid = blob; Content = corrupted } ])
                |> success |> ignore
                match ReviewWorkflow.getTaskWithSpec second "broken" with
                | Error(LedgerIntegrityError _) -> ()
                | other -> Assert.Fail(sprintf "Malformed entity must fail closed: %A" other))
        mutateDocument (fun text -> text.Replace("\"lifecycle\":\"Open\"", "\"lifecycle\":\"Unknown\""))
        mutateDocument (fun text -> text.Replace("\"schemaVersion\":2,", ""))
        mutateDocument (fun text -> text.Replace("\"specVersion\":1,", ""))


    [<Test>]
    member _.LedgerBindingNeedsExplicitTrustAndPersistsVerifiedHead() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let current = (LedgerStore.fetch first |> success).Commit
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let bind approved expected =
                Client.V2ReviewApi.ReviewV2Binding.bindExisting
                    "owner" ctx first.ProjectId first.LedgerId first.Epoch
                    first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl expected approved
            match bind false current with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Unapproved bind must refuse: %A" other)
            Assert.That(File.Exists path, Is.False)
            match bind true (String.replicate 40 "a") with
            | Error(Client.V2LedgerFailureError info) when info.Code = "LedgerHistoryUnavailable" -> ()
            | other -> Assert.Fail(sprintf "Wrong trust head must refuse: %A" other)
            Assert.That(File.Exists path, Is.False)
            let stored = bind true current |> function
                | Ok value -> value
                | Error error -> failwithf "Bind failed: %A" error
            Assert.That(stored.HighestTrustedHead, Is.EqualTo(current))
            Assert.That(File.Exists path, Is.True)
            let extra = ReviewWorkflow.createTask first (Guid.NewGuid()) "trustTask" "Trust" |> success
            let fresh = (LedgerStore.fetch first |> success).Commit
            Assert.That(fresh, Is.Not.EqualTo(current))
            let loaded = Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx
            match loaded with
            | Ok binding -> Assert.That(binding.ProjectId, Is.EqualTo(first.ProjectId))
            | Error error -> Assert.Fail(sprintf "Verified load failed: %A" error)
            let config = System.Text.Json.JsonSerializer.Deserialize<Client.V2ReviewApi.ReviewV2OptIn>(
                File.ReadAllText path)
            Assert.That(config.HighestTrustedHead, Is.EqualTo(fresh))
            git one [ "remote"; "set-url"; "origin"; "file:///not-the-approved-remote.git" ] |> ignore
            match Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx with
            | Error(Client.V2LedgerFailureError failure) when failure.Code = "InvalidBinding" -> ()
            | other -> Assert.Fail(sprintf "Changed remote must be rejected: %A" other)
            git one [ "remote"; "set-url"; "origin"; first.ApprovedFetchUrl ] |> ignore
            Assert.That((Client.V2ReviewApi.ReviewV2Binding.health "owner" ctx |> function
                         | Ok value -> value.trustedLedgerHead
                         | Error error -> failwithf "Health: %A" error), Is.EqualTo(fresh))
            File.Delete path)

    [<Test>]
    member _.InitializeAndRebindRequireExplicitOperatorConsent() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let root = Path.GetDirectoryName one
            let newRemote = Path.Combine(root, "fresh-remote.git")
            git root [ "init"; "--bare"; newRemote ] |> ignore
            git one [ "remote"; "add"; "fresh"; newRemote ] |> ignore
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let project = Guid.NewGuid()
            let ledger = Guid.NewGuid()
            let create approve =
                Client.V2ReviewApi.ReviewV2Binding.initializeNew
                    "owner" ctx project ledger 1 "fresh" newRemote newRemote approve
            match create false with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Unapproved init must fail: %A" other)
            Assert.That(git one [ "ls-remote"; "fresh"; "refs/heads/jarvis-orchestration" ], Is.Empty)
            let created = create true |> function
                | Ok value -> value
                | Error error -> failwithf "Initialization failed: %A" error
            Assert.That(created.ProjectId, Is.EqualTo(project.ToString("D")))
            Assert.That(created.HighestTrustedHead.Length, Is.EqualTo(40))
            let otherRemote = Path.Combine(root, "mirror.git")
            git root [ "clone"; "--bare"; newRemote; otherRemote ] |> ignore
            git one [ "remote"; "add"; "mirror"; otherRemote ] |> ignore
            let rebind approved expected =
                Client.V2ReviewApi.ReviewV2Binding.rebindRemote
                    "owner" ctx "mirror" otherRemote otherRemote expected approved
            match rebind false created.HighestTrustedHead with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Rebind without approval must fail: %A" other)
            match rebind true (String.replicate 40 "f") with
            | Error(Client.V2LedgerFailureError info) when info.Code = "LedgerHistoryUnavailable" -> ()
            | other -> Assert.Fail(sprintf "Rebind to mismatched trust must fail: %A" other)
            let rebound = rebind true created.HighestTrustedHead |> function
                | Ok value -> value
                | Error error -> failwithf "Rebind failed: %A" error
            Assert.That(rebound.RemoteName, Is.EqualTo("mirror"))
            Assert.That((Client.V2ReviewApi.ReviewV2Binding.health "owner" ctx |> function
                         | Ok value -> value.remoteName
                         | Error error -> failwithf "Health: %A" error), Is.EqualTo("mirror"))
            File.Delete path)


    [<Test>]
    member _.PendingV2IntentSurvivesFailedCallAndCanReplayUnchanged() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let operationId = Guid.NewGuid()
            let command = {| action = "CreateTask"; taskId = "restart"; title = "Original" |}
            let fail () : Result<int, Client.EffectError> =
                Error(Client.GenericError "Simulated lost server response")
            let execute f =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first operationId
                    "V2TaskWrite" command f
            match execute fail with
            | Error(Client.GenericError _) -> ()
            | other -> Assert.Fail(sprintf "Simulated unknown outcome should preserve intent: %A" other)
            let pending () =
                Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success
            let saved = pending ()
            Assert.That(saved.Length, Is.EqualTo(1))
            Assert.That(saved.Head.OperationId, Is.EqualTo(operationId.ToString("D")))
            Assert.That(saved.Head.CommandType, Is.EqualTo("V2TaskWrite"))
            Assert.That(saved.Head.CommandJson, Does.Contain("restart"))
            Assert.That(saved.Head.CommandSha256.Length, Is.EqualTo(64))
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let lookup : Common.V2LedgerReadCommand =
                { ProjectName = "owner"; Action = "PendingOperations"; OperationId = "" }
            let listing = Client.V2ReviewApi.V2LedgerApi.read lookup ctx |> success
            let json = System.Text.Json.JsonSerializer.Serialize(listing)
            Assert.That(json, Does.Contain(operationId.ToString("D")))
            Assert.That(json, Does.Contain("Original"))
            let mismatch =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first operationId
                    "V2TaskWrite" {| action = "CreateTask"; taskId = "restart"; title = "Changed" |}
                    (fun () -> Ok 999)
            match mismatch with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "UUID conflict must reject altered intent: %A" other)
            Assert.That((pending ()).Length, Is.EqualTo(1))
            let recovered = execute (fun () -> Ok 17) |> success
            Assert.That(recovered, Is.EqualTo(17))
            Assert.That((pending ()).Length, Is.EqualTo(0))
            let lostId = Guid.NewGuid()
            let retryLost f =
                Client.V2ReviewApi.V2PendingIntentJournal.execute first lostId
                    "V2ReviewWrite" {| action = "Decide"; requestId = "r1" |} f
            match retryLost (fun () ->
                    Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                        (Client.LedgerStore.OutcomeUnknown "Network stopped after push"))) with
            | Error(Client.V2LedgerFailureError info) ->
                Assert.That(info.Code, Is.EqualTo("OutcomeUnknown"))
                Assert.That(info.OperationId, Is.EqualTo(Some(lostId.ToString("D"))))
            | other -> Assert.Fail(sprintf "Unknown result must retain typed UUID: %A" other)
            Assert.That((pending ()).Length, Is.EqualTo(1))
            retryLost (fun () -> Ok 19) |> success |> ignore
            Assert.That((pending ()).Length, Is.EqualTo(0)))


    [<Test>]
    member _.ReconciliationNeedsVerifiedRemoteReceiptAndExplicitAcknowledgment() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let command taskId =
                System.Text.Json.JsonSerializer.Serialize(
                    {| Action = "CreateTask"; TaskId = taskId
                       Title = taskId; Html = "<h1>Task</h1>" |})
            let acceptedId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first acceptedId "V2TaskWrite" (command "accepted") |> success
            ReviewWorkflow.createTaskWithSpec first acceptedId "accepted" "accepted" "<h1>Task</h1>"
            |> success |> ignore

            let pendingId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first pendingId "V2TaskWrite" (command "notpushed") |> success
            let mismatchedId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first mismatchedId "V2TaskWrite" (command "wrongTask") |> success
            ReviewWorkflow.createTaskWithSpec first mismatchedId "realTask" "realTask" "<h1>Task</h1>"
            |> success |> ignore

            let sameTaskDifferentPayloadId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist first sameTaskDifferentPayloadId
                "V2TaskWrite" (command "sameTask") |> success
            ReviewWorkflow.createTaskWithSpec first sameTaskDifferentPayloadId
                "sameTask" "sameTask" "<h1>Different body</h1>" |> success |> ignore

            let foreignId = Guid.NewGuid()
            Client.V2ReviewApi.V2PendingIntentJournal.persist { first with LedgerId = Guid.NewGuid() } foreignId
                "V2TaskWrite" (command "foreign") |> success

            let before = (LedgerStore.fetch first |> success).Commit
            let inspected = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(inspected.Length, Is.EqualTo(5))
            let status (id: Guid) =
                inspected |> List.find (fun entry -> entry.operationId = id.ToString("D"))
            Assert.That((status acceptedId).disposition, Is.EqualTo("Applied"))
            Assert.That((status pendingId).disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            Assert.That((status mismatchedId).disposition, Is.EqualTo("IdentityMismatch"))
            Assert.That((status sameTaskDifferentPayloadId).disposition, Is.EqualTo("IdentityMismatch"))
            Assert.That((status foreignId).disposition, Is.EqualTo("BindingMismatch"))
            Assert.That(inspected |> List.forall (fun entry -> entry.snapshotCommit = before), Is.True)
            let accepted = status acceptedId
            let remoteHash = accepted.operation.Value.RequestHash
            let acknowledge (id: Guid) requestHash =
                Client.V2ReviewApi.V2PendingIntentJournal.acknowledge first id requestHash
            match acknowledge acceptedId (String.replicate 64 "f") with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Wrong hash must not acknowledge: %A" other)
            match acknowledge pendingId remoteHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Uncommitted operation must remain pending: %A" other)
            match acknowledge mismatchedId (status mismatchedId).operation.Value.RequestHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Mismatched command must remain pending: %A" other)
            match acknowledge sameTaskDifferentPayloadId (status sameTaskDifferentPayloadId).operation.Value.RequestHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Changed payload on same task must remain pending: %A" other)
            match acknowledge foreignId remoteHash with
            | Error(Client.ValidationError _) -> ()
            | other -> Assert.Fail(sprintf "Wrong ledger identity must remain pending: %A" other)
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length, Is.EqualTo(5))
            let recorded = acknowledge acceptedId remoteHash |> success
            Assert.That(recorded.acknowledged, Is.True)
            Assert.That(recorded.verifiedLedgerHead, Is.EqualTo(before))
            Assert.That(recorded.originalResult, Does.Contain(acceptedId.ToString("D")))
            Assert.That(recorded.alreadyAcknowledged, Is.False)
            let repeat = acknowledge acceptedId remoteHash |> success
            Assert.That(repeat.alreadyAcknowledged, Is.True)
            Assert.That(repeat.originalResult, Is.EqualTo(recorded.originalResult))
            let remains = Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success
            Assert.That(remains.Length, Is.EqualTo(4))
            Assert.That(remains |> List.exists (fun entry -> entry.OperationId = acceptedId.ToString("D")),
                        Is.False)
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))


    [<Test>]
    member _.LostResponseRecoversAcceptedRemoteOperationButNeverGuessesFailure() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let command name =
                {| Action = "CreateTask"; TaskId = name; Title = name
                   Html = "<h1>Original</h1>" |}
            let operationId = Guid.NewGuid()
            let remoteAccepted () : Result<LedgerWrite, Client.EffectError> =
                ReviewWorkflow.createTaskWithSpec first operationId
                    "alreadyaccepted" "alreadyaccepted" "<h1>Original</h1>"
                |> success |> ignore
                Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                    (OutcomeUnknown "A source push succeeded but the response was lost"))
            let recovered =
                Client.V2ReviewApi.V2PendingIntentJournal.executeLedgerWrite first operationId
                    "V2TaskWrite" (command "alreadyaccepted") remoteAccepted |> success
            Assert.That(recovered.AlreadyApplied, Is.True)
            Assert.That(recovered.Operation.OperationId, Is.EqualTo(operationId.ToString("D")))
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0))
            let onlyPendingId = Guid.NewGuid()
            let before = (LedgerStore.fetch first |> success).Commit
            let neverAccepted () : Result<LedgerWrite, Client.EffectError> =
                Error(Client.V2ReviewApi.V2LedgerErrors.toEffect
                    (OutcomeUnknown "No remote confirmation"))
            match Client.V2ReviewApi.V2PendingIntentJournal.executeLedgerWrite first onlyPendingId
                    "V2TaskWrite" (command "notaccepted") neverAccepted with
            | Error(Client.V2LedgerFailureError failure) ->
                Assert.That(failure.Code, Is.EqualTo("OutcomeUnknown"))
                Assert.That(failure.OperationId, Is.EqualTo(Some(onlyPendingId.ToString("D"))))
                Assert.That(failure.VerifiedLedgerHead, Is.EqualTo(Some before))
            | other -> Assert.Fail(sprintf "Unknown outcome must remain uncertain: %A" other)
            let pending = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(pending.Length, Is.EqualTo(1))
            Assert.That(pending.Head.disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(before)))


    [<Test>]
    member _.ReviewSubmissionReconciliationMatchesExactPinnedRevision() =
        withFixture (fun one _ first _ _ sourceRef newCommit initTask ->
            initTask "submission"
            let head = newCommit "Review candidate" "candidate"
            let requestId = Guid.NewGuid()
            let operationId = Guid.NewGuid()
            let original =
                {| Action = "Submit"; TaskId = "submission"; ActorId = "Owner"
                   ExpectedVersion = 3; Generation = 1
                   RequestId = requestId.ToString("D")
                   SourceRef = sourceRef; HeadOid = head; Summary = "Review" |}
            Client.V2ReviewApi.V2PendingIntentJournal.persist first operationId
                "V2ReviewWrite" (System.Text.Json.JsonSerializer.Serialize original) |> success
            ReviewWorkflow.submitReview first operationId "submission" "Owner"
                3 1 requestId sourceRef head "Review" |> success |> ignore
            let entries = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            let item = entries |> List.find (fun entry -> entry.operationId = operationId.ToString("D"))
            Assert.That(item.disposition, Is.EqualTo("Applied"))
            let hash = item.operation.Value.RequestHash
            Client.V2ReviewApi.V2PendingIntentJournal.acknowledge first operationId hash
            |> success |> ignore
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0)))


    [<Test>]
    member _.DomainResultsRetainExactEntityVersionsAndCheckpointAfterReplay() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "domainReceipt"
            let createId = Guid.NewGuid()
            let created =
                ReviewWorkflow.createTaskWithSpec first createId taskId "Domain"
                    "<h1>Domain</h1>" |> success
            use createdResult = System.Text.Json.JsonDocument.Parse(created.Operation.OriginalResultJson)
            let root = createdResult.RootElement
            Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(2))
            let createdEffects = root.GetProperty("entityResults").EnumerateArray() |> Seq.toList
            let task =
                createdEffects |> List.find (fun row ->
                    row.GetProperty("entityKind").GetString() = "Task")
            Assert.That(task.GetProperty("entityVersion").GetInt32(), Is.EqualTo(1))
            Assert.That(task.GetProperty("lifecycle").GetString(), Is.EqualTo("Open"))
            Assert.That(task.GetProperty("taskId").GetString(), Is.EqualTo(taskId))
            Assert.That(
                createdEffects |> List.exists (fun row ->
                    row.GetProperty("entityKind").GetString() = "TaskSpec"),
                Is.True)

            let assignmentId = Guid.NewGuid()
            let attemptId = Guid.NewGuid()
            let assigned = ReviewWorkflow.assignTask first assignmentId taskId
                               attemptId "Owner" "Reviewer" |> success
            use assignmentResult =
                System.Text.Json.JsonDocument.Parse(assigned.Operation.OriginalResultJson)
            let effects = assignmentResult.RootElement.GetProperty("entityResults").EnumerateArray() |> Seq.toList
            let attempt =
                effects |> List.find (fun row ->
                    row.GetProperty("entityKind").GetString() = "Attempt")
            let owner =
                effects |> List.find (fun row ->
                    row.GetProperty("entityKind").GetString() = "Agent")
            Assert.That(attempt.GetProperty("entityVersion").GetInt32(), Is.EqualTo(1))
            Assert.That(attempt.GetProperty("phase").GetString(), Is.EqualTo("Queued"))
            Assert.That(attempt.GetProperty("assignmentGeneration").GetInt32(), Is.EqualTo(1))
            Assert.That(attempt.GetProperty("attemptId").GetString(), Is.EqualTo(attemptId.ToString("N")))
            Assert.That(owner.GetProperty("state").GetString(), Is.EqualTo("Assigned"))
            Assert.That(owner.GetProperty("entityVersion").GetInt32(), Is.EqualTo(2))

            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 1 1 |> success |> ignore
            let pauseId = Guid.NewGuid()
            let paused =
                ReviewWorkflow.yieldAgent first pauseId taskId "Owner" 2 3 1
                    "Measured current work" "Continue the implementation" |> success
            use yieldResult = System.Text.Json.JsonDocument.Parse(paused.Operation.OriginalResultJson)
            let yielded = yieldResult.RootElement.GetProperty("entityResults").EnumerateArray() |> Seq.toList
            let checkpoint =
                yielded |> List.find (fun row ->
                    row.GetProperty("entityKind").GetString() = "Checkpoint")
            Assert.That(checkpoint.GetProperty("checkpointKind").GetString(), Is.EqualTo("Yield"))
            Assert.That(checkpoint.GetProperty("checkpointSequence").GetInt64(),
                        Is.EqualTo(paused.Operation.Sequence))
            let pausedAttempt =
                yielded |> List.find (fun row ->
                    row.GetProperty("entityKind").GetString() = "Attempt")
            Assert.That(pausedAttempt.GetProperty("phase").GetString(), Is.EqualTo("Paused"))
            Assert.That(pausedAttempt.GetProperty("entityVersion").GetInt32(), Is.EqualTo(3))

            let replay =
                ReviewWorkflow.createTaskWithSpec second createId taskId "Domain"
                    "<h1>Domain</h1>" |> success
            Assert.That(replay.AlreadyApplied, Is.True)
            Assert.That(replay.Operation.OriginalResultJson, Is.EqualTo(created.Operation.OriginalResultJson))
            Assert.That(replay.CurrentHead, Is.Not.EqualTo(created.CurrentHead))
            let latest = LedgerStore.fetch second |> success
            let origin = LedgerStore.getOperationOriginAt second latest replay.Operation |> success
            Assert.That(origin, Is.EqualTo(created.CurrentHead))
            Assert.That(origin, Is.Not.EqualTo(latest.Commit)))


    [<Test>]
    member _.RemoteOutagePreservesPendingIntentUntilVerifiedRetry() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let operationId = Guid.NewGuid()
            let command =
                {| Action = "CreateTask"; TaskId = "offline"
                   Title = "Offline"; Html = "<h1>Offline</h1>" |}
            let attempt () : Result<LedgerWrite, Client.EffectError> =
                ReviewWorkflow.createTaskWithSpec first operationId
                    "offline" "Offline" "<h1>Offline</h1>"
                |> Result.mapError Client.V2ReviewApi.V2LedgerErrors.toEffect
            let execute () =
                Client.V2ReviewApi.V2PendingIntentJournal.executeLedgerWrite
                    first operationId "V2TaskWrite" command attempt
            let unavailable = first.ApprovedFetchUrl + ".unavailable"
            Directory.Move(first.ApprovedFetchUrl, unavailable)
            try
                match execute () with
                | Error(Client.V2LedgerFailureError error) ->
                    Assert.That(error.Code, Is.EqualTo("RemoteUnavailable"))
                    Assert.That(error.OperationId, Is.EqualTo(Some(operationId.ToString("D"))))
                | other -> Assert.Fail(sprintf "Unavailable remote must never accept a local write: %A" other)
                match Client.V2ReviewApi.V2PendingIntentJournal.reconcile first with
                | Error(Client.V2LedgerFailureError error) ->
                    Assert.That(error.Code, Is.EqualTo("RemoteUnavailable"))
                | other -> Assert.Fail(sprintf "Reconcile must fail closed during outage: %A" other)
                Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                            Is.EqualTo(1), "Offline reconciliation must not delete evidence.")
            finally Directory.Move(unavailable, first.ApprovedFetchUrl)

            let before = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(before.Head.disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            let recovered = execute () |> success
            Assert.That(recovered.AlreadyApplied, Is.False)
            let _, recorded = LedgerStore.getOperationStatus first operationId |> success
            Assert.That(recorded.IsSome, Is.True)
            Assert.That(recorded.Value.OriginalResultJson,
                        Is.EqualTo(recovered.Operation.OriginalResultJson))
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0)))


    [<Test>]
    member _.RewrittenRemoteNeverReconcilesOrDiscardsPendingIntent() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let original = (LedgerStore.fetch first |> success).Commit
            let id = Guid.NewGuid()
            let command =
                System.Text.Json.JsonSerializer.Serialize(
                    {| Action = "CreateTask"; TaskId = "rewrite"
                       Title = "Rewrite"; Html = "<h1>Rewrite</h1>" |})
            Client.V2ReviewApi.V2PendingIntentJournal.persist first id "V2TaskWrite" command
            |> success
            let sourceHead = git one [ "rev-parse"; "HEAD" ]
            let ledgerRef = "refs/heads/jarvis-orchestration"
            git one [ "push"; "--force"; "origin"; $"{sourceHead}:{ledgerRef}" ] |> ignore
            try
                match Client.V2ReviewApi.V2PendingIntentJournal.reconcile first with
                | Error(Client.V2LedgerFailureError failure) ->
                    Assert.That(failure.Code, Is.EqualTo("LedgerIntegrityError"))
                | other -> Assert.Fail(sprintf "Rewritten remote cannot authorize recovery: %A" other)
                Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                            Is.EqualTo(1))
            finally
                git one [ "push"; "--force"; "origin"; $"{original}:{ledgerRef}" ] |> ignore
            let recovered = Client.V2ReviewApi.V2PendingIntentJournal.reconcile first |> success
            Assert.That(recovered.Length, Is.EqualTo(1))
            Assert.That(recovered.Head.disposition, Is.EqualTo("NotFoundAtVerifiedHead"))
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(original)))


    [<Test>]
    member _.TrustBindingSerializesConcurrentReadersAndNeverRegresses() =
        withFixture (fun one two first second _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let anchor = (LedgerStore.fetch first |> success).Commit
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let bind () =
                Client.V2ReviewApi.ReviewV2Binding.bindExisting
                    "owner" ctx first.ProjectId first.LedgerId first.Epoch
                    first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl anchor true
            bind () |> function
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Binding failed: %A" error)
            let lockPath = path + ".lock"
            use held = new FileStream(lockPath, FileMode.OpenOrCreate,
                                      FileAccess.ReadWrite, FileShare.None)
            use gate = new System.Threading.ManualResetEventSlim(false)
            let jobs =
                [| for _ in 1 .. 6 ->
                    System.Threading.Tasks.Task.Run(fun () ->
                        gate.Wait()
                        Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx) |]
            gate.Set()
            let newer = ReviewWorkflow.createTask second (Guid.NewGuid()) "newAnchor"
                            "New trusted ledger head" |> success
            let newHead = newer.CurrentHead
            held.Dispose()
            System.Threading.Tasks.Task.WaitAll(jobs |> Array.map (fun job -> job :> System.Threading.Tasks.Task))
            for job in jobs do
                match job.Result with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Concurrent verified load: %A" error)
            let config = System.Text.Json.JsonSerializer.Deserialize<Client.V2ReviewApi.ReviewV2OptIn>(
                File.ReadAllText path)
            Assert.That(config.HighestTrustedHead, Is.EqualTo(newHead))
            Assert.That(File.ReadAllText path, Does.Contain(newHead))
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                let permissions = File.GetUnixFileMode(path)
                Assert.That(permissions.HasFlag(UnixFileMode.OtherRead), Is.False)
                Assert.That(permissions.HasFlag(UnixFileMode.GroupRead), Is.False)
            File.Delete path)

    [<Test>]
    member _.RebindRejectsDifferentHistoryEvenWithCallerSuppliedValidHead() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let trusted = (LedgerStore.fetch first |> success).Commit
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            Client.V2ReviewApi.ReviewV2Binding.bindExisting
                "owner" ctx first.ProjectId first.LedgerId first.Epoch
                first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl trusted true
            |> success |> ignore
            let root = Path.GetDirectoryName one
            let rogue = Path.Combine(root, "rogue.git")
            git root [ "init"; "--bare"; rogue ] |> ignore
            git one [ "remote"; "add"; "rogue"; rogue ] |> ignore
            let separate = Path.Combine(root, "rogue-client")
            git root [ "clone"; first.ApprovedFetchUrl; separate ] |> ignore
            git separate [ "remote"; "add"; "rogue"; rogue ] |> ignore
            let rogueBinding =
                { first with RepositoryPath = separate; RemoteName = "rogue"
                             ApprovedFetchUrl = rogue
                             ApprovedPushUrl = rogue }
            let rogueHead = LedgerStore.initialize rogueBinding true |> success
            let rejected =
                Client.V2ReviewApi.ReviewV2Binding.rebindRemote "owner" ctx
                    "rogue" rogue rogue rogueHead.Commit true
            match rejected with
            | Error(Client.V2LedgerFailureError failure)
                    when failure.Code = "LedgerIntegrityError" || failure.Code = "LedgerHistoryUnavailable" -> ()
            | other -> Assert.Fail(sprintf "Independent rogue root must be rejected: %A" other)
            let config = System.Text.Json.JsonSerializer.Deserialize<Client.V2ReviewApi.ReviewV2OptIn>(
                File.ReadAllText path)
            Assert.That(config.RemoteName, Is.EqualTo(first.RemoteName))
            Assert.That(config.HighestTrustedHead, Is.EqualTo(trusted))
            File.Delete path)


    [<Test>]
    member _.LocalBareRemoteProtectionDiagnosticsAreExplicitAndReadOnly() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let original = (LedgerStore.fetch first |> success).Commit
            let bare = first.ApprovedPushUrl
            let initial = LedgerStore.inspectRemotePolicy first |> success
            Assert.That(initial.RemoteKind, Is.EqualTo("LocalBareGit"))
            Assert.That(initial.Visibility, Is.EqualTo("Unknown"))
            Assert.That(initial.NonFastForwardRejection, Is.EqualTo("NotConfigured"))
            Assert.That(initial.DeletionRejection, Is.EqualTo("NotConfigured"))
            Assert.That(initial.SignedCommitRequirement, Is.EqualTo("Unknown"))
            Assert.That(initial.Warnings.Length, Is.GreaterThan(0))
            git one [ "--git-dir=" + bare; "config"; "receive.denyNonFastForwards"; "true" ] |> ignore
            git one [ "--git-dir=" + bare; "config"; "receive.denyDeletes"; "true" ] |> ignore
            let protectedRemote = LedgerStore.inspectRemotePolicy first |> success
            Assert.That(protectedRemote.NonFastForwardRejection, Is.EqualTo("Enforced"))
            Assert.That(protectedRemote.DeletionRejection, Is.EqualTo("Enforced"))
            Assert.That(protectedRemote.SignedCommitRequirement, Is.EqualTo("Unknown"))
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.EqualTo(original))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))


    [<Test>]
    member _.BindingRejectsEmbeddedUrlCredentialsBeforeNetworkOrPersistence() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let malicious = "https://user:secret@host.example/project.git?token=secret"
            match Client.V2ReviewApi.ReviewV2Binding.bindExisting
                      "owner" ctx first.ProjectId first.LedgerId first.Epoch first.RemoteName
                      malicious malicious (String.replicate 40 "a") true with
            | Error(Client.V2LedgerFailureError failure) ->
                Assert.That(failure.Code, Is.EqualTo("InvalidBinding"))
            | other -> Assert.Fail(sprintf "Embedded credentials must be refused: %A" other)
            Assert.That(File.Exists path, Is.False)
            match LedgerStore.inspectRemotePolicy
                      { first with ApprovedFetchUrl = malicious; ApprovedPushUrl = malicious } with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Remote policy must fail closed for embedded secrets: %A" other))


    [<Test>]
    member _.ChangedPushEndpointAndDeletedLedgerNeverResetTrustedBinding() =
        withFixture (fun one _ first _ _ _ _ _ ->
            let ctx = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let original = (LedgerStore.fetch first |> success).Commit
            let path = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            if File.Exists path then File.Delete path
            Client.V2ReviewApi.ReviewV2Binding.bindExisting
                "owner" ctx first.ProjectId first.LedgerId first.Epoch
                first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl original true
            |> success |> ignore
            git one [ "remote"; "set-url"; "--push"; "origin"; "file:///not-an-approved-push.git" ] |> ignore
            match Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx with
            | Error(Client.V2LedgerFailureError failure) ->
                Assert.That(failure.Code, Is.EqualTo("InvalidBinding"))
            | other -> Assert.Fail(sprintf "Changed push endpoint must reject: %A" other)
            git one [ "remote"; "set-url"; "--push"; "origin"; first.ApprovedPushUrl ] |> ignore
            git one [ "push"; "origin"; ":refs/heads/jarvis-orchestration" ] |> ignore
            try
                match Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx with
                | Error(Client.V2LedgerFailureError failure) ->
                    Assert.That(failure.Code, Is.EqualTo("LedgerIntegrityError"))
                | other -> Assert.Fail(sprintf "Missing ledger must not be recreated: %A" other)
                let config = System.Text.Json.JsonSerializer.Deserialize<Client.V2ReviewApi.ReviewV2OptIn>(
                    File.ReadAllText path)
                Assert.That(config.HighestTrustedHead, Is.EqualTo(original))
            finally
                git one [ "push"; "origin"; $"{original}:refs/heads/jarvis-orchestration" ] |> ignore
            Client.V2ReviewApi.ReviewV2Binding.load "owner" ctx |> function
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Restored trusted ref: %A" error)
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty)
            File.Delete path)

    [<Test>]
    member _.AssignmentAcknowledgmentPinsExactGenerationAndSpec() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "assignmentAck"
            let html = "<h1>Exact pinned instructions</h1>"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
                "Acknowledged task" html |> success |> ignore
            let attemptId = Guid.NewGuid()
            let assigned = ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId
                               attemptId "Owner" "Reviewer" |> success
            let _, _, attempt, issued, prior =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(issued.Generation, Is.EqualTo(1))
            Assert.That(issued.OwnerAgentId, Is.EqualTo("Owner"))
            Assert.That(issued.IssuerId, Is.EqualTo("Orchestrator"))
            Assert.That(issued.SpecBlobOid, Is.EqualTo(attempt.SpecBlobOid))
            Assert.That(issued.IssuedSequence, Is.EqualTo(assigned.Operation.Sequence))
            Assert.That(issued.Instructions, Does.Contain(taskId))
            Assert.That(prior.IsNone, Is.True)
            let ackId = Guid.NewGuid()
            let receipt = ReviewWorkflow.acknowledgeAssignment first ackId taskId "Owner" 1 1
                          |> success
            Assert.That(receipt.AlreadyApplied, Is.False)
            let _, _, acknowledged, historical, evidence =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(acknowledged.Phase, Is.EqualTo("Acknowledged"))
            Assert.That(acknowledged.Version, Is.EqualTo(2))
            Assert.That(historical, Is.EqualTo(issued))
            Assert.That(evidence.Value.OwnerAgentId, Is.EqualTo("Owner"))
            Assert.That(evidence.Value.SpecBlobOid, Is.EqualTo(issued.SpecBlobOid))
            Assert.That(evidence.Value.IssueSequence, Is.EqualTo(issued.IssuedSequence))
            Assert.That(evidence.Value.AcknowledgedSequence, Is.EqualTo(receipt.Operation.Sequence))
            let duplicate = ReviewWorkflow.acknowledgeAssignment second ackId taskId "Owner" 1 1
                            |> success
            Assert.That(duplicate.AlreadyApplied, Is.True)
            expectStale (ReviewWorkflow.beginAttempt second (Guid.NewGuid()) taskId "Owner" 1 1)
            ReviewWorkflow.beginAttempt second (Guid.NewGuid()) taskId "Owner" 2 1
            |> success |> ignore
            let _, _, working, unchanged, ack =
                ReviewWorkflow.getAssignment first taskId 1 |> success
            Assert.That(working.Phase, Is.EqualTo("Working"))
            Assert.That(unchanged, Is.EqualTo(issued))
            Assert.That(ack.Value, Is.EqualTo(evidence.Value))
            Assert.That((LedgerStore.fetch first |> success).Header.NextSequence,
                        Is.GreaterThan(assigned.Operation.Sequence)))

    [<Test>]
    member _.RecoveryIssuesNewImmutableGenerationWithoutOverwritingPriorReceipt() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "generationRecovery"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
                "Recovery" "<h1>Spec frozen</h1>" |> success |> ignore
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
            |> success |> ignore
            let _, _, _, issue1, _ = ReviewWorkflow.getAssignment second taskId 1 |> success
            ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid()) taskId "Owner" 1 1
            |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 2 1
            |> success |> ignore
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 3 3 1
                "Paused at checkpoint" "Operator should resume" |> success |> ignore
            let oldReceipt =
                ReviewWorkflow.getAssignment second taskId 1 |> success
                |> fun (_, _, _, _, receipt) -> receipt.Value
            let recovered =
                ReviewWorkflow.recoverAgentAssignment first (Guid.NewGuid())
                    taskId "Owner" 4 4 1 "Recover stopped session" |> success
            let _, _, attempt, issued2, nextReceipt =
                ReviewWorkflow.getAssignment second taskId 2 |> success
            Assert.That(issued2.Reason, Is.EqualTo("Recovery"))
            Assert.That(issued2.IssuerId, Is.EqualTo("Orchestrator"))
            Assert.That(issued2.Instructions, Is.EqualTo("Recover stopped session"))
            Assert.That(issued2.IssuedSequence, Is.EqualTo(recovered.Operation.Sequence))
            Assert.That(issued2.PreviousGeneration, Is.EqualTo(1))
            Assert.That(issued2.SpecBlobOid, Is.EqualTo(issue1.SpecBlobOid))
            Assert.That(attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(nextReceipt.IsNone, Is.True)
            let _, _, _, oldIssue, oldAck =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(oldIssue, Is.EqualTo(issue1))
            Assert.That(oldAck.Value, Is.EqualTo(oldReceipt))
            expectStale (ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid())
                             taskId "Owner" 5 1)
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 5 1)
            ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid()) taskId "Owner" 5 2
            |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 6 2
            |> success |> ignore
            let _, _, current, immutableIssue2, ack2 =
                ReviewWorkflow.getAssignment second taskId 2 |> success
            Assert.That(current.Phase, Is.EqualTo("Working"))
            Assert.That(immutableIssue2, Is.EqualTo(issued2))
            Assert.That(ack2.Value.Generation, Is.EqualTo(2))
            Assert.That(ack2.Value.SpecBlobOid, Is.EqualTo(issue1.SpecBlobOid)))

    [<Test>]
    member _.ExplicitReissueCanAdoptNewSpecWithoutReplacingOldAssignment() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "specAdoption"
            let initialHtml = "<h1>Original spec</h1>"
            let revisedHtml = "<h1>Revised spec for future generation</h1>"
            ReviewWorkflow.createTaskWithSpec first (Guid.NewGuid()) taskId
                "Task with updated requirements" initialHtml |> success |> ignore
            let attemptId = Guid.NewGuid()
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId attemptId "Owner" "Reviewer"
            |> success |> ignore
            let _, _, _, firstIssue, _ =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid())
                taskId "Owner" 1 1 |> success |> ignore
            let _, _, _, _, firstAcknowledgment =
                ReviewWorkflow.getAssignment second taskId 1 |> success

            let reissueId = Guid.NewGuid()
            let update () =
                ReviewWorkflow.reissueAssignment first reissueId taskId "Owner"
                    2 2 1 "Implement revised acceptance criteria" revisedHtml
            let reissued = update () |> success
            let _, task, attempt, newIssue, newReceipt =
                ReviewWorkflow.getAssignment second taskId 2 |> success
            Assert.That(task.SpecVersion, Is.EqualTo(2))
            Assert.That(task.Version, Is.EqualTo(3))
            Assert.That(attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(attempt.SpecBlobOid, Is.EqualTo(newIssue.SpecBlobOid))
            Assert.That(newIssue.SpecBlobOid, Is.Not.EqualTo(firstIssue.SpecBlobOid))
            Assert.That(newIssue.PreviousGeneration, Is.EqualTo(1))
            Assert.That(newIssue.Reason, Is.EqualTo("ExplicitReissue"))
            Assert.That(newIssue.Instructions, Is.EqualTo("Implement revised acceptance criteria"))
            Assert.That(newIssue.IssuedSequence, Is.EqualTo(reissued.Operation.Sequence))
            Assert.That(newReceipt.IsNone, Is.True)
            let _, _, _, oldIssue, oldReceipt =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(oldIssue, Is.EqualTo(firstIssue))
            Assert.That(oldReceipt.Value, Is.EqualTo(firstAcknowledgment.Value))
            let _, _, _, currentHtml, currentSpecOid =
                ReviewWorkflow.getTaskWithSpec second taskId |> success
            Assert.That(currentHtml, Is.EqualTo(revisedHtml))
            Assert.That(currentSpecOid, Is.EqualTo(newIssue.SpecBlobOid))
            let duplicated = update () |> success
            Assert.That(duplicated.AlreadyApplied, Is.True)
            Assert.That(duplicated.Operation.OriginalResultJson,
                        Is.EqualTo(reissued.Operation.OriginalResultJson))
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 3 1)
            ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid()) taskId "Owner" 3 2
            |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 4 2
            |> success |> ignore
            let before = (LedgerStore.fetch second |> success).Commit
            match ReviewWorkflow.reissueAssignment second (Guid.NewGuid()) taskId "Owner"
                    5 4 2 "Should not interrupt" "<h1>Forbidden</h1>" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Working agent must not be preempted: %A" other)
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(before)))


    [<Test>]
    member _.ReviewChangesReissueGenerationOnlyAfterAgentFinishes() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            let taskId = "reviewReissue"
            initTask taskId
            let _, _, _, oldIssue, _ =
                ReviewWorkflow.getAssignment first taskId 1 |> success
            let head = newCommit "Review branch change" "ready for review"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) taskId "Owner" 3 1
                requestId sourceRef head "First candidate" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) taskId requestId
                "Reviewer" 1 1 |> success |> ignore
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner" 4 3 1
                "Finished original implementation" |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) taskId requestId "Reviewer" 2
                RequestChanges "Address missing checks" |> success |> ignore
            let _, _, attempt, nextIssue, ack =
                ReviewWorkflow.getAssignment first taskId 2 |> success
            Assert.That(attempt.Phase, Is.EqualTo("Queued"))
            Assert.That(attempt.AssignmentGeneration, Is.EqualTo(2))
            Assert.That(nextIssue.Reason, Is.EqualTo("ReviewChanges"))
            Assert.That(nextIssue.IssuerId, Is.EqualTo("Reviewer"))
            Assert.That(nextIssue.Instructions, Is.EqualTo("Address missing checks"))
            Assert.That(nextIssue.SpecBlobOid, Is.EqualTo(oldIssue.SpecBlobOid))
            Assert.That(nextIssue.PreviousGeneration, Is.EqualTo(1))
            Assert.That(ack.IsNone, Is.True)
            let _, _, _, oldStill, _ =
                ReviewWorkflow.getAssignment second taskId 1 |> success
            Assert.That(oldStill, Is.EqualTo(oldIssue))
            let _, inbox = ReviewWorkflow.reviewerQueue second "Reviewer" |> success
            Assert.That(inbox, Is.Empty)
            let ownerState = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(ownerState.Agent.State, Is.EqualTo("Completed"))
            let reactivated = ReviewWorkflow.reissueQueuedAttempt first (Guid.NewGuid())
                                  taskId "Owner" 6 4 2 "Resume requested fixes" |> success
            let _, _, currentAttempt, oldIssueAfter, _ =
                ReviewWorkflow.getAssignment second taskId 2 |> success
            Assert.That(currentAttempt.Version, Is.EqualTo(7))
            Assert.That(oldIssueAfter, Is.EqualTo(nextIssue))
            Assert.That(reactivated.Operation.Type, Is.EqualTo("ReissueQueuedAttempt"))
            expectStale (ReviewWorkflow.beginAttempt first (Guid.NewGuid()) taskId "Owner" 7 1)
            ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid())
                taskId "Owner" 7 2 |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid())
                taskId "Owner" 8 2 |> success |> ignore)

    [<Test>]
    member _.BeginAttemptAtomicallyAcknowledgesQueuedGeneration() =
        withFixture (fun _ _ first second _ _ _ _ ->
            let taskId = "atomicBegin"
            ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Atomic startup"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) taskId (Guid.NewGuid())
                "Owner" "Reviewer" |> success |> ignore
            let before = (LedgerStore.fetch first |> success).Commit
            let beginId = Guid.NewGuid()
            let started =
                ReviewWorkflow.beginAttempt second beginId taskId "Owner" 1 1 |> success
            let _, _, attempt, issue, ack =
                ReviewWorkflow.getAssignment first taskId 1 |> success
            Assert.That(attempt.Phase, Is.EqualTo("Working"))
            Assert.That(ack.IsSome, Is.True)
            Assert.That(ack.Value.AcknowledgedSequence, Is.EqualTo(started.Operation.Sequence))
            Assert.That(ack.Value.IssueSequence, Is.EqualTo(issue.IssuedSequence))
            Assert.That(ack.Value.SpecBlobOid, Is.EqualTo(issue.SpecBlobOid))
            Assert.That((LedgerStore.fetch first |> success).Commit, Is.Not.EqualTo(before))
            let duplicate =
                ReviewWorkflow.beginAttempt first beginId taskId "Owner" 1 1 |> success
            Assert.That(duplicate.AlreadyApplied, Is.True)
            let _, _, _, _, sameReceipt =
                ReviewWorkflow.getAssignment first taskId 1 |> success
            Assert.That(sameReceipt.Value, Is.EqualTo(ack.Value))
            expectStale (ReviewWorkflow.acknowledgeAssignment first (Guid.NewGuid())
                             taskId "Owner" 2 1))

    [<Test>]
    member _.IndependentConcurrentAssignmentsRetainSeparateGenerations() =
        withFixture (fun _ _ first second _ _ _ _ ->
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "Owner2"
            |> success |> ignore
            for task in [ "parallelA"; "parallelB" ] do
                ReviewWorkflow.createTask first (Guid.NewGuid()) task task
                |> success |> ignore
            let firstId = Guid.NewGuid()
            let secondId = Guid.NewGuid()
            use go = new System.Threading.ManualResetEventSlim(false)
            let a = System.Threading.Tasks.Task.Run(fun () ->
                go.Wait()
                ReviewWorkflow.assignTask first firstId "parallelA" (Guid.NewGuid())
                    "Owner" "Reviewer")
            let b = System.Threading.Tasks.Task.Run(fun () ->
                go.Wait()
                ReviewWorkflow.assignTask second secondId "parallelB" (Guid.NewGuid())
                    "Owner2" "Reviewer")
            go.Set()
            let firstResult = a.Result |> success
            let secondResult = b.Result |> success
            Assert.That(firstResult.Operation.OperationId, Is.EqualTo(firstId.ToString("D")))
            Assert.That(secondResult.Operation.OperationId, Is.EqualTo(secondId.ToString("D")))
            for task, owner in [ "parallelA", "Owner"; "parallelB", "Owner2" ] do
                let _, _, attempt, issue, receipt =
                    ReviewWorkflow.getAssignment first task 1 |> success
                Assert.That(attempt.OwnerAgentId, Is.EqualTo(owner))
                Assert.That(issue.OwnerAgentId, Is.EqualTo(owner))
                Assert.That(issue.SpecBlobOid, Is.EqualTo(attempt.SpecBlobOid))
                Assert.That(receipt.IsNone, Is.True))

    [<Test>]
    member _.WorkingCheckpointAndReviewRevisionsPreserveSeparateEvidence() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            initTask "evidence"
            let c = newCommit "Reported code" "v1"
            let checkpointOp = Guid.NewGuid()
            let checkpoint () =
                ReviewWorkflow.checkpointAttempt first checkpointOp "evidence" "Owner"
                    3 3 1 "Implemented first slice" "Continue review work"
                    c "dotnet test: 12 passed (owner report)"
            let progress = checkpoint () |> success
            Assert.That(progress.AlreadyApplied, Is.False)
            Assert.That((checkpoint () |> success).AlreadyApplied, Is.True)
            let checkpointSeq = progress.Operation.Sequence
            let snapshot, saved = ReviewWorkflow.getCheckpoint second "evidence" checkpointSeq |> success
            Assert.That(saved.Kind, Is.EqualTo("Progress"))
            Assert.That(saved.EvidenceStatus, Is.EqualTo("OwnerReported"))
            Assert.That(saved.SourceCommit, Is.EqualTo(c))
            Assert.That(saved.AssignmentGeneration, Is.EqualTo(1))
            Assert.That(saved.Sequence, Is.EqualTo(checkpointSeq))
            Assert.That(snapshot, Is.Not.Empty)
            let inbox = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Working"))
            Assert.That(inbox.Working.IsSome, Is.True)
            Assert.That(inbox.Working.Value.Attempt.Version, Is.EqualTo(4))
            expectStale (ReviewWorkflow.checkpointAttempt second (Guid.NewGuid())
                            "evidence" "Owner" 3 3 1 "Old" "Old" c "")

            let requestId = Guid.NewGuid()
            let submitId = Guid.NewGuid()
            let submit () =
                ReviewWorkflow.submitReviewWithEvidence first submitId "evidence" "Owner" 4 1
                    requestId sourceRef c "Review v1" "12 unit tests passed (owner report)"
            let submitted = submit () |> success
            Assert.That((submit () |> success).AlreadyApplied, Is.True)
            let _, request, revision1 = ReviewWorkflow.getReview second "evidence" requestId |> success
            Assert.That(request.Status, Is.EqualTo("Unclaimed"))
            Assert.That(revision1.Revision, Is.EqualTo(1))
            Assert.That(revision1.SpecBlobOid.Length, Is.EqualTo(40))
            let pinnedSource = LedgerStore.sourceRepositoryIdentity first |> success
            Assert.That(revision1.SourceRepositoryId, Is.EqualTo(pinnedSource.RepositoryId))
            Assert.That(revision1.SourcePushRemote, Is.EqualTo(pinnedSource.PushUrl))
            Assert.That(revision1.SourceRemote, Is.EqualTo(first.ApprovedFetchUrl))
            Assert.That(revision1.PublishedSourceOid, Is.EqualTo(c))
            Assert.That(revision1.EvidenceStatus, Is.EqualTo("OwnerReported"))
            Assert.That(revision1.TestEvidence, Does.Contain("12 unit tests"))
            let _, final1 = ReviewWorkflow.getCheckpoint second "evidence" revision1.CheckpointSequence |> success
            Assert.That(final1.Kind, Is.EqualTo("Submission"))
            Assert.That(final1.SourceCommit, Is.EqualTo(c))
            Assert.That(final1.EvidenceStatus, Is.EqualTo("OwnerReported"))
            Assert.That(final1.Sequence, Is.Not.EqualTo(checkpointSeq))

            let d = newCommit "Revision v2" "v2"
            let replacement = ReviewWorkflow.updateReviewWithEvidence first (Guid.NewGuid())
                                  "evidence" "Owner" 1 requestId 1 sourceRef d "Review v2" ""
                              |> success
            let _, request2, revision2 = ReviewWorkflow.getReview second "evidence" requestId |> success
            Assert.That(request2.CurrentRevision, Is.EqualTo(2))
            Assert.That(revision2.Revision, Is.EqualTo(2))
            Assert.That(revision2.HeadCommit, Is.EqualTo(d))
            Assert.That(revision2.PublishedSourceOid, Is.EqualTo(d))
            Assert.That(revision2.EvidenceStatus, Is.EqualTo("NotProvided"))
            Assert.That(revision2.TestEvidence, Is.Empty)
            Assert.That(revision2.CheckpointSequence, Is.EqualTo(replacement.Operation.Sequence))
            let _, final2 = ReviewWorkflow.getCheckpoint second "evidence" revision2.CheckpointSequence |> success
            Assert.That(final2.Kind, Is.EqualTo("Revision"))
            Assert.That(final2.EvidenceStatus, Is.EqualTo("NotProvided"))
            Assert.That(final2.SourceCommit, Is.EqualTo(d))
            Assert.That(final2.Sequence, Is.Not.EqualTo(final1.Sequence))
            let _, historic = ReviewWorkflow.getCheckpoint second "evidence" revision1.CheckpointSequence |> success
            Assert.That(historic.SourceCommit, Is.EqualTo(c))
            Assert.That((ReviewWorkflow.getAgentInbox second "Owner" |> success).Agent.State,
                        Is.EqualTo("Working"))
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "evidence" requestId "Reviewer" 2 2
            |> success |> ignore
            match ReviewWorkflow.updateReviewWithEvidence first (Guid.NewGuid()) "evidence"
                      "Owner" 1 requestId 3 sourceRef d "Cannot replace" "not tested" with
            | Error(ReviewAlreadyClaimed _) -> ()
            | other -> Assert.Fail(sprintf "Frozen revision replaced: %A" other)
            Assert.That(git one [ "status"; "--porcelain" ], Is.Empty))

    [<Test>]
    member _.ProgressCheckpointRejectsStaleGenerationAndUnverifiedSource() =
        withFixture (fun _ _ first _ _ _ _ initTask ->
            initTask "progressguard"
            match ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) "progressguard" "Owner"
                      3 3 2 "wrong generation" "continue" "" "" with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Wrong generation accepted: %A" other)
            match ReviewWorkflow.checkpointAttempt first (Guid.NewGuid()) "progressguard" "Owner"
                      3 3 1 "source not present" "continue" (String.replicate 40 "e") "" with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Unverified source SHA accepted")
            let _, _, attempt = ReviewWorkflow.getTask first "progressguard" |> success
            Assert.That(attempt.Value.Version, Is.EqualTo(3)))

    [<Test>]
    member _.ReviewerEvidenceIsPinnedToClaimedRevisionAndNotInferredFromAcceptance() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "reviewevidence"
            let head = newCommit "Review me" "reviewed"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitReviewWithEvidence first (Guid.NewGuid()) "reviewevidence"
                "Owner" 3 1 requestId sourceRef head "Please review"
                "owner reports test run" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "reviewevidence"
                requestId "Reviewer" 1 1 |> success |> ignore
            match ReviewWorkflow.decideWithEvidence second (Guid.NewGuid()) "reviewevidence"
                    requestId "Reviewer" 2 Accept "Accepted" true true "" with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Tested requires explicit evidence: %A" other)
            let result = ReviewWorkflow.decideWithEvidence second (Guid.NewGuid())
                             "reviewevidence" requestId "Reviewer" 2 Accept "Accepted"
                             true true "dotnet test: 122 passed (reviewer report)" |> success
            let _, evidence = ReviewWorkflow.getDecisionEvidence first "reviewevidence" requestId |> success
            Assert.That(evidence.Sequence, Is.EqualTo(result.Operation.Sequence))
            Assert.That(evidence.Status, Is.EqualTo("Accepted"))
            Assert.That(evidence.Revision, Is.EqualTo(1))
            Assert.That(evidence.Inspected, Is.True)
            Assert.That(evidence.Tested, Is.True)
            Assert.That(evidence.ReviewedHead, Is.EqualTo(head))
            Assert.That(evidence.ReviewRef, Does.EndWith("/r1"))
            Assert.That(evidence.SpecBlobOid.Length, Is.EqualTo(40))
            Assert.That(evidence.SourceRemote, Is.EqualTo(first.ApprovedFetchUrl))
            let _, _, revision = ReviewWorkflow.getReview second "reviewevidence" requestId |> success
            Assert.That(revision.EvidenceStatus, Is.EqualTo("OwnerReported"))
            Assert.That(revision.Revision, Is.EqualTo(evidence.Revision))
            let _, _, attempt = ReviewWorkflow.getTask first "reviewevidence" |> success
            Assert.That(attempt.Value.Phase, Is.EqualTo("Working"))

            // A separate accepted decision never claims testing merely because
            // the reviewer accepted the code.
            ReviewWorkflow.createTask first (Guid.NewGuid()) "acceptednoevidence" "Accepted no test claims"
            |> success |> ignore
            ReviewWorkflow.assignTask first (Guid.NewGuid()) "acceptednoevidence"
                (Guid.NewGuid()) "Reviewer" "Owner" |> success |> ignore
            ReviewWorkflow.beginAttempt first (Guid.NewGuid()) "acceptednoevidence" "Reviewer" 1 1
            |> success |> ignore
            let baseOid = git one [ "rev-parse"; "HEAD~1" ]
            ReviewWorkflow.bindReviewBase first (Guid.NewGuid()) "acceptednoevidence"
                "Reviewer" 2 1 baseOid sourceRef |> success |> ignore
            let otherId = Guid.NewGuid()
            ReviewWorkflow.submitReview first (Guid.NewGuid()) "acceptednoevidence" "Reviewer" 3 1
                otherId sourceRef head "Review without supplied tests" |> success |> ignore
            ReviewWorkflow.claimReview second (Guid.NewGuid()) "acceptednoevidence" otherId "Owner" 1 1
            |> success |> ignore
            ReviewWorkflow.decide second (Guid.NewGuid()) "acceptednoevidence" otherId "Owner"
                2 Accept "Accepted" |> success |> ignore
            let _, plain = ReviewWorkflow.getDecisionEvidence second "acceptednoevidence" otherId |> success
            Assert.That(plain.Status, Is.EqualTo("Accepted"))
            Assert.That(plain.Inspected, Is.False)
            Assert.That(plain.Tested, Is.False)
            Assert.That(plain.TestDetails, Is.Null)
            Assert.That(plain.Revision, Is.EqualTo(1)))

    [<Test>]
    member _.LostImmutableReviewRefPreventsClaimAndFalseVerification() =
        withFixture (fun one _ first second _ sourceRef newCommit initTask ->
            initTask "missingref"
            let head = newCommit "Source commit" "missing review ref"
            let req = Guid.NewGuid()
            ReviewWorkflow.submitReviewWithEvidence first (Guid.NewGuid()) "missingref" "Owner"
                3 1 req sourceRef head "Review C" "" |> success |> ignore
            let reviewRef = $"refs/heads/jarvis-review/missingref/{req:N}/r1"
            git one [ "push"; "origin"; ":" + reviewRef ] |> ignore
            match ReviewWorkflow.claimReview second (Guid.NewGuid()) "missingref" req "Reviewer" 1 1 with
            | Error _ -> ()
            | Ok _ -> Assert.Fail("Missing immutable remote review ref must fail closed.")
            let _, request, revision = ReviewWorkflow.getReview first "missingref" req |> success
            Assert.That(request.Status, Is.EqualTo("Unclaimed"))
            Assert.That(revision.EvidenceStatus, Is.EqualTo("NotProvided"))
            Assert.That(revision.PublishedSourceOid, Is.EqualTo(head)))
