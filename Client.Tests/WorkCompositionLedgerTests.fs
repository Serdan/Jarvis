module Client.Tests.WorkCompositionLedgerTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open FsToolkit.ErrorHandling
open Common
open NUnit.Framework
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkCompositionGitLedger
open Client.WorkCompositionAcceptanceAdapter
open Client.ReviewWorkflow
open Jarvis.WorkQueues

let private succeed = function
    | Ok value -> value
    | Error error -> failwithf "Unexpected ledger failure: %A" error

let private git directory arguments =
    use runner = new Process()
    runner.StartInfo.FileName <- "git"
    runner.StartInfo.WorkingDirectory <- directory
    runner.StartInfo.RedirectStandardOutput <- true
    runner.StartInfo.RedirectStandardError <- true
    for argument in arguments do runner.StartInfo.ArgumentList.Add(argument)
    Assert.That(runner.Start(), Is.True)
    let output = runner.StandardOutput.ReadToEnd()
    let error = runner.StandardError.ReadToEnd()
    runner.WaitForExit()
    Assert.That(runner.ExitCode, Is.EqualTo(0), error)
    output.Trim()

let private withTwoClones action =
    let root = Path.Combine(Path.GetTempPath(), "jarvis-composition-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    try
        let remote = Path.Combine(root, "remote.git")
        let first = Path.Combine(root, "first")
        let second = Path.Combine(root, "second")
        git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
        git root [ "clone"; remote; first ] |> ignore
        File.WriteAllText(Path.Combine(first, "bootstrap.txt"), "source")
        git first [ "add"; "bootstrap.txt" ] |> ignore
        git first [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.test"; "commit"; "-m"; "bootstrap" ] |> ignore
        git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
        git root [ "clone"; remote; second ] |> ignore
        let projectId = Guid.NewGuid()
        let ledgerId = Guid.NewGuid()
        let binding repository =
            { RepositoryPath = repository
              RemoteName = "origin"
              ApprovedFetchUrl = remote
              ApprovedPushUrl = remote
              ProjectId = projectId
              LedgerId = ledgerId
              Epoch = 1 }
        let firstBinding, secondBinding = binding first, binding second
        LedgerStore.initialize firstBinding true |> succeed |> ignore
        LedgerStore.fetch secondBinding |> succeed |> ignore
        action firstBinding secondBinding
    finally
        try Directory.Delete(root, true) with _ -> ()

let private spec title task =
    { Title = title
      Goal = "Deliver " + title
      Scope = "Required outcome"
      AcceptanceCriteria = Set.singleton ("Test " + title)
      Constraints = Set.empty
      LinkedTaskId = task }

[<Test>]
let ``composition graph is durable and shared by two clients through project ledger CAS`` () =
    withTwoClones (fun first second ->
        let _, initial = WorkCompositionGitLedger.load first |> succeed
        Assert.That(initial.Version, Is.EqualTo(0))
        let root = WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None)
        let originalOperation = Guid.NewGuid()
        let write = WorkCompositionGitLedger.execute first "Fixture" originalOperation 0 root |> succeed
        Assert.That(write.AlreadyApplied, Is.False)
        let duplicate = WorkCompositionGitLedger.execute second "Fixture" originalOperation 0 root |> succeed
        Assert.That(duplicate.AlreadyApplied, Is.True)
        let _, graph = WorkCompositionGitLedger.load second |> succeed
        Assert.That(graph.Version, Is.EqualTo(1))
        Assert.That(graph.Units.["project"].Spec.Title, Is.EqualTo("Project"))
        match WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 0
                 (WorkCompositionMutation.Create("system", WorkUnitKind.System, Some "project", spec "System" None)) with
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected version conflict: %A" other)
        WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 1
            (WorkCompositionMutation.Create("system", WorkUnitKind.System, Some "project", spec "System" None))
        |> succeed |> ignore
        let _, reloaded = WorkCompositionGitLedger.load first |> succeed
        Assert.That(reloaded.Version, Is.EqualTo(2))
        Assert.That(reloaded.Units.["system"].ParentId, Is.EqualTo(Some "project")))

[<Test>]
let ``acceptance mutation rejects unaudited evidence and missing WQ coverage is not readiness`` () =
    withTwoClones (fun first second ->
        WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create("project", WorkUnitKind.Project, None, spec "Project" None))
        |> succeed |> ignore
        WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) 1
            (WorkCompositionMutation.Create("task", WorkUnitKind.Feature, Some "project", spec "Task" (Some "canonical-task")))
        |> succeed |> ignore
        match WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 2
                 (WorkCompositionMutation.Accept("task", 1, "caller-string")) with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected fail-closed acceptance: %A" other)
        let overviews = WorkCompositionGitLedger.loadOverviews second |> succeed
        let task = overviews |> List.find (fun view -> view.Id = "task")
        Assert.That(task.Phase, Is.EqualTo(WorkUnitPhase.Unverified)))

