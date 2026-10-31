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
    member _.BranchReviewsIntegrateIndependentlyThroughOrdinaryMergeCommits() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            let taskId = "branch-merge"
            initTask taskId
            git one [ "switch"; "-c"; "feature/branch-merge" ] |> ignore
            let featureRef = "refs/heads/feature/branch-merge"
            let firstHead = newCommit "First review slice" "slice A"
            let a = Guid.NewGuid()
            ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId "Owner" 3 1
                a featureRef firstHead "# Review A" |> success |> ignore
            ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId a "Reviewer" 1
            |> success |> ignore
            ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId a "Reviewer" 2 Accept
            |> success |> ignore
            let aReview = ReviewWorkflow.getBranchReview first taskId a |> success
            Assert.That(aReview.Integrated, Is.False)
            git one [ "switch"; "main" ] |> ignore
            git one [ "fetch"; "origin"; aReview.Review.ReviewRef ] |> ignore
            git one [ "merge"; "--no-ff"; "-m"; "Integrate reviewed slice A";
                      "FETCH_HEAD" ] |> ignore
            git one [ "push"; "origin"; "main" ] |> ignore
            Assert.That((ReviewWorkflow.getBranchReview second taskId a |> success).Integrated, Is.True)
            git one [ "switch"; "feature/branch-merge" ] |> ignore
            git one [ "merge"; "main" ] |> ignore
            let secondHead = newCommit "Second review slice" "slice B"
            let b = Guid.NewGuid()
            ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId "Owner" 3 1
                b featureRef secondHead "# Review B" |> success |> ignore
            let bReview = ReviewWorkflow.getBranchReview second taskId b |> success
            Assert.That(bReview.Review.BaseCommit, Is.EqualTo(git one [ "rev-parse"; "main" ]))
            ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId b "Reviewer" 1
            |> success |> ignore
            ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId b "Reviewer" 2 Accept
            |> success |> ignore
            let pending = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
            Assert.That(pending.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(pending.AcceptedPendingIntegration.Head.Review.RequestId,
                        Is.EqualTo(b.ToString("N")))
            git one [ "switch"; "main" ] |> ignore
            git one [ "fetch"; "origin"; bReview.Review.ReviewRef ] |> ignore
            git one [ "merge"; "--no-ff"; "-m"; "Integrate reviewed slice B";
                      "FETCH_HEAD" ] |> ignore
            git one [ "push"; "origin"; "main" ] |> ignore
            Assert.That((ReviewWorkflow.getBranchReview first taskId b |> success).Integrated, Is.True)
            let empty = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
            Assert.That(empty.AcceptedPendingIntegration, Is.Empty))

    [<Test>]
    member _.CanonicalBranchReviewUsesOneRecordAndGitDerivedClosure() =
        withFixture (fun one _ first second baseOid sourceRef newCommit initTask ->
            let taskId = "simple-review"
            initTask taskId
            let firstHead = newCommit "First submitted patch" "first slice"
            let reviewId = Guid.NewGuid()
            let sent = ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId
                            "Owner" 3 1 reviewId sourceRef firstHead "# Scope\nFirst slice"
                       |> success
            Assert.That(sent.AlreadyApplied, Is.False)
            let details = ReviewWorkflow.getBranchReview second taskId reviewId |> success
            Assert.That(details.Review.BaseCommit, Is.EqualTo(baseOid))
            Assert.That(details.Review.ReviewRef, Does.Contain(reviewId.ToString("N")))
            Assert.That(details.Review.Status, Is.EqualTo("Unclaimed"))
            Assert.That(details.Integrated, Is.False)
            let _, files = ReviewWorkflow.listReviewDocuments second taskId reviewId |> success
            Assert.That(files |> List.map _.Path, Does.Contain("summary.md"))
            let replacement = newCommit "Updated submitted patch" "second slice"
            ReviewWorkflow.replaceBranchReview first (Guid.NewGuid()) taskId "Owner"
                reviewId 1 1 sourceRef replacement "# Scope\nReplacement"
            |> success |> ignore
            let current = ReviewWorkflow.getBranchReview second taskId reviewId |> success
            Assert.That(current.Review.Version, Is.EqualTo(2))
            Assert.That(current.HeadCommit, Is.EqualTo(replacement))
            ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId reviewId "Reviewer" 2
            |> success |> ignore
            match ReviewWorkflow.replaceBranchReview first (Guid.NewGuid()) taskId "Owner"
                    reviewId 3 1 sourceRef replacement "Too late" with
            | Error(ReviewAlreadyClaimed _) -> ()
            | other -> Assert.Fail(sprintf "Claimed branch must not be replaced: %A" other)
            ReviewWorkflow.putReviewDocument second (Guid.NewGuid()) taskId reviewId "Reviewer"
                "findings/test-coverage.md" "" "# Test coverage\nReviewed."
            |> success |> ignore
            ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId reviewId "Reviewer" 3 Accept
            |> success |> ignore
            let acceptedInbox = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
            Assert.That(acceptedInbox.AcceptedPendingIntegration.Length, Is.EqualTo(1))
            Assert.That(acceptedInbox.UnacknowledgedFeedback.Length, Is.EqualTo(1))
            let notice = acceptedInbox.UnacknowledgedFeedback.Head
            ReviewWorkflow.acknowledgeBranchReview first (Guid.NewGuid()) taskId "Owner"
                notice.DecisionSequence |> success |> ignore
            let acknowledged = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
            Assert.That(acknowledged.UnacknowledgedFeedback.Length, Is.EqualTo(0))
            let _, task, attempt = ReviewWorkflow.getTask first taskId |> success
            let agent = acknowledged.Agent
            ReviewWorkflow.completeAgentWork first (Guid.NewGuid()) taskId "Owner"
                attempt.Value.Version agent.Version attempt.Value.AssignmentGeneration "Done"
            |> success |> ignore
            let _, _, finished = ReviewWorkflow.getTask first taskId |> success
            ReviewWorkflow.finalizeBranchReview first (Guid.NewGuid()) taskId "Owner"
                finished.Value.Version reviewId |> success |> ignore
            let _, beforeMerge, _ = ReviewWorkflow.getTask first taskId |> success
            Assert.That(beforeMerge.Lifecycle, Is.EqualTo("Implemented"))
            match ReviewWorkflow.closeBranchTask first (Guid.NewGuid()) taskId beforeMerge.Version with
            | Error(StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Cannot close unmerged patch: %A" other)
            git one [ "push"; "origin"; "HEAD:" + sourceRef ] |> ignore
            let integrated = ReviewWorkflow.getBranchReview second taskId reviewId |> success
            Assert.That(integrated.Integrated, Is.True)
            let finalInbox = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
            Assert.That(finalInbox.AcceptedPendingIntegration.Length, Is.EqualTo(0))
            ReviewWorkflow.closeBranchTask first (Guid.NewGuid()) taskId beforeMerge.Version
            |> success |> ignore
            let _, closed, _ = ReviewWorkflow.getTask first taskId |> success
            Assert.That(closed.Lifecycle, Is.EqualTo("Closed"))
            let snapshot = LedgerStore.fetch first |> success
            let paths = LedgerStore.listAt first snapshot "tasks" |> success
            Assert.That(paths |> List.filter (fun path ->
                path.StartsWith($"tasks/{taskId}/reviews/") && path.EndsWith(".json")),
                Is.EquivalentTo([ $"tasks/{taskId}/reviews/{reviewId:N}/review.json" ])))

    [<Test>]
    member _.LegacyReviewMigrationCreatesCanonicalRecordWithoutLosingFindings() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            let taskId = "migrate-review"
            initTask taskId
            let head = newCommit "Legacy patch" "legacy"
            let id = Guid.NewGuid()
            // Construct old-format ledger data directly, without retaining
            // the retired review implementation in the runtime.
            let refName = $"refs/heads/jarvis-review/{taskId}/{id:N}/r1"
            LedgerStore.publishReviewRef first refName head |> success |> ignore
            let _, _, attemptOption = ReviewWorkflow.getTask first taskId |> success
            let attempt = attemptOption.Value
            let review : ReviewRequest =
                { RequestId = id.ToString("N"); TaskId = taskId
                  AttemptId = attempt.AttemptId; OwnerAgentId = "Owner"
                  ReviewerId = "Reviewer"; Version = 3; CurrentRevision = 1
                  Status = "Accepted"; ClaimantId = "Reviewer"; ClaimedRevision = 1
                  CreatedSequence = 30L; DecisionSequence = 35L; Feedback = "Approved" }
            let revision : ReviewRevision =
                { RequestId = id.ToString("N"); Revision = 1; TaskId = taskId
                  AttemptId = attempt.AttemptId; BaseCommit = git first.RepositoryPath [ "rev-list"; "--max-parents=0"; "HEAD" ]
                  HeadCommit = head; SourceRef = sourceRef; TargetRef = sourceRef
                  ReviewRef = refName; Summary = "Legacy review"; CreatedSequence = 30L
                  SpecBlobOid = attempt.SpecBlobOid; SourceRepositoryId = attempt.SourceRepositoryId
                  SourceRemote = attempt.SourceRemote; SourcePushRemote = attempt.SourcePushRemote
                  CheckpointSequence = 0L; TestEvidence = ""; EvidenceStatus = "NotProvided" }
            let serialize value =
                let options = System.Text.Json.JsonSerializerOptions(
                                  PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase)
                let json = System.Text.Json.JsonSerializer.Serialize(value, options)
                "{\"schemaVersion\":2," + json.Substring(1)
            let changes : LedgerFileChange list =
                [ { Path = $"tasks/{taskId}/reviews/{id:N}/request.json"
                    ExpectedBlobOid = None; Content = serialize review }
                  { Path = $"tasks/{taskId}/reviews/{id:N}/revisions/1.json"
                    ExpectedBlobOid = None; Content = serialize revision }
                  { Path = $"tasks/{taskId}/reviews/{id:N}/findings/old-finding.md"
                    ExpectedBlobOid = None; Content = "# Existing finding\\nKeep this." } ]
            let intent : LedgerIntent =
                { OperationId = Guid.NewGuid(); Actor = "Orchestrator"
                  Kind = "MigrationFixture"; RequestJson = "{\"fixture\":true}" }
            LedgerStore.transact first intent (fun _ -> Ok changes) |> success |> ignore
            let historical = LedgerStore.fetch first |> success
            let migrationId = Guid.NewGuid()
            let migrated = ReviewWorkflow.migrateBranchReviews first migrationId |> success
            Assert.That(migrated.AlreadyApplied, Is.False)
            Assert.That((ReviewWorkflow.migrateBranchReviews first migrationId |> success).AlreadyApplied, Is.True)
            let result = ReviewWorkflow.getBranchReview second taskId id |> success
            Assert.That(result.Review.Status, Is.EqualTo("Accepted"))
            Assert.That(result.HeadCommit, Is.EqualTo(head))
            let paths = LedgerStore.listAt first (LedgerStore.fetch first |> success) "tasks" |> success
            let reviewJsonFiles =
                paths |> List.filter (fun path ->
                    path.StartsWith($"tasks/{taskId}/reviews/", StringComparison.Ordinal)
                    && path.EndsWith(".json", StringComparison.Ordinal))
            Assert.That(reviewJsonFiles,
                        Is.EquivalentTo([ $"tasks/{taskId}/reviews/{id:N}/review.json" ]))
            let historicalLegacyPath = $"tasks/{taskId}/reviews/{id:N}/request.json"
            let historicBody, _ = LedgerStore.readAt first historical historicalLegacyPath |> success
            Assert.That(historicBody.IsSome, Is.True, "Old files must remain in Git history.")
            let currentBody, _ =
                LedgerStore.readAt first (LedgerStore.fetch first |> success) historicalLegacyPath |> success
            Assert.That(currentBody.IsNone, Is.True, "Old files must not remain authoritative.")
            let _, docs = ReviewWorkflow.listReviewDocuments second taskId id |> success
            Assert.That(docs |> List.map _.Path, Does.Contain("findings/old-finding.md"))
            let _, finding = ReviewWorkflow.getReviewDocument second taskId id "findings/old-finding.md"
                             |> success
            Assert.That(finding.Content, Does.Contain("Keep this.")))

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
            // Simulate Git accepting a recovery but the client losing the reply
            // before its matching pending journal entry can be retired.
            let recoveryCommand = resume "session-B" 3
            let recoveryId = Guid.Parse recoveryCommand.OperationId
            Client.V2ReviewApi.V2PendingIntentJournal.persist first recoveryId "V2TaskWrite"
                (System.Text.Json.JsonSerializer.Serialize recoveryCommand) |> success
            ReviewWorkflow.resumeAgentWithSession first recoveryId taskId "Owner" 2 3 1 "session-B"
            |> success |> ignore
            let afterRecovery = LedgerStore.fetch second |> success
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(1))
            let replay = api recoveryCommand
            use replayJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(replay))
            Assert.That(replayJson.RootElement.GetProperty("alreadyApplied").GetBoolean(), Is.True)
            Assert.That(replayJson.RootElement.GetProperty("originalResult").GetProperty("kind").GetString(),
                        Is.EqualTo("ResumeAgent"))
            Assert.That(replayJson.RootElement.GetProperty("operation").GetProperty("OperationId").GetString(),
                        Is.EqualTo(recoveryCommand.OperationId))
            Assert.That((Client.V2ReviewApi.V2PendingIntentJournal.listAtRoot one |> success).Length,
                        Is.EqualTo(0), "A confirmed replay must retire its matching pending local intent.")
            Assert.That((LedgerStore.fetch second |> success).Commit, Is.EqualTo(afterRecovery.Commit),
                        "Replaying an accepted recovery must not create another Git commit.")
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
    member _.OwnerTransferRevokesOldAssignmentAndPreservesFrozenReviewEvidence() =
        withFixture (fun _ _ first second _ sourceRef newCommit initTask ->
            initTask "admin-transfer"
            ReviewWorkflow.registerAgent first (Guid.NewGuid()) "NewOwner" |> success |> ignore
            let head = newCommit "Transfer review" "prior-work"
            let requestId = Guid.NewGuid()
            ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) "admin-transfer" "Owner"
                3 1 requestId sourceRef head "Work before handoff" |> success |> ignore
            ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) "admin-transfer" requestId "Reviewer"
                1 |> success |> ignore
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
            Assert.That(afterAttempt.Value.SpecBlobOid, Is.EqualTo(oldAttempt.SpecBlobOid))
            Assert.That(afterAttempt.Value.SourceRef, Is.Null)
            Assert.That(afterAttempt.Value.BaseCommit, Is.Null)
            Assert.That((ReviewWorkflow.getAgentInbox first "Owner" |> success).Agent.State,
                        Is.EqualTo("Completed"))
            let newOwner = ReviewWorkflow.getAgentInbox second "NewOwner" |> success
            Assert.That(newOwner.Agent.State, Is.EqualTo("Assigned"))
            Assert.That(newOwner.Agent.FocusAttemptId, Is.EqualTo(transferredAttemptId.ToString("N")))
            let withdrawn = ReviewWorkflow.getBranchReview first "admin-transfer" requestId |> success
            Assert.That(withdrawn.Review.Status, Is.EqualTo("Withdrawn"))
            Assert.That(withdrawn.HeadCommit, Is.EqualTo(head))
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
                      RequestId = ""; ReviewerId = ""; DocumentPath = "" }
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
                      Verdict = ""; Feedback = ""; TestEvidence = ""; Inspected = false; Tested = false; TestDetails = ""; DocumentPath = ""; Content = ""; ExpectedBlobOid = "" }
                match Client.V2ReviewApi.V2ReviewApi.write reviewCmd ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "V2 submit API failed: %A" error)
                // Exercise the canonical branch review via the same typed API.
                let claim = { reviewCmd with Action = "Claim"; ActorId = "Reviewer"
                                             ExpectedVersion = 1
                                             OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write claim ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Branch claim failed: %A" error)
                let findingContent = "# Needs regression test\nPlease add coverage for this edge case."
                let documentWrite =
                    { reviewCmd with Action = "PutDocument"; ActorId = "Reviewer"
                                     DocumentPath = "findings/missing-regression-test.md"
                                     Content = findingContent; ExpectedBlobOid = ""
                                     OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write documentWrite ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Review document write failed: %A" error)
                let documentRead =
                    { getTask with Action = "ReadDocument"
                                   RequestId = requestId.ToString("D")
                                   DocumentPath = "findings/missing-regression-test.md" }
                match Client.V2ReviewApi.V2ReviewApi.read documentRead ctx with
                | Ok value ->
                    let resultJson = System.Text.Json.JsonSerializer.Serialize(value)
                    Assert.That(resultJson, Does.Contain("Please add coverage for this edge case"))
                | Error error -> Assert.Fail(sprintf "Review document read failed: %A" error)
                let decided = { claim with Action = "Decide"; Verdict = "Accept";
                                          ExpectedVersion = 2;
                                          OperationId = Guid.NewGuid().ToString("D") }
                match Client.V2ReviewApi.V2ReviewApi.write decided ctx with
                | Ok _ -> ()
                | Error error -> Assert.Fail(sprintf "Branch decision failed: %A" error)
                let inspected = ReviewWorkflow.getBranchReview first taskId requestId |> success
                Assert.That(inspected.Review.Status, Is.EqualTo("Accepted"))
                Assert.That(inspected.Integrated, Is.False)
                let ownerInbox = ReviewWorkflow.getBranchAgentInbox first "Owner" |> success
                Assert.That(ownerInbox.AcceptedPendingIntegration.Length, Is.EqualTo(1))
                let after =
                    Client.V2ReviewApi.V2TaskApi.read
                        { ProjectName = "owner"; AgentId = "Owner"; Action = "AgentInbox"; TaskId = ""; AssignmentGeneration = 0; CheckpointSequence = 0L; Cursor = ""; PageSize = 0; BeforeOid = null; AfterOid = null; RelativePath = null } ctx
                    |> function
                        | Ok value -> unbox<AgentInbox> value
                        | Error error -> failwithf "V2 inbox failed: %A" error
                Assert.That(after.SlotAvailable, Is.False)
                Assert.That(after.Agent.State, Is.EqualTo("Working"))
                run { template "CompleteAgentWork" taskId with
                          ExpectedVersion = 5; ExpectedAgentVersion = 5
                          AssignmentGeneration = 1; Summary = "Finished the implementation" } |> ignore
                let finished = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(finished.Agent.State, Is.EqualTo("Completed"))
                run { template "ParkAgent" taskId with
                          ExpectedAgentVersion = 6; Reason = "No other work" } |> ignore
                let parked = ReviewWorkflow.getAgentInbox first "Owner" |> success
                Assert.That(parked.Agent.State, Is.EqualTo("Parked"))
                Assert.That(parked.Focused.IsNone, Is.True)
                Assert.That(parked.PendingReviews.Length, Is.EqualTo(0))
                Assert.That(after.PendingReviews.Length, Is.EqualTo(0))
                Assert.That(after.AcceptedPendingIntegration.Length, Is.EqualTo(1))
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
                Assert.That(after.AcceptedPendingIntegration.Head.Review.RequestId, Is.EqualTo(requestId.ToString("N")))
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
    member _.ReadyAgentCanChangeSourceBranchWithoutResuming() =
        withFixture (fun one _ first second baseOid _ _ initTask ->
            initTask "ready-source"
            let path = Path.Combine(Path.GetDirectoryName(one), "ready-source-worktree")
            git one [ "branch"; "feature/ready-source"; baseOid ] |> ignore
            git one [ "worktree"; "add"; path; "feature/ready-source" ] |> ignore
            let selected = { first with RepositoryPath = path }
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) "ready-source" "Owner" 3 3 1
                "Review the current slice" "Continue on the prepared branch"
            |> success |> ignore
            ReviewWorkflow.rebindAttemptSource selected (Guid.NewGuid()) "ready-source"
                "Owner" 4 1 "refs/heads/feature/ready-source"
            |> success |> ignore
            let inbox = ReviewWorkflow.getAgentInbox second "Owner" |> success
            Assert.That(inbox.Agent.State, Is.EqualTo("Ready"))
            Assert.That(inbox.Agent.ActiveAttemptId, Is.Null)
            Assert.That(inbox.Focused.Value.Attempt.Phase, Is.EqualTo("Paused"))
            Assert.That(inbox.Focused.Value.Attempt.SourceRef,
                        Is.EqualTo("refs/heads/feature/ready-source"))
            git one [ "worktree"; "remove"; path ] |> ignore)

    [<Test>]
    member _.RebindingSourceContextIgnoresUnrelatedDirtyCheckout() =
        withFixture (fun one _ first second baseOid _ _ initTask ->
            let taskId = "rebind-dirty-unrelated"
            initTask taskId
            git one [ "branch"; "feature/context"; baseOid ] |> ignore
            File.WriteAllText(Path.Combine(one, "source.txt"), "uncommitted unrelated changes")
            ReviewWorkflow.rebindAttemptSource first (Guid.NewGuid()) taskId "Owner" 3 1
                "refs/heads/feature/context" |> success |> ignore
            let _, _, attempt = ReviewWorkflow.getTask second taskId |> success
            Assert.That(attempt.Value.SourceRef, Is.EqualTo("refs/heads/feature/context"))
            Assert.That(git one [ "status"; "--porcelain" ], Is.Not.Empty))

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
    member _.AgentsDashboardReadsCanonicalReadyInsteadOfSourceBranchTaskFiles() =
        withFixture (fun one _ first _ _ _ _ initTask ->
            let taskId = "320"
            initTask taskId
            ReviewWorkflow.yieldAgent first (Guid.NewGuid()) taskId "Owner" 3 3 1
                "Slice committed" "Review four commits" |> success |> ignore
            let canonical = ReviewWorkflow.getAgentInbox first "Owner" |> success
            Assert.That(canonical.Agent.State, Is.EqualTo("Ready"))
            let context = GitCommandTests.TestContext(Path.GetDirectoryName one)
            let configPath = Client.V2ReviewApi.ReviewV2Binding.pathForRoot one
            try
                let head = (LedgerStore.fetch first |> success).Commit
                Client.V2ReviewApi.ReviewV2Binding.bindExisting
                    "owner" context first.ProjectId first.LedgerId first.Epoch
                    first.RemoteName first.ApprovedFetchUrl first.ApprovedPushUrl head true
                |> function
                   | Ok _ -> ()
                   | Error error -> failwithf "Cannot bind canonical test project: %A" error
                let dashboard =
                    match Client.AgentDashboard.load context with
                    | Ok snapshot -> snapshot
                    | Error error -> failwithf "Cannot read canonical dashboard: %s" error
                let owner = Client.AgentView.rows dashboard
                            |> List.find (fun item -> item.Project = "owner" && item.Agent = "Owner")
                Assert.That(owner.State, Is.EqualTo("Ready"))
                Assert.That(owner.Focus.Value.TaskId, Is.EqualTo(taskId))
                Assert.That(owner.Focus.Value.Phase, Is.EqualTo("Paused"))
                Assert.That(owner.StateEnteredAt.Value, Is.EqualTo(DateTimeOffset.Parse(canonical.Agent.StateEnteredAtUtc)))
                Assert.That(Client.AgentView.agentSummary owner, Is.EqualTo("#320 - Ready"))
            finally
                if File.Exists configPath then File.Delete configPath)

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
            ReviewWorkflow.submitBranchReview first operationId "submission" "Owner"
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
