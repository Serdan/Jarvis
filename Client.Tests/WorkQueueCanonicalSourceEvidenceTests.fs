module Client.Tests.WorkQueueCanonicalSourceEvidenceTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.RoleInstructions
open Client.WorkQueueCanonicalSourceEvidence

[<TestFixture>]
type WorkQueueCanonicalSourceEvidenceTests() =
    let git directory args =
        use runner = new Process()
        runner.StartInfo.FileName <- "git"
        runner.StartInfo.WorkingDirectory <- directory
        runner.StartInfo.RedirectStandardOutput <- true
        runner.StartInfo.RedirectStandardError <- true
        args |> List.iter runner.StartInfo.ArgumentList.Add
        if not (runner.Start()) then failwith "Git fixture did not start."
        let output = runner.StandardOutput.ReadToEnd()
        let error = runner.StandardError.ReadToEnd()
        if not (runner.WaitForExit(20000)) then
            runner.Kill(true)
            failwith "Git fixture timed out."
        if runner.ExitCode <> 0 then failwithf "Git fixture failed: %s" error
        output.Trim()

    let ok = function
        | Ok item -> item
        | Error error -> failwithf "Unexpected canonical ledger error: %A" error

    let blocked = function
        | Ok item -> Assert.Fail(sprintf "Untrusted source evidence was accepted: %A" item)
        | Error _ -> ()

    let anchor (binding: LedgerBinding) (write: LedgerWrite) =
        let snapshot = LedgerStore.fetch binding |> ok
        { SnapshotHead = snapshot.Commit
          SnapshotHighWater = snapshot.Header.NextSequence - 1L
          OriginalOperationId = Guid.Parse write.Operation.OperationId
          OriginalRequestHash = write.Operation.RequestHash }

    let withClones work =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq04-evidence-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let bare = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root [ "init"; "--bare"; "--initial-branch=main"; bare ] |> ignore
            git root [ "clone"; bare; first ] |> ignore
            git first [ "config"; "user.name"; "Fixture" ] |> ignore
            git first [ "config"; "user.email"; "fixture@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "first source snapshot")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "commit"; "-m"; "fixture source" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; bare; second ] |> ignore
            let projectId, ledgerId = Guid.NewGuid(), Guid.NewGuid()
            let binding directory =
                { RepositoryPath = directory; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = projectId; LedgerId = ledgerId; Epoch = 1 }
            let a, b = binding first, binding second
            LedgerStore.initialize a true |> ok |> ignore
            for agent in [ "Owner"; "Lead"; "Reviewer" ] do
                ReviewWorkflow.registerAgent a (Guid.NewGuid()) agent |> ok |> ignore
            work a b (git first [ "rev-parse"; "HEAD" ])
        finally
            try Directory.Delete(root, true) with _ -> ()

    [<Test>]
    member _.OriginalTaskReceiptIsNotACallerHighWaterOrPrefixAssertion() =
        withClones (fun a b _ ->
            let task = "canonical-task"
            let created = ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok
            let _, current, _ = ReviewWorkflow.getTask a task |> ok
            let closeId = Guid.NewGuid()
            let closed = ReviewWorkflow.closeTaskAs a "Lead" closeId task current.Version |> ok
            let original = anchor b closed
            let selector = TaskTransition(task, null, "CloseTask", 0)
            let found = WorkQueueCanonicalSourceEvidence.read b original selector |> ok
            Assert.That(found.OperationId, Is.EqualTo(closeId))
            Assert.That(found.OperationSequence, Is.EqualTo(closed.Operation.Sequence))
            Assert.That(found.OperationKind, Is.EqualTo("CloseTask"))
            Assert.That(found.EntityPath, Is.EqualTo(sprintf "tasks/%s/lifecycle/%d.json" task closed.Operation.Sequence))
            Assert.That(found.OriginHead, Is.Not.EqualTo(""))
            match found.Coverage with
            | IncompleteCoverage explanation ->
                Assert.That(explanation, Does.Contain("No atomic source Git"))
            match found.Event with
            | Lifecycle event ->
                Assert.That(event.Action, Is.EqualTo("CloseTask"))
                Assert.That(event.OperationId, Is.EqualTo(closeId.ToString("D")))
            | other -> Assert.Fail(sprintf "Wrong event kind: %A" other)

            // The request hash and original operation receipt must match, even
            // though the caller supplies a syntactically valid operation UUID.
            blocked (WorkQueueCanonicalSourceEvidence.read b
                        { original with OriginalRequestHash = String.replicate 64 "0" } selector)
            blocked (WorkQueueCanonicalSourceEvidence.read b
                        { original with OriginalOperationId = Guid.NewGuid() } selector)
            blocked (WorkQueueCanonicalSourceEvidence.read b
                        { original with OriginalOperationId = Guid.Parse(created.Operation.OperationId)
                                        OriginalRequestHash = created.Operation.RequestHash } selector)
            blocked (WorkQueueCanonicalSourceEvidence.read b original
                        (TaskTransition("renamed-task", null, "CloseTask", 0)))
            blocked (WorkQueueCanonicalSourceEvidence.read b
                        { original with SnapshotHighWater = original.SnapshotHighWater + 1L } selector)
            blocked (WorkQueueCanonicalSourceEvidence.read b
                        { original with SnapshotHead = String.replicate 40 "0" } selector)

            // A second clone advances canonical Git HEAD independently while
            // the first clone still holds its old, valid, immutable receipt.
            ReviewWorkflow.createTask a (Guid.NewGuid()) "second-task" "Second" |> ok |> ignore
            blocked (WorkQueueCanonicalSourceEvidence.read b original selector)
        )

    [<Test>]
    member _.ARewrittenLifecycleAuditIsNotOriginalEventEvidence() =
        withClones (fun a b _ ->
            let task = "immutable-audit-task"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let _, taskRecord, _ = ReviewWorkflow.getTask a task |> ok
            let closure =
                ReviewWorkflow.closeTaskAs a "Lead" (Guid.NewGuid()) task taskRecord.Version |> ok
            let original = anchor b closure
            let selector = TaskTransition(task, null, "CloseTask", 0)
            WorkQueueCanonicalSourceEvidence.read b original selector |> ok |> ignore
            let eventPath = sprintf "tasks/%s/lifecycle/%d.json" task closure.Operation.Sequence
            // Force a negative mutation in the disposable *canonical* Git ledger.
            // The bridge must follow the original operation effect, not mistake
            // a later rewritten entity for its original immutable audit.
            LedgerStore.transact b
                { OperationId = Guid.NewGuid(); Actor = "test-foreign-writer"
                  Kind = "TestRewriteOfAudit"; RequestJson = "{}" }
                (fun current ->
                    match LedgerStore.readAt b current eventPath with
                    | Ok(Some raw, Some oid) ->
                        let modified = raw.Replace("\"action\":\"CloseTask\"", "\"action\":\"ReopenTask\"")
                        if modified = raw then failwith "Expected source audit action field absent."
                        Ok [ { Path = eventPath; ExpectedBlobOid = Some oid; Content = modified } ]
                    | other -> failwithf "Missing source audit fixture: %A" other)
                |> ok |> ignore
            let newer = LedgerStore.fetch a |> ok
            let asserted =
                { original with SnapshotHead = newer.Commit
                                SnapshotHighWater = newer.Header.NextSequence - 1L }
            blocked (WorkQueueCanonicalSourceEvidence.read a asserted selector)
        )

    [<Test>]
    member _.ReviewVerdictBindsCanonicalDecisionSourceShaAndManagedRef() =
        withClones (fun a b sourceSha ->
            let task = "review-bound-task"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let sourceRef = "refs/heads/main"
            let reviewHead =
                ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                    sourceRef sourceSha "review frozen source" "task-bound" sourceRef |> ok
            let pendingAnchor = anchor b reviewHead
            let reviewAtSubmit =
                ReviewWorkflow.getBranchReviewAt b (LedgerStore.fetch b |> ok) task reviewId |> ok
            Assert.That(reviewAtSubmit.Status, Is.EqualTo("Unclaimed"))
            blocked (WorkQueueCanonicalSourceEvidence.read b pendingAnchor
                        (AcceptedReview(task, reviewId, reviewAtSubmit.ReviewRef, 1, 1, sourceSha, sourceRef)))
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1
                |> ok |> ignore
            let accepted =
                ReviewWorkflow.decideBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 2 Accept |> ok
            let decisionAnchor = anchor b accepted
            let review = ReviewWorkflow.getBranchReviewAt b (LedgerStore.fetch b |> ok) task reviewId |> ok
            let selector =
                AcceptedReview(task, reviewId, review.ReviewRef, review.SourceVersion,
                               review.Version, sourceSha, sourceRef)
            let evidence = WorkQueueCanonicalSourceEvidence.read b decisionAnchor selector |> ok
            match evidence.Event with
            | ReviewVerdict verdict ->
                Assert.That(verdict.Status, Is.EqualTo("Accepted"))
                Assert.That(verdict.DecisionSequence, Is.EqualTo(accepted.Operation.Sequence))
            | other -> Assert.Fail(sprintf "Not a decision: %A" other)
            blocked (WorkQueueCanonicalSourceEvidence.read b decisionAnchor
                        (AcceptedReview(task, reviewId, review.ReviewRef, 1, review.Version,
                                        String.replicate 40 "a", sourceRef)))
            blocked (WorkQueueCanonicalSourceEvidence.read b decisionAnchor
                        (AcceptedReview(task, reviewId, "refs/heads/jarvis-review/forged",
                                        1, review.Version, sourceSha, sourceRef)))
            blocked (WorkQueueCanonicalSourceEvidence.read b decisionAnchor
                        (AcceptedReview(task, reviewId, review.ReviewRef,
                                        1, review.Version + 1, sourceSha, sourceRef)))
            blocked (WorkQueueCanonicalSourceEvidence.read b decisionAnchor
                        (AcceptedReview("other-task", reviewId, review.ReviewRef,
                                        1, review.Version, sourceSha, sourceRef)))
            blocked (WorkQueueCanonicalSourceEvidence.read b decisionAnchor
                        (AcceptedReview(task, reviewId, review.ReviewRef,
                                        1, review.Version, sourceSha, "refs/heads/renamed-target")))
            // The source remote can advance without the orchestration ledger.
            // This is a valid typed historical verdict, not source-remote or
            // WorkQueue CAS authorization; coverage must remain incomplete.
            File.WriteAllText(Path.Combine(b.RepositoryPath, "source.txt"), "new unreviewed source")
            git b.RepositoryPath [ "add"; "source.txt" ] |> ignore
            git b.RepositoryPath [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test";
                                   "commit"; "-m"; "unreviewed source advance" ] |> ignore
            git b.RepositoryPath [ "push"; "origin"; "HEAD:refs/heads/main" ] |> ignore
            let historic = WorkQueueCanonicalSourceEvidence.read b decisionAnchor selector |> ok
            match historic.Coverage with IncompleteCoverage _ -> ()
        )

    [<Test>]
    member _.ChallengeEventIsOnlyReadFromOriginalCommittedEffect() =
        withClones (fun a b _ ->
            let task = "challenge-linked-task"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let id = Guid.NewGuid()
            let created =
                ChallengeWorkflow.create a id "Owner" "Read-only source proof" task
                    [ { OwnerKind = "Agent"; OwnerAgentId = "Lead"
                        SuggestedAgentId = ""; Description = "Inspect event and receipt" } ] |> ok
            let selector = ChallengeTransition(task, id, "Created")
            let receipt = anchor b created.Receipt
            let evidence = WorkQueueCanonicalSourceEvidence.read b receipt selector |> ok
            match evidence.Event with
            | Challenge challenge ->
                Assert.That(challenge.Action, Is.EqualTo("Created"))
                Assert.That(challenge.Sequence, Is.EqualTo(created.Receipt.Operation.Sequence))
            | other -> Assert.Fail(sprintf "Not challenge audit: %A" other)
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (ChallengeTransition(task, id, "ResolveChallengeCondition")))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (ChallengeTransition("renamed-task", id, "Created")))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (ChallengeTransition(task, Guid.NewGuid(), "Created")))
        )

    [<Test>]
    member _.OperatorResponseHasOriginalActorActionAndSequenceNotNotificationAuthority() =
        withClones (fun a b _ ->
            let task = "operator-event-task"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let id = Guid.NewGuid()
            let created =
                ChallengeWorkflow.create a id "Owner" "Operator decision" task
                    [ { OwnerKind = "Operator"; OwnerAgentId = "";
                        SuggestedAgentId = ""; Description = "Confirm disposition" } ] |> ok
            let operationId = Guid.NewGuid()
            let response =
                ChallengeWorkflow.respondOperator a operationId "LocalOperator" created.ChallengeId
                    created.ConditionIds.Head 1 "Verified answer" |> ok
            let pinned = anchor b response
            let selector = ChallengeTransition(task, id, "RespondOperatorCondition")
            let found = WorkQueueCanonicalSourceEvidence.read b pinned selector |> ok
            match found.Event with
            | Challenge audit ->
                Assert.That(audit.Action, Is.EqualTo("RespondOperatorCondition"))
                Assert.That(audit.ActorId, Is.EqualTo("LocalOperator"))
                Assert.That(audit.Sequence, Is.EqualTo(response.Operation.Sequence))
            | other -> Assert.Fail(sprintf "Not operator audit: %A" other)
            blocked (WorkQueueCanonicalSourceEvidence.read b pinned
                        (ChallengeTransition(task, id, "ResolveChallengeCondition")))
        )

    [<Test>]
    member _.RoleMembershipIsVersionedAndNoCrossLedgerAuthorizationIsReturned() =
        withClones (fun a b _ ->
            RoleInstructions.createRole a "Lead" (Guid.NewGuid()) "review-worker" "Role purpose" "Instructions"
                |> ok |> ignore
            let id = Guid.NewGuid()
            let attached = RoleInstructions.attachRole a "Lead" id "Owner" "review-worker" 0 |> ok
            let receipt = anchor b attached
            let selector = RoleMembership("Owner", "review-worker", 1, 1)
            let evidence = WorkQueueCanonicalSourceEvidence.read b receipt selector |> ok
            match evidence.Event with
            | AttachedRole (config, role) ->
                Assert.That(config.RoleIds, Does.Contain("review-worker"))
                Assert.That(config.Version, Is.EqualTo(1))
                Assert.That(role.Version, Is.EqualTo(1))
            | other -> Assert.Fail(sprintf "Not a canonical role membership: %A" other)
            match evidence.Coverage with
            | IncompleteCoverage _ -> ()
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (RoleMembership("Owner", "review-worker", 2, 1)))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (RoleMembership("renamed-owner", "review-worker", 1, 1)))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (RoleMembership("Owner", "review-worker", 1, 2)))
            // A new role version invalidates an earlier, otherwise valid snapshot.
            RoleInstructions.updateRole a "Lead" (Guid.NewGuid())
                "review-worker" 1 "Changed purpose" "Updated instructions" |> ok |> ignore
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt selector)
        )

    [<Test>]
    member _.PendingIntegrationCannotBeProjectedAsIntegratedEvenWithEqualSourceSha() =
        withClones (fun a b sourceSha ->
            let task = "integration-typed-task"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let sourceRef = "refs/heads/main"
            ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                sourceRef sourceSha "review exact commit" "task-bound" sourceRef |> ok |> ignore
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1 |> ok |> ignore
            ReviewWorkflow.decideBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 2 Accept
                |> ok |> ignore
            let submissionId = Guid.NewGuid()
            let submitted =
                ReviewWorkflow.submitIntegration a (Guid.NewGuid()) task "Owner" submissionId
                    sourceRef sourceSha (reviewId.ToString("N")) sourceRef |> ok
            let receipt = anchor b submitted
            let expected =
                IntegrationState(task, submissionId, reviewId, sourceSha, sourceRef, "Pending")
            let evidence = WorkQueueCanonicalSourceEvidence.read b receipt expected |> ok
            match evidence.Event with
            | Integration item -> Assert.That(item.Status, Is.EqualTo("Pending"))
            | other -> Assert.Fail(sprintf "Not an integration submission: %A" other)
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (IntegrationState(task, submissionId, reviewId, sourceSha, sourceRef, "Integrated")))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (IntegrationState(task, submissionId, Guid.NewGuid(), sourceSha, sourceRef, "Pending")))
            blocked (WorkQueueCanonicalSourceEvidence.read b receipt
                        (IntegrationState(task, submissionId, reviewId, String.replicate 40 "a", sourceRef, "Pending")))
        )