[<Test>]
let ``composition acceptance requires exact independently reviewed and integrated source proof`` () =
    withTwoClones (fun first second ->
        let owner = "Owner"
        let reviewer = "Reviewer"
        let taskId = "project-acceptance-validation"
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) owner |> succeed |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> succeed |> ignore
        ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Independent project acceptance" |> succeed |> ignore
        WorkCompositionGitLedger.execute first owner (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create("project", WorkUnitKind.Project, None,
                                            spec "Project" (Some taskId)))
        |> succeed |> ignore
        let _, initial = WorkCompositionGitLedger.load second |> succeed
        let purpose = WorkCompositionAcceptanceAdapter.reviewPurpose initial initial.Units.["project"]

        git first.RepositoryPath [ "config"; "user.name"; "Fixture" ] |> ignore
        git first.RepositoryPath [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        git first.RepositoryPath [ "checkout"; "-b"; "feature/composition-acceptance" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "verification.txt"),
                          "Independently verified project acceptance")
        git first.RepositoryPath [ "add"; "verification.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Acceptance evidence for project" ] |> ignore
        let acceptedSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let reviewId = Guid.NewGuid()
        let sourceRef = "refs/heads/feature/composition-acceptance"
        let targetRef = "refs/heads/main"
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            reviewId sourceRef acceptedSha "Validate project acceptance criteria"
            purpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 1
        |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 2 Accept
        |> succeed |> ignore
        let submissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration second (Guid.NewGuid()) taskId owner
            submissionId "" "" (reviewId.ToString("N")) targetRef |> succeed |> ignore

        // A review accepted by its own author is not independent acceptance.
        // Give the self-reviewed submission its own frozen descendant source
        // so BOTH receipts must perform actual independently attributed
        // non-force publications, rather than repeating an external push.
        File.WriteAllText(Path.Combine(first.RepositoryPath, "self-review.txt"),
                          "Author-only review does not qualify as independent acceptance")
        git first.RepositoryPath [ "add"; "self-review.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Distinct self-reviewed source" ] |> ignore
        let selfReviewSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let selfReviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            selfReviewId sourceRef selfReviewSha "Self-reviewed project"
            purpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId selfReviewId owner 1
        |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId selfReviewId owner 2 Accept
        |> succeed |> ignore
        let selfSubmissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration second (Guid.NewGuid()) taskId owner
            selfSubmissionId "" "" (selfReviewId.ToString("N")) targetRef |> succeed |> ignore

        // Accepted review alone cannot accept work that has not integrated.
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                  (Guid.NewGuid()) initial.Version "project" 1 submissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected pending-integration rejection: %A" other)

        // Prepare local source checkout, without advancing remote main.
        // CompleteIntegration owns both actual fast-forward non-force pushes.
        git first.RepositoryPath [ "checkout"; "main" ] |> ignore
        git first.RepositoryPath [ "merge"; "--ff-only"; selfReviewSha ] |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) taskId reviewer
            submissionId acceptedSha |> succeed |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) taskId reviewer
            selfSubmissionId selfReviewSha |> succeed |> ignore
        let directPath = $"tasks/{taskId}/integrations/{submissionId:N}.json"
        let verifiedSnapshot = LedgerStore.fetch first |> succeed
        let directCompletion =
            LedgerStore.findCompletionOperationForPathAt first verifiedSnapshot directPath |> succeed
        LedgerStore.verifyCompletionOperationRequest first directCompletion taskId submissionId acceptedSha
        |> succeed
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                  (Guid.NewGuid()) initial.Version "project" 1 selfSubmissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected same-author review rejection: %A" other)

        // The genuine non-force publisher succeeded above; nevertheless no
        // independently authenticated publisher trust root exists to authorize
        // WorkComposition CAS acceptance in the production adapter.
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                  (Guid.NewGuid()) initial.Version "project" 1 submissionId with
        | Error(PublisherProvenanceUnavailable _) -> ()
        | other -> Assert.Fail(sprintf "Unauthenticated direct publication was accepted: %A" other)
        let _, heldGraph = WorkCompositionGitLedger.load first |> succeed
        Assert.That(WorkComposition.currentAcceptance heldGraph "project", Is.EqualTo(None))

        // Remaining assertions only exercise graph closure/CAS semantics,
        // with an explicit TEST-ONLY verifier standing in for a future real
        // independent publisher authority. This is NOT a positive producer
        // provenance/production adapter acceptance test.
        let fixtureAccept binding actor op version unitId (sid: Guid) =
            WorkCompositionGitLedger.executeVerifiedAcceptance binding actor op
                version unitId 1 ("integration:" + sid.ToString("N"))
                (fun _ _ _ _ -> Ok ())
        let acceptanceOp = Guid.NewGuid()
        let accepted =
            fixtureAccept second owner acceptanceOp initial.Version "project" submissionId |> succeed
        Assert.That(accepted.AlreadyApplied, Is.False)
        let duplicate =
            fixtureAccept first owner acceptanceOp initial.Version "project" submissionId |> succeed
        Assert.That(duplicate.AlreadyApplied, Is.True)
        let _, acceptedGraph = WorkCompositionGitLedger.load first |> succeed
        Assert.That(WorkComposition.currentAcceptance acceptedGraph "project" |> Option.isSome, Is.True)
        Assert.That(acceptedGraph.Units.["project"].Acceptances.Count, Is.EqualTo(1))

        // Adding corrective work retains the historical proof, but changes
        // the effective parent target even though its own spec is unchanged.
        let correctionTaskId = "correction-acceptance-validation"
        ReviewWorkflow.createTask first (Guid.NewGuid()) correctionTaskId
            "Independently verify corrective feature" |> succeed |> ignore
        WorkCompositionGitLedger.execute first owner (Guid.NewGuid()) acceptedGraph.Version
            (WorkCompositionMutation.Create("correction", WorkUnitKind.Feature, Some "project",
                                            spec "Correction" (Some correctionTaskId)))
        |> succeed |> ignore
        let _, withChild = WorkCompositionGitLedger.load second |> succeed
        Assert.That(WorkComposition.currentAcceptance withChild "project" |> Option.isNone, Is.True)
        Assert.That(
            WorkCompositionAcceptanceAdapter.reviewPurpose withChild withChild.Units.["project"],
            Is.Not.EqualTo(purpose))
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                  (Guid.NewGuid()) withChild.Version "project" 1 submissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected review to become stale after adding child: %A" other)

        // Independently review, integrate and accept the child. Once all
        // children are complete the *old* parent review must still fail.
        git first.RepositoryPath [ "checkout"; "-b"; "feature/composition-correction" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "correction-verification.txt"),
                          "Independently verified corrective feature")
        git first.RepositoryPath [ "add"; "correction-verification.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Verify corrective feature" ] |> ignore
        let correctionSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let correctionRef = "refs/heads/feature/composition-correction"
        let correctionPurpose =
            WorkCompositionAcceptanceAdapter.reviewPurpose withChild withChild.Units.["correction"]
        let correctionReviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) correctionTaskId owner
            correctionReviewId correctionRef correctionSha "Validate corrective feature"
            correctionPurpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) correctionTaskId
            correctionReviewId reviewer 1 |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) correctionTaskId
            correctionReviewId reviewer 2 Accept |> succeed |> ignore
        let correctionSubmissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration second (Guid.NewGuid()) correctionTaskId owner
            correctionSubmissionId "" "" (correctionReviewId.ToString("N")) targetRef
        |> succeed |> ignore
        git first.RepositoryPath [ "checkout"; "main" ] |> ignore
        git first.RepositoryPath [ "merge"; "--ff-only"; correctionSha ] |> ignore
        // Deliberately leave bare remote unchanged until trusted publisher.
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) correctionTaskId reviewer
            correctionSubmissionId correctionSha |> succeed |> ignore
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner (Guid.NewGuid())
                  withChild.Version "correction" 1 correctionSubmissionId with
        | Error(PublisherProvenanceUnavailable _) -> ()
        | other -> Assert.Fail(sprintf "Unauthenticated corrective publication was accepted: %A" other)
        fixtureAccept second owner (Guid.NewGuid()) withChild.Version "correction" correctionSubmissionId
        |> succeed |> ignore
        let _, withAcceptedChild = WorkCompositionGitLedger.load first |> succeed
        let childProof = WorkComposition.currentAcceptance withAcceptedChild "correction" |> Option.get
        Assert.That(WorkComposition.currentAcceptance withAcceptedChild "project" |> Option.isNone, Is.True)
        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner
                  (Guid.NewGuid()) withAcceptedChild.Version "project" 1 submissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Old parent review replayed after accepted child: %A" other)

        // Fresh, independent parent review now binds the newly accepted child
        // revision, and can authorize a new parent acceptance target.
        let freshPurpose =
            WorkCompositionAcceptanceAdapter.reviewPurpose
                withAcceptedChild withAcceptedChild.Units.["project"]
        // Parent acceptance needs a fresh reviewed outcome after child
        // integration. An already published child's exact SHA is NOT a new
        // non-force publication and must not be rubber-stamped as one.
        git first.RepositoryPath [ "checkout"; "feature/composition-correction" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "parent-verification.txt"),
                          "New independently reviewed composed parent outcome")
        git first.RepositoryPath [ "add"; "parent-verification.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Fresh composed parent outcome" ] |> ignore
        let freshSha = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let freshReviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            freshReviewId correctionRef freshSha "Validate composed project outcome"
            freshPurpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId
            freshReviewId reviewer 1 |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId
            freshReviewId reviewer 2 Accept |> succeed |> ignore
        let freshSubmissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration second (Guid.NewGuid()) taskId owner
            freshSubmissionId "" "" (freshReviewId.ToString("N")) targetRef
        |> succeed |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) taskId reviewer
            freshSubmissionId freshSha |> succeed |> ignore
        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner (Guid.NewGuid())
                  withAcceptedChild.Version "project" 1 freshSubmissionId with
        | Error(PublisherProvenanceUnavailable _) -> ()
        | other -> Assert.Fail(sprintf "Unauthenticated parent publication was accepted: %A" other)
        fixtureAccept first owner (Guid.NewGuid()) withAcceptedChild.Version "project" freshSubmissionId
        |> succeed |> ignore
        let _, refreshed = WorkCompositionGitLedger.load second |> succeed
        let parentProof = WorkComposition.currentAcceptance refreshed "project" |> Option.get
        Assert.That(parentProof.ChildAcceptances.["correction"], Is.EqualTo(childProof.GraphVersion))
        Assert.That(refreshed.Units.["project"].Acceptances.Count, Is.EqualTo(2))

        // A later change to the parent's *own* specification also invalidates
        // the new acceptance, without deleting historical target receipts.
        WorkCompositionGitLedger.execute second owner (Guid.NewGuid()) refreshed.Version
            (WorkCompositionMutation.Revise("project", 1,
                { refreshed.Units.["project"].Spec with Scope = "New goal boundary" }))
        |> succeed |> ignore
        let _, revised = WorkCompositionGitLedger.load first |> succeed
        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner
                  (Guid.NewGuid()) revised.Version "project" 2 freshSubmissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected stale target-review rejection: %A" other))

