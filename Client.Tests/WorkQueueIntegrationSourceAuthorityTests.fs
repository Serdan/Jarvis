module Client.Tests.WorkQueueIntegrationSourceAuthorityTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkQueueGitLedger
open Client.WorkQueueReceiptProducer
open Client.WorkQueueIntegrationSourceAuthority
open Client.WorkQueueReservationPermissions

type private IntegrationSourceFixture =
    { A: LedgerBinding
      B: LedgerBinding
      Source: LedgerBinding
      Bare: string
      WorkId: string
      ReviewWorkId: string
      ReviewRef: string
      ReviewedSha: string
      BaseSha: string
      TargetRef: string
      SourceRef: string
      LaunchId: string
      Bearer: string
      Tree: string
      Operator: string }

[<TestFixture>]
type WorkQueueIntegrationSourceAuthorityTests() =
    let ok = function
        | Ok value -> value
        | Error e -> failwithf "Original integration proof fixture failed: %A" e

    let deny = function
        | Error _ -> ()
        | Ok _ -> Assert.Fail("Unverified Integration source operation was authorized.")

    let git cwd command =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- cwd
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        p.StartInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
        command |> List.iter p.StartInfo.ArgumentList.Add
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        Assert.That(p.WaitForExit(30000), Is.True)
        Assert.That(p.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let mutate (binding: LedgerBinding) now mutation =
        let _, current = WorkQueueGitLedger.load binding |> ok
        WorkQueueGitLedger.executeInertFixtureAt binding "integration-fixture"
            (Guid.NewGuid()) current.Version now mutation |> ok |> ignore

    let baseLaunch (binding: LedgerBinding) launch role =
        { Id = launch; ProjectId = binding.ProjectId.ToString("D")
          RoleId = role; RoleVersion = 1
          AccountTokenId = Guid.NewGuid().ToString("D")
          State = LaunchState.ReservedUnregistered; Lease = None; Version = 0
          Credit = None; PrimaryWorkId = None; EverClaimed = false
          AncillaryMessages = Set.empty; DispatchCount = 0 }

    let withClones action =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq11-integration-source-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root |> string) |> ignore
        try
            let bare = Path.Combine(root,"remote.git")
            let aPath,bPath = Path.Combine(root,"a"),Path.Combine(root,"b")
            git root ["init";"--bare";"--initial-branch=main";bare] |> ignore
            git root ["clone";bare;aPath] |> ignore
            git aPath ["config";"user.name";"Fixture"] |> ignore
            git aPath ["config";"user.email";"fixture@example.test"] |> ignore
            File.WriteAllText(Path.Combine(aPath,"source.txt"),"review base")
            git aPath ["add";"source.txt"] |> ignore
            git aPath ["commit";"-m";"baseline"] |> ignore
            git aPath ["push";"-u";"origin";"HEAD"] |> ignore
            let baseSha = git aPath ["rev-parse";"HEAD"]
            git aPath ["checkout";"-b";"source-reviewed"] |> ignore
            File.WriteAllText(Path.Combine(aPath,"source.txt"),"reviewed exact source")
            git aPath ["add";"source.txt"] |> ignore
            git aPath ["commit";"-m";"reviewed source"] |> ignore
            let reviewedSha = git aPath ["rev-parse";"HEAD"]
            git aPath ["push";"-u";"origin";"HEAD"] |> ignore
            git root ["clone";bare;bPath] |> ignore
            let targetRef = "refs/heads/main"
            let sourceRef = "refs/heads/source-reviewed"
            let id, ledger = Guid.NewGuid(),Guid.NewGuid()
            let binding path : LedgerBinding =
                { RepositoryPath = path; RemoteName = "origin"
                  ApprovedFetchUrl = bare; ApprovedPushUrl = bare
                  ProjectId = id; LedgerId = ledger; Epoch = 1 }
            let a,b = binding aPath,binding bPath
            LedgerStore.initialize a true |> ok |> ignore
            WorkQueueGitLedger.initialize a "fixture" (Guid.NewGuid())
                (Map.ofList ["Reviewer",set ["review"]; "Integrator",set ["integration"]])
                |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Owner" |> ok |> ignore
            ReviewWorkflow.registerAgent a (Guid.NewGuid()) "Reviewer" |> ok |> ignore

            let task = "integration-original"
            ReviewWorkflow.createTask a (Guid.NewGuid()) task task |> ok |> ignore
            let reviewId = Guid.NewGuid()
            let submittedReview =
                ReviewWorkflow.submitBranchReview a (Guid.NewGuid()) task "Owner" reviewId
                    sourceRef reviewedSha "Review exact source" "task-bound" targetRef |> ok
            let originalReview = (ReviewWorkflow.getBranchReview a task reviewId |> ok).Review
            let reviewSourceOperation = Guid.Parse submittedReview.Operation.OperationId
            WQ04ProducerFixture.prepare b reviewSourceOperation
            let reviewPreview = WorkQueueReceiptProducer.preview b reviewSourceOperation |> ok
            Assert.That(reviewPreview.Status, Is.EqualTo("ReadyToProduce"))
            WQ04ProducerFixture.produce b "fixture" (Guid.NewGuid())
                reviewSourceOperation |> ok |> ignore

            // The canonical WQ02 review prerequisite is actually Completed,
            // rather than a caller-supplied boolean permitting the integration.
            let reviewerLaunch = Guid.NewGuid().ToString("D")
            WQ04ProducerFixture.admitPinnedReviewFixture a "fixture" (Guid.NewGuid())
                (baseLaunch a reviewerLaunch "Reviewer") reviewPreview.StageId
            mutate b 101L (DispatchOnce(reviewerLaunch, DispatchState.Submitted))
            mutate a 102L (InitializeAgent(reviewerLaunch,"fixture-only-noncredentialed-review"))
            mutate b 103L (ClaimNext reviewerLaunch)
            let _, claimedReview = WorkQueueGitLedger.load a |> ok
            Assert.That(claimedReview.Works.[reviewPreview.StageId].State,
                        Is.EqualTo(WorkState.Claimed reviewerLaunch))
            ReviewWorkflow.claimBranchReview a (Guid.NewGuid()) task reviewId "Reviewer" 1
                |> ok |> ignore
            let decided =
                ReviewWorkflow.decideBranchReview b (Guid.NewGuid()) task reviewId
                    "Reviewer" 2 Accept |> ok
            let _, beforeFinalizing = WorkQueueGitLedger.load a |> ok
            let originalHeadBeforeDecision = LedgerStore.fetch b |> ok
            // A self-declared Accepted verdict without the original
            // DecideBranchReview receipt cannot terminalize WQ02 Review.
            WorkQueueIntegrationSourceAuthority.finalizeAcceptedReviewFixtureAt
                b "fixture" (Guid.NewGuid()) beforeFinalizing.Version 104L
                reviewerLaunch reviewPreview.StageId 1 reviewId (Guid.NewGuid()) |> deny
            Assert.That((LedgerStore.fetch b |> ok).Commit,
                        Is.EqualTo(originalHeadBeforeDecision.Commit))
            WorkQueueIntegrationSourceAuthority.finalizeAcceptedReviewFixtureAt
                b "fixture" (Guid.NewGuid()) beforeFinalizing.Version 104L
                reviewerLaunch reviewPreview.StageId 1 reviewId
                (Guid.Parse decided.Operation.OperationId)
                |> ok |> ignore
            mutate a 105L (CompleteOrdinary(reviewerLaunch, reviewPreview.StageId))
            let integrationId = Guid.NewGuid()
            let submitted = ReviewWorkflow.submitIntegration a (Guid.NewGuid()) task "Owner" integrationId
                                sourceRef reviewedSha (reviewId.ToString("N")) targetRef |> ok
            let integrationOp = Guid.Parse submitted.Operation.OperationId
            WQ04ProducerFixture.prepare b integrationOp
            let preview = WorkQueueReceiptProducer.preview b integrationOp |> ok
            Assert.That(preview.Status, Is.EqualTo("ReadyToProduce"))
            WQ04ProducerFixture.produce b "fixture" (Guid.NewGuid()) integrationOp |> ok |> ignore
            let _, before = WorkQueueGitLedger.load a |> ok
            let stage = before.Works.[preview.StageId]
            Assert.That(stage.Kind, Is.EqualTo(WorkKind.Integration))
            Assert.That(stage.State, Is.EqualTo(WorkState.Available))
            Assert.That(stage.IntegrationAuthorized, Is.False)
            Assert.That(stage.SourceLineage.IsNone, Is.True)
            match WorkQueueIntegrationSourceAuthority.inspectAt a (LedgerStore.fetch a |> ok) stage with
            | IntegrationSourceAuthorityDecision.Verified lineage ->
                Assert.That(lineage.ReachableRef, Is.EqualTo(Some originalReview.ReviewRef))
                Assert.That(lineage.ContinuationSha, Is.EqualTo(Some reviewedSha))
                Assert.That(lineage.TargetRef, Is.EqualTo(targetRef))
            | other -> Assert.Fail(sprintf "Original Accepted stage was not proven: %A" other)

            let launch = Guid.NewGuid().ToString("D")
            let credential = IssuedWorkCredential.Issue launch
            mutate a 110L (AdmitCredentialProject(baseLaunch a launch "Integrator", credential.VerifierSha256))
            mutate b 111L (DispatchOnce(launch, DispatchState.Submitted))
            let tree = sprintf "wq03-%s-%s-g1" preview.StageId launch
            let checkout = Path.Combine(root,tree)
            git aPath ["worktree";"add";"--detach";checkout;reviewedSha] |> ignore
            let source = { a with RepositoryPath = checkout }
            let bearer = credential.RevealForPrivateDelivery()
            let _, prior = WorkQueueGitLedger.load b |> ok
            WorkQueueGitLedger.executeCredentialFixtureAt b "fixture" (Guid.NewGuid())
                prior.Version 115L launch bearer CredentialWorkAction.FirstConnect
                |> ok |> ignore
            let fixture =
                { A = a; B = b; Source = source; Bare = bare
                  WorkId = preview.StageId; ReviewWorkId = reviewPreview.StageId
                  ReviewRef = originalReview.ReviewRef
                  ReviewedSha = reviewedSha; BaseSha = baseSha; TargetRef = targetRef
                  SourceRef = sourceRef; LaunchId = launch; Bearer = bearer; Tree = tree
                  Operator = "operator:" + String.replicate 64 "a" }
            action fixture
        finally
            try Directory.Delete(root,true) with _ -> ()

    let scope f version revoked =
        { LaunchId = f.LaunchId; ExpectedRevision = version; ExpectedWorkId = f.WorkId
          ReadProjectIds = Set.singleton (f.A.ProjectId.ToString("D"))
          Families = if revoked then Set.empty
                     else set [ReservationOperationFamily.ContextRead; ReservationOperationFamily.Integration]
          SourceWorktree = if revoked then None else Some f.Tree
          ExpiresAtUnixMs = 5000L; Revoked = revoked
          OperatorApprovalId = if revoked then "operator-revocation" else "operator-approved-wq05-source" }

    let approval f =
        WorkQueueReservationPermissions.reviseWithSourceAt f.A (Some f.Source)
            f.Operator (Guid.NewGuid()) 120L (scope f 0 false) |> ok |> ignore

    let permission f family revision =
        let _, project = WorkQueueGitLedger.load f.B |> ok
        let launch = project.Launches.[f.LaunchId]
        { LaunchId = f.LaunchId; TargetProjectId = f.A.ProjectId.ToString("D")
          TargetWorkId = f.WorkId; Family = family
          SourceRef = Some f.ReviewRef; SourceSha = Some f.ReviewedSha
          TargetRef = Some f.TargetRef
          SourceRepositoryId = Some(WorkQueueGitLedger.sourceRepositoryId f.A)
          SourceClaimGeneration = Some 1; SourceWorktree = Some f.Tree
          ExpectedRevision = revision; ExpectedLaunchVersion = launch.Version
          ExpectedLeaseGeneration = launch.Lease.Value.Generation }

    [<Test>]
    member _.OriginalAcceptedSourceAndDestinationAreDistinctAndClaimUsesSameCas() =
        withClones (fun f ->
            let originalHead, original = WorkQueueGitLedger.load f.A |> ok
            let stage = original.Works.[f.WorkId]
            Assert.That(stage.SourceLineage.IsNone, Is.True)
            approval f
            let authorizedRequest = permission f ReservationOperationFamily.Integration 1
            let grant = WorkQueueReservationPermissions.checkSourceAt f.B f.Source 130L f.Bearer authorizedRequest |> ok
            Assert.That(grant.SourceRef, Is.EqualTo(Some f.ReviewRef))
            Assert.That(grant.SourceSha, Is.EqualTo(Some f.ReviewedSha))
            Assert.That(grant.TargetRef, Is.EqualTo(Some f.TargetRef))
            Assert.That(grant.SourceClaimGeneration, Is.EqualTo(Some 1))
            Assert.That(grant.SourceCheckoutPath, Is.EqualTo(Some f.Source.RepositoryPath))
            Assert.That(grant.SourceRef, Is.Not.EqualTo(grant.TargetRef))
            WorkQueueReservationPermissions.checkAt f.A 131L f.Bearer authorizedRequest |> deny
            let _, before = WorkQueueGitLedger.load f.A |> ok
            WorkQueueReservationPermissions.claimSourceFixtureAt f.Source "fixture"
                (Guid.NewGuid()) before.Version 132L f.Bearer authorizedRequest 1 1 |> ok |> ignore
            let _, after = WorkQueueGitLedger.load f.B |> ok
            Assert.That(after.Works.[f.WorkId].State, Is.EqualTo(WorkState.Claimed f.LaunchId))
            Assert.That(after.Works.[f.WorkId].ClaimGeneration, Is.EqualTo(1))
            Assert.That(after.Works.[f.WorkId].SourceLineage.IsNone, Is.True)
            // Claim increments the canonical launch generation: the old
            // preclaim grant MUST stop working until a fresh operator revision.
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 133L f.Bearer
                authorizedRequest |> deny
            WorkQueueReservationPermissions.reviseWithSourceAt f.B (Some f.Source)
                f.Operator (Guid.NewGuid()) 134L
                {scope f 1 false with OperatorApprovalId="operator-reapproved-live-claim"}
                |> ok |> ignore
            let liveRequest = permission f ReservationOperationFamily.Integration 2
            let liveGrant =
                WorkQueueReservationPermissions.checkSourceAt f.A f.Source 135L f.Bearer
                    liveRequest |> ok
            Assert.That(liveGrant.SourceClaimGeneration, Is.EqualTo(Some 1))
            Assert.That(liveGrant.LaunchVersionAtApproval, Is.EqualTo(after.Launches.[f.LaunchId].Version))
            Assert.That(after.Version, Is.GreaterThan(original.Version))
            Assert.That(originalHead.Commit, Is.Not.EqualTo((LedgerStore.fetch f.B |> ok).Commit)))

    [<Test>]
    member _.ValidForeignWorktreeWrongRefTargetShaCredentialAndDirtyOriginalAreDenied() =
        withClones (fun f ->
            approval f
            let good = permission f ReservationOperationFamily.Integration 1
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 130L f.Bearer good |> ok |> ignore
            let secondRoot = Path.Combine(Path.GetDirectoryName(f.B.RepositoryPath),"other-source")
            Directory.CreateDirectory secondRoot |> ignore
            let foreignPath = Path.Combine(secondRoot,f.Tree)
            git f.B.RepositoryPath ["worktree";"add";"--detach";foreignPath;f.ReviewedSha] |> ignore
            let foreign = { f.B with RepositoryPath = foreignPath }
            WorkQueueReservationPermissions.checkSourceAt f.B foreign 131L f.Bearer good |> deny
            let unregisteredRoot = Path.Combine(Path.GetDirectoryName(f.B.RepositoryPath), "unlinked")
            Directory.CreateDirectory(unregisteredRoot) |> ignore
            let unregisteredPath = Path.Combine(unregisteredRoot,f.Tree)
            git unregisteredRoot ["clone";f.Bare;unregisteredPath] |> ignore
            git unregisteredPath ["checkout";"--detach";f.ReviewedSha] |> ignore
            let unregistered = {f.B with RepositoryPath = unregisteredPath}
            WorkQueueReservationPermissions.checkSourceAt f.B unregistered 131L f.Bearer good |> deny
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 131L f.Bearer
                {good with LaunchId = Guid.NewGuid().ToString("D")} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 132L f.Bearer
                {good with SourceRef=Some f.SourceRef} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 133L f.Bearer
                {good with SourceSha=Some f.BaseSha} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 134L f.Bearer
                {good with TargetRef=Some f.ReviewRef} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 135L f.Bearer
                {good with SourceClaimGeneration=Some 2} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 136L f.Bearer
                {good with SourceRepositoryId=Some(String.replicate 64 "f")} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 137L
                ((IssuedWorkCredential.Issue f.LaunchId).RevealForPrivateDelivery()) good |> deny
            File.WriteAllText(Path.Combine(f.Source.RepositoryPath,"source.txt"),"modified after original review")
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 138L f.Bearer good |> deny
            git f.Source.RepositoryPath ["checkout";"--";"source.txt"] |> ignore
            git f.Source.RepositoryPath ["checkout";"--detach";f.BaseSha] |> ignore
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 139L f.Bearer good |> deny
            git f.Source.RepositoryPath ["checkout";"--detach";f.ReviewedSha] |> ignore
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 140L f.Bearer good |> ok |> ignore)

    [<Test>]
    member _.RevisionReplayStaleProjectAndRevocationCannotEscalate() =
        withClones (fun f ->
            let originalOperation = Guid.NewGuid()
            let first = scope f 0 false
            WorkQueueReservationPermissions.reviseWithSourceAt f.A (Some f.Source)
                f.Operator originalOperation 120L first |> ok |> ignore
            let beforeReplay = LedgerStore.fetch f.B |> ok
            WorkQueueReservationPermissions.reviseWithSourceAt f.B (Some f.Source)
                f.Operator originalOperation 120L first |> ok |> ignore
            let afterReplay = LedgerStore.fetch f.B |> ok
            Assert.That(afterReplay.Commit, Is.EqualTo(beforeReplay.Commit))
            WorkQueueReservationPermissions.reviseWithSourceAt f.A (Some f.Source)
                f.Operator originalOperation 120L
                {first with OperatorApprovalId="forged-different-command"} |> deny
            let request = permission f ReservationOperationFamily.Integration 1
            let beforeVersion = (WorkQueueGitLedger.load f.B |> ok |> snd).Version
            let _, beforeMutation = WorkQueueGitLedger.load f.A |> ok
            let template = beforeMutation.Works.[f.WorkId]
            let otherStage =
                { template with Id = "unrelated-stage"; SubjectId = "unrelated-task"
                                Kind = WorkKind.Planning; QueueId = "planning"
                                Dependencies = Set.empty; SourceLineage = None }
            mutate f.A 141L (PublishStage otherStage)
            let attemptClaim = WorkQueueReservationPermissions.claimSourceFixtureAt
                                f.Source "fixture" (Guid.NewGuid()) beforeVersion 142L f.Bearer request 1 1
            deny attemptClaim
            let revocation = scope f 1 true
            WorkQueueReservationPermissions.reviseWithSourceAt f.B None f.Operator
                (Guid.NewGuid()) 150L revocation |> ok |> ignore
            WorkQueueReservationPermissions.checkSourceAt f.B f.Source 151L f.Bearer
                {request with ExpectedRevision=2} |> deny
            WorkQueueReservationPermissions.checkSourceAt f.A f.Source 152L f.Bearer request |> deny)
