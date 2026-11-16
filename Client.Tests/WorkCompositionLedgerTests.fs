module Client.Tests.WorkCompositionLedgerTests

open System
open System.Diagnostics
open System.IO
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
        let selfReviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            selfReviewId sourceRef acceptedSha "Self-reviewed project"
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

        git first.RepositoryPath [ "checkout"; "main" ] |> ignore
        git first.RepositoryPath [ "merge"; "--ff-only"; acceptedSha ] |> ignore
        git first.RepositoryPath [ "push"; "origin"; "main" ] |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) taskId reviewer
            submissionId acceptedSha |> succeed |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) taskId reviewer
            selfSubmissionId acceptedSha |> succeed |> ignore
        match WorkCompositionAcceptanceAdapter.acceptIntegrated second owner
                  (Guid.NewGuid()) initial.Version "project" 1 selfSubmissionId with
        | Error(InvalidBinding _) -> ()
        | other -> Assert.Fail(sprintf "Expected same-author review rejection: %A" other)

        let acceptanceOp = Guid.NewGuid()
        let accepted =
            WorkCompositionAcceptanceAdapter.acceptIntegrated second owner acceptanceOp
                initial.Version "project" 1 submissionId |> succeed
        Assert.That(accepted.AlreadyApplied, Is.False)
        let duplicate =
            WorkCompositionAcceptanceAdapter.acceptIntegrated first owner acceptanceOp
                initial.Version "project" 1 submissionId |> succeed
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
        git first.RepositoryPath [ "push"; "origin"; "main" ] |> ignore
        ReviewWorkflow.completeIntegration second (Guid.NewGuid()) correctionTaskId reviewer
            correctionSubmissionId correctionSha |> succeed |> ignore
        WorkCompositionAcceptanceAdapter.acceptIntegrated second owner (Guid.NewGuid())
            withChild.Version "correction" 1 correctionSubmissionId |> succeed |> ignore
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
        let freshReviewId = Guid.NewGuid()
        ReviewWorkflow.submitBranchReview first (Guid.NewGuid()) taskId owner
            freshReviewId correctionRef correctionSha "Validate composed project outcome"
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
            freshSubmissionId correctionSha |> succeed |> ignore
        WorkCompositionAcceptanceAdapter.acceptIntegrated first owner (Guid.NewGuid())
            withAcceptedChild.Version "project" 1 freshSubmissionId |> succeed |> ignore
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