[<Test>]
let compositionAcceptanceRejectsNewlyForgedLegacyDirectCompletion () =
    withTwoClones (fun first second ->
        let owner, reviewer = "Owner", "Reviewer"
        let taskId, targetRef = "composition-forged-legacy-direct", "refs/heads/main"
        git first.RepositoryPath [ "config"; "user.name"; "Fixture" ] |> ignore
        git first.RepositoryPath [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) owner |> succeed |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> succeed |> ignore
        ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Historical direct acceptance"
        |> succeed |> ignore
        WorkCompositionGitLedger.execute first owner (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create(
                "project", WorkUnitKind.Project, None, spec "Project" (Some taskId)))
        |> succeed |> ignore
        let _, initial = WorkCompositionGitLedger.load second |> succeed
        let purpose = WorkCompositionAcceptanceAdapter.reviewPurpose initial initial.Units.["project"]
        git first.RepositoryPath [ "checkout"; "-b"; "feature/composition-historical-direct" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "historical-direct.txt"), "reviewed direct source")
        git first.RepositoryPath [ "add"; "historical-direct.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Historical direct source" ] |> ignore
        let source = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let reviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            reviewId "refs/heads/feature/composition-historical-direct" source
            "Historical direct review" purpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 1
        |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 2 Accept
        |> succeed |> ignore
        let submissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration first (Guid.NewGuid()) taskId owner
            submissionId "" "" (reviewId.ToString("N")) targetRef |> succeed |> ignore
        let pending = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(pending.Status, Is.EqualTo("Pending"))

        // A NEW canonical CompleteIntegration transaction can manufacture
        // pre-v1-looking fields with a backdated CompletedAtUtc. Even when
        // remote main already includes S, this is not historical origin proof.
        git first.RepositoryPath [ "push"; "origin"; $"{source}:{targetRef}" ] |> ignore
        let forgedLegacy =
            { pending with
                Status = "Integrated"
                IntegratedCommit = source
                CompletedAtUtc = "2026-11-06T03:16:08Z" }
        let path = $"tasks/{taskId}/integrations/{submissionId:N}.json"
        let operation: LedgerIntent =
            { OperationId = Guid.NewGuid()
              Actor = reviewer
              Kind = "CompleteIntegration"
              RequestJson = JsonSerializer.Serialize
                {| taskId = taskId; submissionId = submissionId.ToString("D"); integratedCommit = source |} }
        LedgerStore.transact second operation (fun snapshot -> result {
            let! _, oid = LedgerStore.readAt second snapshot path
            let serialized = JsonSerializer.Serialize(
                forgedLegacy, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
            return [ { Path = path
                       ExpectedBlobOid = oid
                       Content = "{\"schemaVersion\":2," + serialized.Substring(1) } ]
        }) |> succeed |> ignore

        let readBack = (ReviewWorkflow.getIntegrationSubmissions first taskId |> succeed).Latest.Value
        Assert.That(readBack.PublicationOperationId, Is.Null)
        Assert.That(readBack.IntegratedCommit, Is.EqualTo(source))
        Assert.That(LedgerStore.readRemoteTarget first targetRef |> succeed, Is.EqualTo(source))
        let snapshot = LedgerStore.fetch first |> succeed
        let completion = LedgerStore.findCompletionOperationForPathAt first snapshot path |> succeed
        LedgerStore.verifyCompletionOperationRequest first completion taskId submissionId source
        |> succeed
        let rejectArguments task id commit =
            match LedgerStore.verifyCompletionOperationRequest first completion task id commit with
            | Error(InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "Unbound completion request accepted: %A" other)
        rejectArguments "different-task" submissionId source
        rejectArguments taskId (Guid.NewGuid()) source
        rejectArguments taskId submissionId (String.replicate 40 "a")

        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner
                  (Guid.NewGuid()) initial.Version "project" 1 submissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Fresh legacy-shaped completion was accepted: %A" other)
        let _, graph = WorkCompositionGitLedger.load second |> succeed
        Assert.That(WorkComposition.currentAcceptance graph "project", Is.EqualTo(None))

        // A second new completion also cannot claim this receipt with request
        // arguments for some OTHER submission, regardless of remote S.
        let mismatchedId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration first (Guid.NewGuid()) taskId owner
            mismatchedId "" "" (reviewId.ToString("N")) targetRef |> succeed |> ignore
        let pending2 = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(pending2.SubmissionId, Is.EqualTo(mismatchedId.ToString("N")))
        let path2 = $"tasks/{taskId}/integrations/{mismatchedId:N}.json"
        let fabricatedOperation: LedgerIntent =
            { OperationId = Guid.NewGuid()
              Actor = reviewer
              Kind = "CompleteIntegration"
              RequestJson = JsonSerializer.Serialize
                {| taskId = taskId; submissionId = Guid.NewGuid().ToString("D"); integratedCommit = source |} }
        LedgerStore.transact second fabricatedOperation (fun current -> result {
            let! _, oid = LedgerStore.readAt second current path2
            let forged =
                { pending2 with
                    Status = "Integrated"
                    IntegratedCommit = source
                    CompletedAtUtc = "2026-11-06T03:16:08Z" }
            let serialized = JsonSerializer.Serialize(
                forged, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
            return [ { Path = path2
                       ExpectedBlobOid = oid
                       Content = "{\"schemaVersion\":2," + serialized.Substring(1) } ]
        }) |> succeed |> ignore
        let snapshot2 = LedgerStore.fetch first |> succeed
        let wrongOp = LedgerStore.findCompletionOperationForPathAt first snapshot2 path2 |> succeed
        match LedgerStore.verifyCompletionOperationRequest first wrongOp taskId mismatchedId source with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Mismatched canonical completion request accepted: %A" other)
        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner
                  (Guid.NewGuid()) initial.Version "project" 1 mismatchedId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Forged mismatched submission was accepted: %A" other))

[<Test>]
let compositionAcceptanceRequiresOriginalNonForceReceiptForDistinctCleanMerge () =
    withTwoClones (fun first second ->
        let owner, reviewer = "Owner", "Reviewer"
        let taskId = "composition-merge-acceptance"
        let targetRef = "refs/heads/main"
        for client in [ first; second ] do
            git client.RepositoryPath [ "config"; "user.name"; "Fixture" ] |> ignore
            git client.RepositoryPath [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        let baseHead = git second.RepositoryPath [ "rev-parse"; "HEAD" ]
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) owner |> succeed |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> succeed |> ignore
        ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Clean-merge project acceptance"
        |> succeed |> ignore
        WorkCompositionGitLedger.execute first owner (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create(
                "project", WorkUnitKind.Project, None, spec "Project" (Some taskId)))
        |> succeed |> ignore
        let _, initial = WorkCompositionGitLedger.load second |> succeed
        let purpose = WorkCompositionAcceptanceAdapter.reviewPurpose initial initial.Units.["project"]

        // Source S is independently reviewed while remote main is still B.
        git first.RepositoryPath [ "checkout"; "-b"; "feature/composition-clean-merge" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "reviewed.txt"), "exact reviewed source")
        git first.RepositoryPath [ "add"; "reviewed.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Independent reviewed outcome" ] |> ignore
        let source = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let sourceRef = "refs/heads/feature/composition-clean-merge"
        let reviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            reviewId sourceRef source "Source review" purpose targetRef |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 1
        |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 2 Accept
        |> succeed |> ignore

        // A different clone independently advances remote main to T.
        File.WriteAllText(Path.Combine(second.RepositoryPath, "target.txt"), "independent target")
        git second.RepositoryPath [ "add"; "target.txt" ] |> ignore
        git second.RepositoryPath [ "commit"; "-m"; "Independent target advancement" ] |> ignore
        let target = git second.RepositoryPath [ "rev-parse"; "HEAD" ]
        Assert.That(target, Is.Not.EqualTo(source))
        git second.RepositoryPath [ "push"; "origin"; "main" ] |> ignore
        let submissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration first (Guid.NewGuid()) taskId owner
            submissionId "" "" (reviewId.ToString("N")) targetRef |> succeed |> ignore
        let pending = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(pending.SubmittedCommit, Is.EqualTo(source))
        Assert.That(pending.TargetHeadAtSubmission, Is.EqualTo(target))

        // Compose exactly I=(T,S). CompleteIntegration itself publishes I via
        // the bounded non-force writer and commits its immutable receipt.
        git first.RepositoryPath [ "fetch"; "origin"; "main" ] |> ignore
        git first.RepositoryPath [ "checkout"; "--detach"; "FETCH_HEAD" ] |> ignore
        git first.RepositoryPath [ "merge"; "--no-ff"; "--no-edit"; source ] |> ignore
        let integrated = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        Assert.That(integrated, Is.Not.EqualTo(source))
        Assert.That(git first.RepositoryPath [ "rev-list"; "--parents"; "-n"; "1"; integrated ],
                    Is.EqualTo(String.concat " " [ integrated; target; source ]))
        let completionId = Guid.NewGuid()
        ReviewWorkflow.completeIntegration first completionId taskId reviewer submissionId integrated
        |> succeed |> ignore
        let completed = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(completed.IntegratedCommit, Is.EqualTo(integrated))
        Assert.That(completed.PublicationPreviousHead, Is.EqualTo(target))
        Assert.That(completed.PublicationObservedHead, Is.EqualTo(integrated))
        Assert.That(completed.PublicationOperationId, Is.EqualTo(completionId.ToString("D")))
        Assert.That(completed.PublicationMechanism, Is.EqualTo("VerifiedNonForceGitPush/v1"))
        Assert.That(LedgerStore.readRemoteTarget second targetRef |> succeed, Is.EqualTo(integrated))

        // Even with an otherwise valid mechanical merge, a forged or
        // post-hoc altered canonical receipt cannot borrow publisher authority.
        let replaceReceiptFor (id: Guid) (record: IntegrationSubmission) =
            let path = "tasks/" + taskId + "/integrations/" + id.ToString("N") + ".json"
            let action: LedgerIntent =
                { OperationId = Guid.NewGuid()
                  Actor = "Fixture"
                  Kind = "Fixture.AdversarialReceiptMutation"
                  RequestJson = CanonicalCodec.encode record }
            LedgerStore.transact second action (fun snapshot -> result {
                let! _, priorBlob = LedgerStore.readAt second snapshot path
                return [ { Path = path
                           ExpectedBlobOid = priorBlob
                           Content = "{\"schemaVersion\":2," +
                                     (JsonSerializer.Serialize(record, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))).Substring(1) } ]
            }) |> succeed |> ignore
        let replaceReceipt = replaceReceiptFor submissionId
        let failTampered (forged: IntegrationSubmission) =
            replaceReceipt forged
            match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                    (Guid.NewGuid()) initial.Version "project" 1 submissionId with
            | Error(InvalidBinding _) | Error(LedgerIntegrityError _) -> ()
            | other -> Assert.Fail(sprintf "Forged WorkComposition publication was accepted: %A" other)
            replaceReceipt completed

        failTampered { completed with PublicationMechanism = null }
        failTampered { completed with PublicationOperationId = Guid.NewGuid().ToString("D") }
        failTampered { completed with PublicationPreviousHead = source }
        failTampered { completed with PublicationObservedHead = source }
        // Downgrading I to S must not disable immutable completion authentication.
        failTampered { completed with IntegratedCommit = source }
        failTampered
            { completed with IntegratedCommit = source
                             PublicationPreviousHead = null
                             PublicationObservedHead = null
                             PublicationOperationId = null
                             PublicationMechanism = null }

        // An Integrated-shaped record without a CompleteIntegration operation
        // is not a valid direct receipt, even if the source is now on main.
        let uncompletedId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration first (Guid.NewGuid()) taskId owner
            uncompletedId "" "" (reviewId.ToString("N")) targetRef |> succeed |> ignore
        let uncompleted = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(uncompleted.SubmissionId, Is.EqualTo(uncompletedId.ToString("N")))
        replaceReceiptFor uncompletedId
            { uncompleted with Status = "Integrated"
                               IntegratedCommit = source
                               CompletedAtUtc = "2026-11-21T08:00:00Z" }
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                (Guid.NewGuid()) initial.Version "project" 1 uncompletedId with
        | Error(InvalidBinding _) | Error(LedgerIntegrityError _) -> ()
        | other -> Assert.Fail(sprintf "Completion-less direct receipt was accepted: %A" other)
        replaceReceiptFor uncompletedId uncompleted

        // Extra edits and manually resolved conflicts cannot be mistaken
        // for Git's exact conflict-free mechanical merge result.
        File.WriteAllText(Path.Combine(first.RepositoryPath, "unreviewed.txt"), "unreviewed addition")
        git first.RepositoryPath [ "add"; "unreviewed.txt" ] |> ignore
        let editedTree = git first.RepositoryPath [ "write-tree" ]
        let extraMerge =
            git first.RepositoryPath [ "commit-tree"; editedTree; "-p"; target; "-p"; source;
                                       "-m"; "Forged extra tree changes" ]
        match LedgerStore.verifyPublishedMechanicalIntegration first targetRef target
                source extraMerge target integrated with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Extra merge-tree edits gained verified proof: %A" other)

        git first.RepositoryPath [ "reset"; "--hard"; integrated ] |> ignore

        // Independent source S2 adds target.txt differently from target T:
        // Git merge-tree must report a conflict, never silently choose a side.
        git first.RepositoryPath [ "checkout"; "-b"; "feature/conflicted-proof"; baseHead ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "target.txt"), "source-side conflict")
        git first.RepositoryPath [ "add"; "target.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Conflicting reviewed candidate" ] |> ignore
        let conflictSource = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let targetTree = git first.RepositoryPath [ "rev-parse"; target + "^{tree}" ]
        let fakeResolution =
            git first.RepositoryPath [ "commit-tree"; targetTree; "-p"; target; "-p"; conflictSource;
                                       "-m"; "Unreviewed conflict resolution" ]
        match LedgerStore.verifyPublishedMechanicalIntegration first targetRef target
                conflictSource fakeResolution target integrated with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Unreviewed merge conflict gained verified proof: %A" other)

        // The genuine mechanical I=(T,S) and immutable v1 field restoration
        // are still insufficient to authenticate a publisher execution.
        // With no verified trust root, hold even the legitimate writer's
        // receipt rather than mint a false graph acceptance.
        let acceptanceOp = Guid.NewGuid()
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner acceptanceOp
                initial.Version "project" 1 submissionId with
        | Error(PublisherProvenanceUnavailable _) -> ()
        | other -> Assert.Fail(sprintf "Unauthenticated mechanical merge was accepted: %A" other)
        let _, heldGraph = WorkCompositionGitLedger.load first |> succeed
        Assert.That(heldGraph, Is.EqualTo(initial))
        Assert.That(WorkComposition.currentAcceptance heldGraph "project", Is.EqualTo(None))
        match LedgerStore.getOperationAt first (LedgerStore.fetch first |> succeed) acceptanceOp with
        | Ok None -> ()
        | other -> Assert.Fail(sprintf "Publisher HOLD wrote an acceptance operation: %A" other))

[<Test>]
let historicalCancellationProofLedgerRoundTrip () =
    withTwoClones (fun first second ->
        let step mutation (graph: WorkCompositionGraph) =
            match WorkComposition.apply graph.Version mutation graph with
            | Ok next -> next
            | Error error -> failwithf "Invalid composition test fixture: %A" error

        // The pure model records a cancellation at -SpecVersion. Reopening
        // and re-cancelling yields -2 rather than the initial -1. Construct
        // the proven state directly to exercise ledger loading independently
        // of the production-only verified acceptance adapter.
        let accepted =
            WorkComposition.empty
            |> step (WorkCompositionMutation.Create(
                "project", WorkUnitKind.Project, None, spec "Project" None))
            |> step (WorkCompositionMutation.Create(
                "child", WorkUnitKind.Feature, Some "project", spec "Child" None))
            |> step (WorkCompositionMutation.Cancel("child", "Initial exclusion"))
            |> step (WorkCompositionMutation.Reopen "child")
            |> step (WorkCompositionMutation.Cancel("child", "Revised exclusion"))
            |> step (WorkCompositionMutation.Accept("project", 1, "integration:fixture"))
        let originalProof = WorkComposition.currentAcceptance accepted "project" |> Option.get
        Assert.That(originalProof.ChildAcceptances.["child"], Is.EqualTo(-2))

        let envelope: CompositionEnvelope =
            { SchemaVersion = 1
              ProjectId = first.ProjectId.ToString("D")
              StateHash = CanonicalCodec.digest (CanonicalCodec.encode accepted)
              Graph = accepted }
        let intent: LedgerIntent =
            { OperationId = Guid.NewGuid()
              Actor = "Fixture"
              Kind = "Fixture.CompositionRoundTrip"
              RequestJson = CanonicalCodec.encode accepted }
        LedgerStore.transact first intent (fun _ ->
            Ok [ { Path = WorkCompositionGitLedger.RecordPath
                   ExpectedBlobOid = None
                   Content = CanonicalCodec.encode envelope } ])
        |> succeed |> ignore

        let _, reloaded = WorkCompositionGitLedger.load second |> succeed
        Assert.That(WorkComposition.currentAcceptance reloaded "project" |> Option.isSome, Is.True)
        Assert.That(reloaded.Units.["project"].Acceptances.Count, Is.EqualTo(1))

        // A subsequent legitimate CAS mutation must also preserve history,
        // while making the old parent target no longer current.
        WorkCompositionGitLedger.execute second "Fixture" (Guid.NewGuid()) reloaded.Version
            (WorkCompositionMutation.Create(
                "correction", WorkUnitKind.Feature, Some "project", spec "Correction" None))
        |> succeed |> ignore
        let _, withCorrection = WorkCompositionGitLedger.load first |> succeed
        Assert.That(WorkComposition.currentAcceptance withCorrection "project" |> Option.isNone, Is.True)
        Assert.That(withCorrection.Units.["project"].Acceptances.[string originalProof.GraphVersion],
                    Is.EqualTo(originalProof)))


[<Test>]
let ``operator mutation input maps explicit units and forbids caller acceptance`` () =
    let json = """{"action":"Create","unitId":"scheduling","kind":"System","parentId":"project","title":"Scheduling","goal":"Dispatch ready work","scope":"Queues","acceptanceCriteria":["Two prerequisites gate launch"],"constraints":["Git is authoritative"],"linkedTaskId":"canonical-scheduling"}"""
    match Client.WorkCompositionOperatorInputs.WorkCompositionOperatorInputs.parseMutation json with
    | Ok(WorkCompositionMutation.Create(id, WorkUnitKind.System, Some parent, input)) ->
        Assert.That(id, Is.EqualTo("scheduling"))
        Assert.That(parent, Is.EqualTo("project"))
        Assert.That(input.LinkedTaskId, Is.EqualTo(Some "canonical-scheduling"))
        Assert.That(input.AcceptanceCriteria.Contains("Two prerequisites gate launch"), Is.True)
    | other -> Assert.Fail(sprintf "Expected explicit System mutation: %A" other)

    let revise = """{"action":"revise","unitId":"scheduling","expectedSpecVersion":3,"title":"Scheduling","goal":"Dispatch","scope":"Queues","acceptanceCriteria":["Ready"],"constraints":[]}"""
    match Client.WorkCompositionOperatorInputs.WorkCompositionOperatorInputs.parseMutation revise with
    | Ok(WorkCompositionMutation.Revise("scheduling", 3, _)) -> ()
    | other -> Assert.Fail(sprintf "Expected version-pinned revision: %A" other)

    let forged = """{"action":"accept","unitId":"scheduling","reason":"trusted"}"""
    match Client.WorkCompositionOperatorInputs.WorkCompositionOperatorInputs.parseMutation forged with
    | Error message when message.Contains("separately verified") -> ()
    | other -> Assert.Fail(sprintf "Unexpected arbitrary acceptance: %A" other)

    match Client.WorkCompositionOperatorInputs.WorkCompositionOperatorInputs.parseMutation "{}" with
    | Error message when message.Contains("unitId") -> ()
    | other -> Assert.Fail(sprintf "Unbounded operator input: %A" other)

[<Test>]
let ``composition operator overview pages pin specs and stages to one verified Git head`` () =
    withTwoClones (fun first second ->
        let create version id kind parent =
            WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) version
                (WorkCompositionMutation.Create(id, kind, parent, spec id None))
            |> succeed |> ignore
        create 0 "project" WorkUnitKind.Project None
        create 1 "system" WorkUnitKind.System (Some "project")
        create 2 "feature" WorkUnitKind.Feature (Some "system")

        let initial = WorkCompositionGitLedger.loadOverviewPage second "" 1 "" |> succeed
        Assert.That(initial.TotalUnits, Is.EqualTo(3))
        Assert.That(initial.Items.Length, Is.EqualTo(1))
        Assert.That(initial.Items.Head.Unit.Id, Is.EqualTo("feature"))
        Assert.That(initial.Items.Head.Overview.Id, Is.EqualTo("feature"))
        Assert.That(initial.NextCursor.IsSome, Is.True)

        let next = WorkCompositionGitLedger.loadOverviewPage second "" 1 initial.NextCursor.Value |> succeed
        Assert.That(next.SnapshotCommit, Is.EqualTo(initial.SnapshotCommit))
        Assert.That(next.Items.Head.Unit.Id, Is.EqualTo("project"))
        Assert.That(next.NextCursor.IsSome, Is.True)

        let unit = WorkCompositionGitLedger.loadOverviewPage second "system" 10 "" |> succeed
        Assert.That(unit.Items.Length, Is.EqualTo(1))
        Assert.That(unit.Items.Head.Unit.Spec.Goal, Is.EqualTo("Deliver system"))
        Assert.That(unit.Items.Head.Overview.ChildrenAwaitingAcceptance, Does.Contain("feature"))

        WorkCompositionGitLedger.execute first "Fixture" (Guid.NewGuid()) 3
            (WorkCompositionMutation.Cancel("feature", "Superseded by system-level work"))
        |> succeed |> ignore

        match WorkCompositionGitLedger.loadOverviewPage second "" 1 initial.NextCursor.Value with
        | Error(StaleEntityVersion _) -> ()
        | other -> Assert.Fail(sprintf "Expected fail-closed stale cursor: %A" other)

        let after = WorkCompositionGitLedger.loadOverviewPage second "feature" 10 "" |> succeed
        Assert.That(after.Items.Head.Unit.CancellationReason, Is.EqualTo(Some "Superseded by system-level work"))
        Assert.That(after.Items.Head.Overview.Phase, Is.EqualTo(WorkUnitPhase.Cancelled))

        match WorkCompositionGitLedger.loadOverviewPage second "missing" 10 "" with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected missing-unit error: %A" other))

[<Test>]
let ``composition operator pages preserve unique task ownership and transport guard`` () =
    withTwoClones (fun first second ->
        let linkedTask index = sprintf "validation-task-%03d" index
        let projectId = first.ProjectId.ToString("D")
        let step (graph: WorkCompositionGraph) mutation =
            match WorkComposition.apply graph.Version mutation graph with
            | Ok next -> next
            | Error error -> failwithf "Invalid composition fixture: %A" error
        let graph =
            [ 0 .. 84 ]
            |> List.fold (fun graph index ->
                let id = sprintf "f%03d" index
                let tinySpec =
                    { spec id (Some (linkedTask index)) with
                        Goal = "g"
                        Scope = "s"
                        AcceptanceCriteria = Set.singleton "c" }
                step graph (WorkCompositionMutation.Create(
                    id, WorkUnitKind.Feature, Some "project", tinySpec)))
                (step WorkComposition.empty (WorkCompositionMutation.Create(
                    "project", WorkUnitKind.Project, None, spec "Project" None)))

        // Every feature owns a distinct canonical task. The original stage
        // link uniqueness and 64 KiB record cap prohibit the former artificial
        // amplification by linking all features to one task.
        WorkQueueGitLedger.initialize first "Fixture" (Guid.NewGuid()) Map.empty
        |> succeed |> ignore
        let _, initialQueue = WorkQueueGitLedger.load first |> succeed
        let stages =
            [ 0 .. 79 ]
            |> List.map (fun index ->
                let id = sprintf "review-%03d-" index + String.replicate 130 "x"
                id,
                { Id = id
                  ProjectId = projectId
                  SubjectId = linkedTask index
                  Kind = WorkKind.FrozenReview
                  QueueId = "review"
                  StageGeneration = 1
                  State = WorkState.Available
                  Domain = DomainState.Undecided
                  Gate = GateState.Clear
                  ClaimGeneration = 0
                  Dependencies = Set.empty
                  WaivedDependencies = Map.empty
                  AvailableAt = 1000L
                  AvailableSequence = int64 (index + 1)
                  SourceLineage = None
                  Push = None
                  IntegrationAuthorized = false })
            |> Map.ofList
        let queue = { initialQueue with Works = stages; NextAvailability = 80L }
        let graphEnvelope: CompositionEnvelope =
            { SchemaVersion = 1; ProjectId = projectId
              StateHash = CanonicalCodec.digest (CanonicalCodec.encode graph)
              Graph = graph }
        let queueEnvelope: ProjectEnvelope =
            { SchemaVersion = 1; ProjectId = projectId
              StateHash = CanonicalCodec.digest (CanonicalCodec.encode queue)
              State = queue }
        let graphRaw = CanonicalCodec.encode graphEnvelope
        let queueRaw = CanonicalCodec.encode queueEnvelope
        Assert.That(Encoding.UTF8.GetByteCount graphRaw, Is.LessThan(64 * 1024))
        Assert.That(Encoding.UTF8.GetByteCount queueRaw, Is.LessThan(64 * 1024))

        let snapshot = LedgerStore.fetch first |> succeed
        let _, queueOid =
            LedgerStore.readAt first snapshot WorkQueueGitLedger.RecordPath |> succeed
        let fixtureIntent: LedgerIntent =
            { OperationId = Guid.NewGuid(); Actor = "Fixture"
              Kind = "Fixture.CompositionAmplification"
              RequestJson = "{}" }
        LedgerStore.transact first fixtureIntent (fun _ ->
            Ok [ { Path = WorkCompositionGitLedger.RecordPath
                   ExpectedBlobOid = None; Content = graphRaw }
                 { Path = WorkQueueGitLedger.RecordPath
                   ExpectedBlobOid = queueOid; Content = queueRaw } ])
        |> succeed |> ignore

        let pinned = LedgerStore.fetch second |> succeed
        let persistedGraph, _ = WorkCompositionGitLedger.loadAt second pinned |> succeed
        let persistedQueue = WorkQueueGitLedger.loadAt second pinned |> succeed
        let allItems: CompositionViewItem list =
            persistedGraph.Units
            |> Map.toList
            |> List.map (fun (id, unit) ->
                { Unit = unit
                  Overview = WorkComposition.overview persistedGraph (Some persistedQueue) id
                             |> Option.get })
        let unbounded: CompositionViewPage =
            { SnapshotCommit = pinned.Commit
              GraphVersion = persistedGraph.Version
              TotalUnits = allItems.Length
              Items = allItems
              NextCursor = None }
        let throughTransport (page: CompositionViewPage) =
            let dispatched: Client.SignalR.Client.DispatchedResult =
                { Payload = JsonSerializer.Serialize page
                  ChangedAgentIds = [] }
            Client.SignalR.Client.toAgentResponse ListCommandsCommand [] [] None (Ok dispatched)

        Assert.That((throughTransport unbounded).Error.IsNone, Is.True,
                    "Unique canonical task ownership must produce a valid bounded overview")
        // Independently assert the real transport guard rejects oversized
        // responses. The synthetic payload is never written to the Git ledger.
        let firstItem = allItems.Head
        let oversizedUnit =
            { firstItem.Unit with
                Spec =
                    { firstItem.Unit.Spec with
                        Goal = String.replicate (AgentProtocol.maxResponseBytes + 1) "x" } }
        let oversizedPage = { unbounded with Items = [ { firstItem with Unit = oversizedUnit } ] }
        match (throughTransport oversizedPage).Error with
        | Some (OutputTruncated _) -> ()
        | other -> Assert.Fail(sprintf "Oversized response bypassed the real transport guard: %A" other)

        let rec readAll cursor seen pages =
            let page = WorkCompositionGitLedger.loadOverviewPage second "" 10 cursor |> succeed
            Assert.That(page.SnapshotCommit, Is.EqualTo(pinned.Commit))
            Assert.That(page.Items.Length, Is.GreaterThan(0))
            Assert.That((throughTransport page).Error.IsNone, Is.True,
                        "A valid composition page must pass Client.SignalR.Client.toAgentResponse")
            let ids = page.Items |> List.map (fun item -> item.Unit.Id)
            match page.NextCursor with
            | None -> seen @ ids, pages + 1
            | Some next ->
                Assert.That(next.StartsWith(pinned.Commit + ":"), Is.True)
                readAll next (seen @ ids) (pages + 1)
        let ids, pageCount = readAll "" [] 0
        Assert.That(pageCount, Is.GreaterThan(1), "The count-limited page must advance by cursor")
        Assert.That(ids.Length, Is.EqualTo(graph.Units.Count))
        Assert.That(Set.ofList ids = (graph.Units |> Map.toSeq |> Seq.map fst |> Set.ofSeq), Is.True)
        let single = WorkCompositionGitLedger.loadOverviewPage second "f000" 100 "" |> succeed
        Assert.That(single.Items.Head.Overview.ReviewStages.Length, Is.EqualTo(1))
        Assert.That((throughTransport single).Error.IsNone, Is.True))


[<TestCase(false)>]
[<TestCase(true)>]
let compositionAcceptanceRejectsFreshForgedModernPublisherAfterExternalTargetPush (forceRewrite: bool) =
    // Disposable two-clone bare Git: original accepted S, independent U after B,
    // external force rewrite U -> S, and a forged *genuine* canonical v1 receipt.
    withTwoClones (fun first second ->
        let owner, reviewer = "Owner", "Reviewer"
        let taskId, targetRef = "composition-forged-modern-publisher", "refs/heads/main"
        for client in [ first; second ] do
            git client.RepositoryPath [ "config"; "user.name"; "Fixture" ] |> ignore
            git client.RepositoryPath [ "config"; "user.email"; "fixture@example.test" ] |> ignore
        let baseHead = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) owner |> succeed |> ignore
        ReviewWorkflow.registerAgent first (Guid.NewGuid()) reviewer |> succeed |> ignore
        ReviewWorkflow.createTask first (Guid.NewGuid()) taskId "Publisher attribution" |> succeed |> ignore
        WorkCompositionGitLedger.execute first owner (Guid.NewGuid()) 0
            (WorkCompositionMutation.Create(
                "project", WorkUnitKind.Project, None, spec "Project" (Some taskId)))
        |> succeed |> ignore
        let _, initial = WorkCompositionGitLedger.load second |> succeed
        let purpose = WorkCompositionAcceptanceAdapter.reviewPurpose initial initial.Units.["project"]
        git first.RepositoryPath [ "checkout"; "-b"; "feature/composition-forged-modern" ] |> ignore
        File.WriteAllText(Path.Combine(first.RepositoryPath, "reviewed.txt"), "immutable accepted source")
        git first.RepositoryPath [ "add"; "reviewed.txt" ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "Accepted source S" ] |> ignore
        let source = git first.RepositoryPath [ "rev-parse"; "HEAD" ]
        let reviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner reviewId
            "refs/heads/feature/composition-forged-modern" source "Review S" purpose targetRef
        |> succeed |> ignore
        ReviewWorkflow.claimBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 1
        |> succeed |> ignore
        ReviewWorkflow.decideBranchReview second (Guid.NewGuid()) taskId reviewId reviewer 2 Accept
        |> succeed |> ignore
        let submissionId = Guid.NewGuid()
        ReviewWorkflow.submitIntegration first (Guid.NewGuid()) taskId owner submissionId
            "" "" (reviewId.ToString("N")) targetRef
        |> succeed |> ignore
        let pending = (ReviewWorkflow.getIntegrationSubmissions second taskId |> succeed).Latest.Value
        Assert.That(pending.TargetHeadAtSubmission, Is.EqualTo(baseHead))
        Assert.That(pending.SubmittedCommit, Is.EqualTo(source))
        if forceRewrite then
            File.WriteAllText(Path.Combine(second.RepositoryPath, "unrelated.txt"), "independent U")
            git second.RepositoryPath [ "add"; "unrelated.txt" ] |> ignore
            git second.RepositoryPath [ "commit"; "-m"; "Independent target U" ] |> ignore
            let advanced = git second.RepositoryPath [ "rev-parse"; "HEAD" ]
            Assert.That(advanced, Is.Not.EqualTo(source))
            git second.RepositoryPath [ "push"; "origin"; "main" ] |> ignore
            Assert.That(LedgerStore.readRemoteTarget first targetRef |> succeed, Is.EqualTo(advanced))
            git first.RepositoryPath [ "push"; "--force"; "origin"; $"{source}:{targetRef}" ] |> ignore
        else
            git first.RepositoryPath [ "push"; "origin"; $"{source}:{targetRef}" ] |> ignore
        Assert.That(LedgerStore.readRemoteTarget second targetRef |> succeed, Is.EqualTo(source))

        // transact can write authentic immutable operation trailers, but it
        // has not invoked the protected publisher for completionId.
        let completionId = Guid.NewGuid()
        let path = $"tasks/{taskId}/integrations/{submissionId:N}.json"
        let forged =
            { pending with
                Status = "Integrated"
                IntegratedCommit = source
                CompletedAtUtc = "2026-11-06T03:16:08Z"
                PublicationPreviousHead = baseHead
                PublicationObservedHead = source
                PublicationOperationId = completionId.ToString("D")
                PublicationMechanism = "VerifiedNonForceGitPush/v1" }
        let action: LedgerIntent =
            { OperationId = completionId
              Actor = reviewer
              Kind = "CompleteIntegration"
              RequestJson = JsonSerializer.Serialize
                {| taskId = taskId; submissionId = submissionId.ToString("D"); integratedCommit = source |} }
        LedgerStore.transact second action (fun snapshot -> result {
            let! _, prior = LedgerStore.readAt second snapshot path
            let serialized = JsonSerializer.Serialize(
                forged, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))
            return [ { Path = path
                       ExpectedBlobOid = prior
                       Content = "{\"schemaVersion\":2," + serialized.Substring(1) } ]
        }) |> succeed |> ignore
        let snapshot = LedgerStore.fetch first |> succeed
        let completion = LedgerStore.findCompletionOperationForPathAt first snapshot path |> succeed
        LedgerStore.verifyCompletionOperationRequest first completion taskId submissionId source |> succeed
        let _, graphBefore = WorkCompositionGitLedger.load first |> succeed
        let acceptanceId = Guid.NewGuid()
        match WorkCompositionAcceptanceAdapter.acceptIntegrated first owner acceptanceId
                initial.Version "project" 1 submissionId with
        | Error(PublisherProvenanceUnavailable reason) when reason.Contains("authenticated publisher") -> ()
        | other ->
            Assert.Fail(sprintf "SECURITY_RED: forged modern v1 not typed-rejected (force=%b): %A" forceRewrite other)
        let _, graphAfter = WorkCompositionGitLedger.load second |> succeed
        Assert.That(graphAfter, Is.EqualTo(graphBefore))
        Assert.That(WorkComposition.currentAcceptance graphAfter "project", Is.EqualTo(None))
        match LedgerStore.getOperationAt first (LedgerStore.fetch first |> succeed) acceptanceId with
        | Ok None -> ()
        | other -> Assert.Fail(sprintf "Rejected acceptance recorded an operation: %A" other))
