module Client.Tests.WorkQueueReviewIntegrationAdapterTests

open System
open System.Diagnostics
open System.IO
open NUnit.Framework
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Client.ReviewWorkflow
open Client.WorkQueueReviewIntegrationAdapter
open Client.LedgerStore
open Client.WorkQueueGitLedger

module private Wq05Fixture =
    let source = String.replicate 40 "a"
    let baseSha = String.replicate 40 "b"
    let remoteCommit = String.replicate 40 "c"
    let taskId = "wq05-task"
    let reviewId = "29c11bb5cc0541419034c6398c15d621"
    let submissionId = "ea0539b1966543ffae09a9f0c1c11fbd"

    let succeed = function
        | Ok item -> item
        | Error e -> failwithf "Expected success, received %A" e

    let review status : BranchReview =
        { ReviewId = reviewId
          TaskId = taskId
          AttemptId = "attempt-1"
          OwnerAgentId = "Worker"
          Kind = "Independent"
          TaskRef = "refs/heads/feat/wq05-source"
          ReviewRef = sprintf "refs/heads/jarvis-review/%s/%s/r2" taskId reviewId
          SourceVersion = 2
          ReviewedHeadOid = source
          BaseCommit = baseSha
          TargetRef = "refs/heads/main"
          Status = status
          ReviewerId = if status = "Unclaimed" then null else "Reviewer"
          Version = if status = "Unclaimed" then 2 else 4
          CreatedSequence = 11L
          DecisionSequence = if status = "Accepted" || status = "ChangesRequested" then 12L else 0L
          Acknowledged = false }

    let integration : IntegrationSubmission =
        { SubmissionId = submissionId
          TaskId = taskId
          ActorId = "Worker"
          SourceRef = "refs/heads/feat/wq05-source"
          SubmittedCommit = source
          ReviewId = reviewId
          ReviewRef = sprintf "refs/heads/jarvis-review/%s/%s/r2" taskId reviewId
          ReviewVersion = 2
          ReviewedHeadOid = source
          IntegrationRef = "refs/heads/jarvis-integration/" + submissionId
          TargetHeadAtSubmission = baseSha
          TargetRef = "refs/heads/main"
          Status = "Pending"
          CreatedSequence = 13L
          IntegratedCommit = null
          CompletedAtUtc = null
          PublicationPreviousHead = null; PublicationObservedHead = null
          PublicationOperationId = null; PublicationMechanism = null; SharedBinding = None }

    let subscriptions =
        Map.ofList [
            "Integrator", Set.singleton "integration"
            "Reviewer", Set.singleton "review"
            "Lead", Set.singleton "planning" ]

    let stage projectId id queue kind =
        { Id = id
          ProjectId = projectId
          SubjectId = taskId
          Kind = kind
          QueueId = queue
          StageGeneration = 1
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = Set.empty
          WaivedDependencies = Map.empty
          AvailableAt = 0L
          AvailableSequence = 0L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }

    let launch projectId id role : Launch =
        { Id = id
          ProjectId = projectId
          RoleId = role
          RoleVersion = 1
          AccountTokenId = "token-" + id
          State = LaunchState.ReservedUnregistered
          Lease = None
          Version = 0
          Credit = None
          PrimaryWorkId = None
          EverClaimed = false
          AncillaryMessages = Set.empty
          DispatchCount = 0 }

    let publish (p: Project) (work: WorkStage) =
        Contracts.publishStage p.Version ("publish:" + work.Id) work p |> succeed
    let activate (p: Project) id role =
        let p = Contracts.admitProject p.Version ("admit:" + id) (launch p.Works.[p.Works |> Map.toSeq |> Seq.head |> fst].ProjectId id role) p |> succeed
        let p = Contracts.dispatchOnce p.Version ("dispatch:" + id) id DispatchState.Submitted p |> succeed
        let p = Contracts.initializeAgent p.Version ("init:" + id) id ("session:" + id) ("uuid:" + id) 0L p |> succeed
        Contracts.claimNext p.Version ("claim:" + id) id p |> succeed

    let accepted () =
        let review = review "Accepted" |> WorkQueueReviewIntegrationAdapter.bindReview |> succeed
        let binding = WorkQueueReviewIntegrationAdapter.bindAcceptedIntegration review integration |> succeed
        review, binding

    let activeIntegration () =
        let review, binding = accepted()
        let reviewWork =
            (WorkQueueReviewIntegrationAdapter.planFrozenReview "project"
                "review" Set.empty { review with Status = "Unclaimed"; ReviewerId = ""; DecisionSequence = 0L }
             |> succeed).Work
        let reviewed =
            { reviewWork with State = WorkState.Completed
                              Domain = DomainState.FinalizedPendingTerminal "canonical-verdict" }
        // Synthetic pure-contract fixture ONLY. Production WQ05 is not permitted
        // to publish this stage before WQ04 same-CAS authoritative binding.
        let integrationWork =
            { stage "project" (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                    "integration" WorkKind.Integration with
                Dependencies = Set.singleton (WorkQueueReviewIntegrationAdapter.reviewStageId review) }
        let p = emptyProject subscriptions |> fun p -> publish p reviewed |> fun p -> publish p integrationWork
        let p = activate p "integration-launch" "Integrator"
        binding, p

    let git dir arguments =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- dir
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        arguments |> List.iter p.StartInfo.ArgumentList.Add
        Assert.That(p.Start(), Is.True)
        let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        p.WaitForExit()
        Assert.That(p.ExitCode, Is.EqualTo(0), error)
        output.Trim()

    let withTwoClients test =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-wq05-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            let remote = Path.Combine(root, "remote.git")
            let first = Path.Combine(root, "first")
            let second = Path.Combine(root, "second")
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; first ] |> ignore
            git first [ "config"; "user.name"; "WQ05 Test" ] |> ignore
            git first [ "config"; "user.email"; "wq05@example.test" ] |> ignore
            File.WriteAllText(Path.Combine(first, "source.txt"), "two-client fixture")
            git first [ "add"; "source.txt" ] |> ignore
            git first [ "commit"; "-m"; "initial" ] |> ignore
            git first [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            git root [ "clone"; remote; second ] |> ignore
            let projectId = Guid.NewGuid()
            let ledgerId = Guid.NewGuid()
            let binding dir : LedgerBinding =
                { RepositoryPath = dir
                  RemoteName = "origin"
                  ApprovedFetchUrl = remote
                  ApprovedPushUrl = remote
                  ProjectId = projectId
                  LedgerId = ledgerId
                  Epoch = 1 }
            let one, two = binding first, binding second
            LedgerStore.initialize one true |> succeed |> ignore
            WorkQueueGitLedger.initialize one "wq05-test" (Guid.NewGuid()) subscriptions
            |> succeed |> ignore
            test one two
            Assert.That(git first [ "status"; "--porcelain" ], Is.Empty)
            Assert.That(git second [ "status"; "--porcelain" ], Is.Empty)
        finally
            try Directory.Delete(root, true) with _ -> ()

    // Independent disposable Git CAS model fixture: stores the exact ordered
    // contract inputs in two real clones. It does NOT represent WQ02/WQ03
    // production integration-push persistence or verified host receipts.
    let gateEventsPath = "wq05-gate-events.txt"

    let gitExit dir arguments =
        use p = new Process()
        p.StartInfo.FileName <- "git"
        p.StartInfo.WorkingDirectory <- dir
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true
        arguments |> List.iter p.StartInfo.ArgumentList.Add
        Assert.That(p.Start(), Is.True)
        let _output = p.StandardOutput.ReadToEnd()
        let _error = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode

    let beginGateModel (first: LedgerBinding) (second: LedgerBinding) =
        File.WriteAllText(Path.Combine(first.RepositoryPath, gateEventsPath), "BASE\n")
        git first.RepositoryPath [ "add"; gateEventsPath ] |> ignore
        git first.RepositoryPath [ "commit"; "-m"; "gate model base" ] |> ignore
        git first.RepositoryPath [ "push"; "origin"; "HEAD:refs/heads/main" ] |> ignore
        git second.RepositoryPath [ "fetch"; "origin"; "main" ] |> ignore
        git second.RepositoryPath [ "reset"; "--hard"; "origin/main" ] |> ignore

    let appendGateEvent (client: LedgerBinding) eventName =
        File.AppendAllText(Path.Combine(client.RepositoryPath, gateEventsPath), eventName + "\n")
        git client.RepositoryPath [ "add"; gateEventsPath ] |> ignore
        git client.RepositoryPath [ "commit"; "-m"; "gate model " + eventName + " from " + Path.GetFileName(client.RepositoryPath) ] |> ignore

    let gateEvents (client: LedgerBinding) =
        File.ReadAllLines(Path.Combine(client.RepositoryPath, gateEventsPath))
        |> Array.skip 1 |> Array.toList

    let pushGateModel (client: LedgerBinding) =
        gitExit client.RepositoryPath [ "push"; "origin"; "HEAD:refs/heads/main" ] = 0

    let refreshGateModel (client: LedgerBinding) =
        git client.RepositoryPath [ "fetch"; "origin"; "main" ] |> ignore
        git client.RepositoryPath [ "reset"; "--hard"; "origin/main" ] |> ignore

    let modelHead (client: LedgerBinding) =
        git client.RepositoryPath [ "rev-parse"; "HEAD" ]

    // Synthetic event-model command, pinned to the originating project/queue
    // high-water and exact downstream stage/claim generation. It is not a
    // production WQ04 claim envelope or a WQ03 host-dispatch receipt.
    let dClaimEvent op (p: Project) =
        let d = p.Works.["downstream-D"]
        sprintf "claim-D|%s|%d|%d|%d|%d|%d|%d"
            op p.Version p.GraphVersion p.NextAvailability
            d.StageGeneration d.ClaimGeneration d.AvailableSequence

[<TestFixture>]
type WorkQueueReviewIntegrationAdapterTests() =
    let success = Wq05Fixture.succeed

    [<Test>]
    member _.FrozenReviewRejectsMutableAndAlreadyDecidedSource() =
        let canonical = Wq05Fixture.review "Unclaimed"
        let bound = WorkQueueReviewIntegrationAdapter.bindReview canonical |> success
        let staged =
            WorkQueueReviewIntegrationAdapter.planFrozenReview "project" "review"
                (Set.singleton "prior-implementation") bound |> success
        Assert.That(staged.Work.Kind, Is.EqualTo(WorkKind.FrozenReview))
        Assert.That(staged.HardPrerequisites = Set.singleton "prior-implementation", Is.True)
        Assert.That(staged.Provenance.ReviewRef, Is.EqualTo(canonical.ReviewRef))
        Assert.That(staged.Provenance.SourceSha, Is.EqualTo(Wq05Fixture.source))
        match WorkQueueReviewIntegrationAdapter.bindReview
                { canonical with ReviewedHeadOid = "unreviewed-local-branch" } with
        | Error AdapterError.InvalidCanonicalReview -> ()
        | other -> Assert.Fail(sprintf "Unpinned source was admitted: %A" other)
        match WorkQueueReviewIntegrationAdapter.bindReview
                { canonical with ReviewRef = "refs/heads/mutable" } with
        | Error AdapterError.InvalidCanonicalReview -> ()
        | other -> Assert.Fail(sprintf "Mutable source ref was admitted: %A" other)
        let decided = Wq05Fixture.review "ChangesRequested"
                      |> WorkQueueReviewIntegrationAdapter.bindReview |> success
        match WorkQueueReviewIntegrationAdapter.planFrozenReview "project" "review" Set.empty decided with
        | Error AdapterError.ReviewNotUndecided -> ()
        | other -> Assert.Fail(sprintf "Decided review reentered the queue: %A" other)

    [<Test>]
    member _.CorrectionCannotCrossLineageOrPublishWithoutCanonicalJoin() =
        let review = Wq05Fixture.review "ChangesRequested"
                     |> WorkQueueReviewIntegrationAdapter.bindReview |> success
        let baseWork =
            { Wq05Fixture.stage "project" "original" "implementation" WorkKind.Implementation with
                State = WorkState.Completed
                StageGeneration = 2
                SourceLineage =
                    Some { RepositoryId = "source-repo"
                           TaskId = Wq05Fixture.taskId
                           TargetRef = "refs/heads/main"
                           LineageId = "attempt-1"
                           PinnedBaseCommit = Wq05Fixture.baseSha
                           ReachableRef = Some "refs/heads/feat/wq05-source"
                           ContinuationSha = Some Wq05Fixture.source
                           Generation = 2 } }
        let p = { emptyProject Wq05Fixture.subscriptions with
                    Works = Map.ofList [ "original", baseWork ] }
        let assertNoCorrection project source =
            match WorkQueueReviewIntegrationAdapter.planCorrection project "implementation" review source with
            | Error AdapterError.AuthoritativeJoinRequired
            | Error AdapterError.StaleSourceLineage -> ()
            | other -> Assert.Fail(sprintf "Unverified source-lineage correction was issued: %A" other)
        // Matching names and SHA are still caller-supplied, NOT current-ledger
        // identity and high-water proof.
        assertNoCorrection p baseWork
        let wrongSource =
            { baseWork with Id = "newer-implementation"
                            StageGeneration = 3
                            SourceLineage =
                                Some { baseWork.SourceLineage.Value with
                                        LineageId = "attempt-2"; Generation = 3
                                        ContinuationSha = Some Wq05Fixture.baseSha } }
        let replaced = { p with Works = p.Works.Add(wrongSource.Id, wrongSource) }
        assertNoCorrection replaced wrongSource
        assertNoCorrection replaced baseWork
        let wrongAttempt = { review with AttemptId = "attempt-previous"; SourceVersion = 1 }
        match WorkQueueReviewIntegrationAdapter.planCorrection replaced "implementation" wrongAttempt wrongSource with
        | Error AdapterError.AuthoritativeJoinRequired -> ()
        | other -> Assert.Fail(sprintf "Older review attempted to create correction: %A" other)
        let duplicateDifferentLineage =
            { wrongSource with Id = "old-correction"; StageGeneration = 4 }
        let duplicated = { replaced with Works = replaced.Works.Add(duplicateDifferentLineage.Id, duplicateDifferentLineage) }
        assertNoCorrection duplicated baseWork

    [<Test>]
    member _.AcceptedExactShaTargetAndImmutableSubmissionAreRequired() =
        let review, binding = Wq05Fixture.accepted()
        let staged =
            WorkQueueReviewIntegrationAdapter.planAcceptedIntegration "project" "integration"
                (Set.singleton "blocking-B") binding |> success
        Assert.That(staged.HardPrerequisites.Contains (WorkQueueReviewIntegrationAdapter.reviewStageId review), Is.True)
        Assert.That(staged.HardPrerequisites.Contains "blocking-B", Is.True)
        Assert.That(staged.ImmutableBinding, Is.EqualTo(binding))
        // Intent deliberately exposes no WorkStage that can lose provenance
        // when projected to the live WQ02/WQ04 project ledger.
        for bad in [
            { Wq05Fixture.integration with SubmittedCommit = Wq05Fixture.baseSha }
            { Wq05Fixture.integration with TargetRef = "refs/heads/other" }
            { Wq05Fixture.integration with IntegrationRef = "refs/heads/mutable" }
            { Wq05Fixture.integration with ReviewVersion = 1 }
            { Wq05Fixture.integration with ReviewedHeadOid = Wq05Fixture.baseSha } ] do
            match WorkQueueReviewIntegrationAdapter.bindAcceptedIntegration review bad with
            | Error AdapterError.InvalidCanonicalIntegration -> ()
            | other -> Assert.Fail(sprintf "Invalid immutable integration evidence admitted: %A" other)

    [<Test>]
    member _.PureContractGateHoldSeparatesObservedExternalEffectFromAuthorization() =
        let binding, p = Wq05Fixture.activeIntegration()
        let workId = WorkQueueReviewIntegrationAdapter.integrationStageId binding
        // Direct Contracts calls are disposable model tests, NOT a WQ05
        // authorized dispatch or a WQ03 host-journal receipt.
        let p =
            Contracts.startOriginalPush p.Version "original-intent"
                "integration-launch" workId "original-push" binding.SourceSha binding.TargetRef p |> success
        let p = Wq05Fixture.publish p (Wq05Fixture.stage "project" "blocking-B" "planning" WorkKind.Planning)
        let p = Contracts.setHardPrerequisite p.Version "insert-B" workId "blocking-B" 100L p |> success
        let stage = p.Works.[workId]
        Assert.That(stage.Gate, Is.EqualTo(GateState.IntegrationGateHold(p.GraphVersion, "original-push")))
        let observed : VerifiedRemoteEffect =
            { OriginalPushOperationId = "original-push"
              AcceptedSourceSha = binding.SourceSha
              TargetRef = binding.TargetRef
              RemoteCommitSha = Wq05Fixture.remoteCommit
              AncestryVerified = true
              JobsReconciled = true }
        let p =
            Contracts.reconcileExternalEffect p.Version "reconcile-original"
                "integration-launch" workId observed 101L p |> success
        let held = p.Works.[workId]
        Assert.That(held.State, Is.EqualTo(WorkState.GateException))
        Assert.That(held.Domain, Is.EqualTo(DomainState.Undecided))
        Assert.That(held.IntegrationAuthorized, Is.False)
        Assert.That(p.ProducerCount, Is.EqualTo(0))
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration p binding, Is.False)
        match Contracts.resolveIntegrationGateException p.Version "premature" workId None 102L p with
        | Error Rejection.MissingProof -> ()
        | other -> Assert.Fail(sprintf "Unsatisfied B was bypassed: %A" other)
        let waiver : VerifiedWaiverReceipt =
            { ReceiptId = "model-waiver-not-live-approval"
              WorkId = workId
              Dependencies = Set.singleton "blocking-B"
              AccountApprovalVersion = 2
              ApprovedByRole = "Lead"
              CanonicallyVerified = true }
        let resolved = Contracts.resolveIntegrationGateException p.Version "waive-B" workId (Some waiver) 103L p |> success
        Assert.That(resolved.ProducerCount, Is.EqualTo(1))
        Assert.That(resolved.Works.[workId].WaivedDependencies.["blocking-B"],
                    Is.EqualTo(waiver.ReceiptId))
        // A pure model's 'Integrated' result is never live canonical authority.
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration resolved binding, Is.False)
        match Contracts.resolveIntegrationGateException resolved.Version "repeat" workId (Some waiver) 104L resolved with
        | Error (Rejection.InvalidState _) -> ()
        | other -> Assert.Fail(sprintf "Gate finalized twice: %A" other)

    [<Test>]
    member _.UnknownPushCannotBeReconciledAsNoEffectOrDuplicate() =
        let binding, p = Wq05Fixture.activeIntegration()
        let p =
            Contracts.startOriginalPush p.Version "start"
                "integration-launch" (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                "push-original" binding.SourceSha binding.TargetRef p |> success
        let p = Wq05Fixture.publish p (Wq05Fixture.stage "project" "B" "planning" WorkKind.Planning)
        let p = Contracts.setHardPrerequisite p.Version "gate"
                    (WorkQueueReviewIntegrationAdapter.integrationStageId binding) "B" 20L p |> success
        let invalid : VerifiedRemoteEffect =
            { OriginalPushOperationId = "other-push"
              AcceptedSourceSha = binding.SourceSha
              TargetRef = binding.TargetRef
              RemoteCommitSha = Wq05Fixture.remoteCommit
              AncestryVerified = true
              JobsReconciled = true }
        match Contracts.reconcileExternalEffect p.Version "wrong-original" "integration-launch"
                (WorkQueueReviewIntegrationAdapter.integrationStageId binding) invalid 21L p with
        | Error Rejection.UnsafeExternalEffect -> ()
        | other -> Assert.Fail(sprintf "Unknown new push was admitted: %A" other)
        match Contracts.resolveDependencyGate p.Version "unsafe-noeffect"
                "integration-launch" (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                (SourceHandoffProof.ReadOnly "only-checkout") 22L p with
        | Error Rejection.UnsafeExternalEffect -> ()
        | other -> Assert.Fail(sprintf "Unknown in-flight push released: %A" other)

    [<Test>]
    member _.TwoGitClientsRaceOnOneVersionedHardEdgeAndReplaySameReceipt() =
        let review = Wq05Fixture.review "Unclaimed"
                     |> WorkQueueReviewIntegrationAdapter.bindReview |> success
        let proposal =
            WorkQueueReviewIntegrationAdapter.planFrozenReview
                "synthetic-project" "review" Set.empty review |> success
        // Positive graph/receipt semantics are a pure Contracts model, not an
        // authorized Git work ledger or evidence of WQ04 producer provenance.
        let seed =
            emptyProject Wq05Fixture.subscriptions
            |> fun p -> Wq05Fixture.publish p proposal.Work
            |> fun p -> Wq05Fixture.publish p
                            (Wq05Fixture.stage "synthetic-project" "B" "planning" WorkKind.Planning)
        let cloneA, cloneB = seed, seed
        let operation = "synthetic-hard-edge-receipt"
        let gated =
            Contracts.setHardPrerequisite cloneA.Version operation proposal.Work.Id "B" 10L cloneA
            |> success
        match Contracts.cancelUnclaimedStage cloneB.Version "synthetic-stale-cancel"
                proposal.Work.Id "stale-plan" 11L gated with
        | Error Rejection.CasConflict -> ()
        | other -> Assert.Fail(sprintf "Synthetic stale cancellation won the CAS: %A" other)
        let replay =
            Contracts.setHardPrerequisite cloneA.Version operation proposal.Work.Id "B" 10L gated
            |> success
        Assert.That(replay, Is.EqualTo(gated), "same synthetic operation replays the original receipt")
        Assert.That(gated.GraphVersion, Is.EqualTo(seed.GraphVersion + 1))
        Assert.That(gated.Works.[proposal.Work.Id].State, Is.EqualTo(WorkState.Blocked))
        Assert.That(gated.Works.[proposal.Work.Id].Dependencies = Set.singleton "B", Is.True)

        // On actual current main, two independent Git clones MUST NOT create
        // the unverified review/B stages, even with plausible IDs and same UUID.
        // The future canonical producer/source adapter must supply real proof;
        // never turn this negative into a fake positive Git CAS mutation.
        Wq05Fixture.withTwoClients (fun first second ->
            let remoteProjectId = first.ProjectId.ToString("D")
            let remoteReview = { proposal.Work with ProjectId = remoteProjectId }
            let remoteB = Wq05Fixture.stage remoteProjectId "B" "planning" WorkKind.Planning
            let _, beforeA = WorkQueueGitLedger.load first |> success
            let _, beforeB = WorkQueueGitLedger.load second |> success
            Assert.That(beforeA.Version, Is.EqualTo(beforeB.Version))
            let operation = Guid.NewGuid()
            let rejectUnverified = function
                | Error (LedgerIntegrityError reason)
                    when reason.Contains("WQ-04 stages require canonical producer authority",
                                         StringComparison.Ordinal) -> ()
                | other -> Assert.Fail(sprintf "Unverified WQ05 stage entered Git CAS: %A" other)
            rejectUnverified
                (WorkQueueGitLedger.execute first "wq05-test" operation beforeA.Version
                    (PublishStage remoteReview))
            rejectUnverified
                (WorkQueueGitLedger.execute second "wq05-test" operation beforeB.Version
                    (PublishStage remoteReview))
            rejectUnverified
                (WorkQueueGitLedger.execute first "wq05-test" (Guid.NewGuid()) beforeA.Version
                    (PublishStage remoteB))
            rejectUnverified
                (WorkQueueGitLedger.execute second "wq05-test" (Guid.NewGuid()) beforeB.Version
                    (PublishStage { remoteReview with Id = "legacy-no-prefix" }))
            let _, afterA = WorkQueueGitLedger.load first |> success
            let _, afterB = WorkQueueGitLedger.load second |> success
            Assert.That(afterA.Version, Is.EqualTo(beforeA.Version))
            Assert.That(afterB.Version, Is.EqualTo(beforeB.Version))
            Assert.That(afterA.GraphVersion, Is.EqualTo(beforeA.GraphVersion))
            Assert.That(afterA.Works, Is.Empty)
            Assert.That(afterB.Works, Is.Empty)
            Assert.That(afterA.Receipts = beforeA.Receipts, Is.True)
            Assert.That(afterB.Receipts = beforeB.Receipts, Is.True))

    [<Test>]
    member _.OppositeCASOrderForReviewVerdictAndNewHardEdgeIsFailClosed() =
        let projectId = "project"
        let reviewId = "frozen-review-r"
        let bId = "unsatisfied-B"
        let review = Wq05Fixture.stage projectId reviewId "review" WorkKind.FrozenReview
        let b = Wq05Fixture.stage projectId bId "planning" WorkKind.Planning
        let original =
            emptyProject Wq05Fixture.subscriptions
            |> fun p -> Wq05Fixture.publish p review
            |> fun p -> Wq05Fixture.publish p b
            |> fun p -> Wq05Fixture.activate p "review-launch" "Reviewer"
        Assert.That(original.Works.[reviewId].State, Is.EqualTo(WorkState.Claimed "review-launch"))
        // The canonical verdict wins the shared project/domain generation.
        // Its reviewer launch is still claimed, but Yield cannot reopen it.
        let decided = Contracts.decideReview original.Version "decide-first" reviewId "receipt-accepted" original |> success
        Assert.That(decided.Works.[reviewId].Domain, Is.EqualTo(DomainState.FinalizedPendingTerminal "receipt-accepted"))
        match Contracts.setHardPrerequisite decided.Version "late-edge"
                reviewId bId 50L decided with
        | Error Rejection.DomainAlreadyFinalized -> ()
        | other -> Assert.Fail(sprintf "Late edge retroactively invalidated review: %A" other)
        let decidedClaim = decided.Works.[reviewId]
        match Contracts.yieldReadOnly decided.Version "late-yield" "review-launch"
                reviewId decidedClaim.ClaimGeneration 51L decided with
        | Error Rejection.UnsafeExternalEffect -> ()
        | other -> Assert.Fail(sprintf "Decided review was requeued: %A" other)
        // Conversely, the hard edge wins first. A stale verdict cannot
        // publish a second producer, decide or release this still-claimed review.
        let gated = Contracts.setHardPrerequisite original.Version "gate-first"
                        reviewId bId 50L original |> success
        Assert.That(gated.Works.[reviewId].Gate,
                    Is.EqualTo(GateState.DependencyGatePending gated.GraphVersion))
        match Contracts.decideReview gated.Version "stale-verdict" reviewId "receipt-accepted" gated with
        | Error Rejection.DependencyGatePending -> ()
        | other -> Assert.Fail(sprintf "Gate-first allowed verdict: %A" other)
        match Contracts.decideReview original.Version "stale-cas" reviewId "receipt-accepted" gated with
        | Error Rejection.CasConflict -> ()
        | other -> Assert.Fail(sprintf "Old review CAS version succeeded: %A" other)
        Assert.That(gated.ProducerCount, Is.EqualTo(0))
        Assert.That(gated.Works.[reviewId].State, Is.EqualTo(WorkState.Claimed "review-launch"))

    [<Test>]
    member _.OriginalPushWithDefinitivelyNoEffectCanBlockButNeverAuthorizeIntegration() =
        let binding, initial = Wq05Fixture.activeIntegration()
        let launched =
            Contracts.startOriginalPush initial.Version "dispatch-once" "integration-launch"
                (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                "original-operation" binding.SourceSha binding.TargetRef initial |> success
        let b = Wq05Fixture.stage "project" "B" "planning" WorkKind.Planning
        let withB = Wq05Fixture.publish launched b
        let gated = Contracts.setHardPrerequisite withB.Version "add-hard-edge"
                        (WorkQueueReviewIntegrationAdapter.integrationStageId binding) "B" 33L withB |> success
        let unproven : DefinitiveNoEffectProof =
            { OriginalPushOperationId = "original-operation"
              DispatchedOperationTerminal = false
              JobsReconciled = true }
        match Contracts.recordDefinitiveNoEffect gated.Version "negative-tip-only"
                (WorkQueueReviewIntegrationAdapter.integrationStageId binding) unproven gated with
        | Error Rejection.UnsafeExternalEffect -> ()
        | other -> Assert.Fail(sprintf "Negative Git observation incorrectly proved no effect: %A" other)
        let proven = { unproven with DispatchedOperationTerminal = true }
        let recorded =
            Contracts.recordDefinitiveNoEffect gated.Version "verified-no-effect"
                (WorkQueueReviewIntegrationAdapter.integrationStageId binding) proven gated |> success
        let stopped =
            Contracts.resolveDependencyGate recorded.Version "terminal-original" "integration-launch"
                (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                (SourceHandoffProof.ReadOnly "verified-no-writer") 34L recorded |> success
        let stage = stopped.Works.[WorkQueueReviewIntegrationAdapter.integrationStageId binding]
        Assert.That(stage.State, Is.EqualTo(WorkState.Blocked))
        Assert.That(stage.Domain, Is.EqualTo(DomainState.Undecided))
        Assert.That(stage.IntegrationAuthorized, Is.False)
        Assert.That(stopped.ProducerCount, Is.EqualTo(0))
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration stopped binding, Is.False)

    [<Test>]
    member _.AlreadyIntegratedCanonicalSubmissionCannotCreateAnotherStage() =
        let review, _ = Wq05Fixture.accepted()
        let historical = { Wq05Fixture.integration with
                             Status = "Integrated"
                             IntegratedCommit = Wq05Fixture.source }
        let binding =
            WorkQueueReviewIntegrationAdapter.bindAcceptedIntegration review historical |> success
        match WorkQueueReviewIntegrationAdapter.planAcceptedIntegration "project"
                "integration" Set.empty binding with
        | Error AdapterError.InvalidCanonicalIntegration -> ()
        | other -> Assert.Fail(sprintf "Historical integrated submission produced work: %A" other)

    [<Test>]
    member _.SecondClientCancellationFirstCannotBeReopenedByStaleGate() =
        let review = Wq05Fixture.review "Unclaimed"
                     |> WorkQueueReviewIntegrationAdapter.bindReview |> success
        let staged =
            WorkQueueReviewIntegrationAdapter.planFrozenReview
                "synthetic-project" "review" Set.empty review |> success
        // Cancellation/gate ordering remains testable inside pure Contracts.
        // No canonical producer can yet persist this review stage on the real
        // WQ04 Git ledger; this is explicitly NOT live two-client authority.
        let seed =
            emptyProject Wq05Fixture.subscriptions
            |> fun p -> Wq05Fixture.publish p staged.Work
            |> fun p -> Wq05Fixture.publish p
                            (Wq05Fixture.stage "synthetic-project" "B" "planning" WorkKind.Planning)
        let cancelled =
            Contracts.cancelUnclaimedStage seed.Version "synthetic-cancel-first"
                staged.Work.Id "superseded" 100L seed |> success
        match Contracts.setHardPrerequisite seed.Version "synthetic-stale-gate"
                staged.Work.Id "B" 101L cancelled with
        | Error Rejection.CasConflict -> ()
        | other -> Assert.Fail(sprintf "Stale synthetic gate changed cancelled stage: %A" other)
        match Contracts.setHardPrerequisite cancelled.Version "synthetic-fresh-gate"
                staged.Work.Id "B" 102L cancelled with
        | Error Rejection.StageAlreadyTerminal -> ()
        | other -> Assert.Fail(sprintf "New synthetic gate reopened cancelled stage: %A" other)
        let replay =
            Contracts.cancelUnclaimedStage seed.Version "synthetic-cancel-first"
                staged.Work.Id "superseded" 100L cancelled |> success
        Assert.That(replay, Is.EqualTo(cancelled))
        Assert.That(cancelled.Works.[staged.Work.Id].State, Is.EqualTo(WorkState.Cancelled))
        Assert.That(cancelled.GraphVersion, Is.EqualTo(seed.GraphVersion))
        Assert.That(cancelled.ProducerCount, Is.EqualTo(seed.ProducerCount))

        // Two real clones cannot establish a review or a B stage by legacy
        // PublishStage, so neither cancellation nor a later gate can acquire a
        // legitimate stage to reopen. Failed attempts leave no Git receipt.
        Wq05Fixture.withTwoClients (fun first second ->
            let projectId = first.ProjectId.ToString("D")
            let remote = { staged.Work with ProjectId = projectId }
            let _, a = WorkQueueGitLedger.load first |> success
            let _, b = WorkQueueGitLedger.load second |> success
            Assert.That(a.Version, Is.EqualTo(b.Version))
            let rejectPublish = function
                | Error (LedgerIntegrityError reason)
                    when reason.Contains("WQ-04 stages require canonical producer authority",
                                         StringComparison.Ordinal) -> ()
                | other -> Assert.Fail(sprintf "Unverified review entered real Git ledger: %A" other)
            rejectPublish
                (WorkQueueGitLedger.execute first "wq05-test" (Guid.NewGuid()) a.Version
                    (PublishStage remote))
            rejectPublish
                (WorkQueueGitLedger.execute second "wq05-test" (Guid.NewGuid()) b.Version
                    (PublishStage (Wq05Fixture.stage projectId "B" "planning" WorkKind.Planning)))
            match WorkQueueGitLedger.execute second "wq05-test" (Guid.NewGuid()) b.Version
                    (CancelUnclaimed(remote.Id, "superseded")) with
            | Error _ -> ()
            | other -> Assert.Fail(sprintf "Cancelled a non-existent unverified review: %A" other)
            match WorkQueueGitLedger.execute first "wq05-test" (Guid.NewGuid()) a.Version
                    (SetHardPrerequisite(remote.Id, "B")) with
            | Error (LedgerIntegrityError reason)
                when reason.Contains("WQ-04 canonical source/index", StringComparison.Ordinal) -> ()
            | other -> Assert.Fail(sprintf "Unverified gate mutated real Git ledger: %A" other)
            let _, afterA = WorkQueueGitLedger.load first |> success
            let _, afterB = WorkQueueGitLedger.load second |> success
            Assert.That(afterA.Version, Is.EqualTo(a.Version))
            Assert.That(afterB.Version, Is.EqualTo(b.Version))
            Assert.That(afterA.Works, Is.Empty)
            Assert.That(afterB.Works, Is.Empty)
            Assert.That(afterA.GraphVersion, Is.EqualTo(a.GraphVersion))
            Assert.That(afterA.Receipts = a.Receipts, Is.True)
            Assert.That(afterB.Receipts = b.Receipts, Is.True))


    [<Test>]
    member _.EffectingAdaptersRejectEveryUnjoinedAcceptedIdentitySubstitution() =
        let binding, initial = Wq05Fixture.activeIntegration()
        let workId = WorkQueueReviewIntegrationAdapter.integrationStageId binding
        let evidence : VerifiedRemoteEffect =
            { OriginalPushOperationId = "same-original"
              AcceptedSourceSha = binding.SourceSha
              TargetRef = binding.TargetRef
              RemoteCommitSha = Wq05Fixture.remoteCommit
              AncestryVerified = true
              JobsReconciled = true }
        let variants = [
            binding
            { binding with SourceSha = Wq05Fixture.baseSha }
            { binding with TargetRef = "refs/heads/other" }
            { binding with ReviewId = Guid.NewGuid().ToString("N") }
            { binding with ReviewRef = "refs/heads/jarvis-review/forged" }
            { binding with ReviewVersion = binding.ReviewVersion + 1 }
            { binding with SourceRef = "refs/heads/other-source" }
            { binding with TargetHeadAtSubmission = Wq05Fixture.remoteCommit }
            { binding with IntegrationRef = "refs/heads/jarvis-integration/forged" }
        ]
        let check = function
            | Error AdapterError.AuthoritativeJoinRequired -> ()
            | other -> Assert.Fail(sprintf "Unjoined effecting API admitted source: %A" other)
        for candidate in variants do
            WorkQueueReviewIntegrationAdapter.startPinnedOriginalPush candidate initial.Version
                "model-start" "integration-launch" "same-original" initial |> check
            WorkQueueReviewIntegrationAdapter.addHardEdge initial.Version "model-edge"
                candidate "B" 100L initial |> check
            WorkQueueReviewIntegrationAdapter.observeOriginalPushUnderGate initial.Version
                "model-reconcile" "integration-launch" candidate evidence 101L initial |> check
            WorkQueueReviewIntegrationAdapter.resolveObservedGate initial.Version
                "model-resolve" candidate None 102L initial |> check
            Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration initial candidate, Is.False)
        // Even a manufactured Completed stage/observed effect cannot turn the
        // sidecar caller binding into a trusted accepted review identity.
        let manufactured =
            { initial.Works.[workId] with
                State = WorkState.Completed
                Domain = DomainState.Integrated Wq05Fixture.remoteCommit
                Push = Some { OperationId = "same-original"
                              AcceptedSourceSha = binding.SourceSha
                              TargetRef = binding.TargetRef
                              Outcome = PushOutcome.Observed Wq05Fixture.remoteCommit }
                IntegrationAuthorized = true }
        let changed = { initial with Works = initial.Works.Add(workId, manufactured) }
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration changed binding, Is.False)

    [<Test>]
    member _.TwoGitClientsPushIntentFirstGateSecondLossAndExactlyOnceResolution() =
        Wq05Fixture.withTwoClients (fun first second ->
            let binding, initial = Wq05Fixture.activeIntegration()
            let workId = WorkQueueReviewIntegrationAdapter.integrationStageId binding
            let d = { Wq05Fixture.stage "project" "downstream-D" "planning" WorkKind.Planning
                      with Dependencies = Set.singleton workId }
            let seed = initial
                       |> fun p -> Wq05Fixture.publish p (Wq05Fixture.stage "project" "B" "review" WorkKind.FrozenReview)
                       |> fun p -> Wq05Fixture.publish p d
            let evidence : VerifiedRemoteEffect =
                { OriginalPushOperationId = "one-original-push"
                  AcceptedSourceSha = binding.SourceSha
                  TargetRef = binding.TargetRef
                  RemoteCommitSha = Wq05Fixture.remoteCommit
                  AncestryVerified = true
                  JobsReconciled = true }
            let waiver : VerifiedWaiverReceipt =
                { ReceiptId = "synthetic-versioned-model-waiver"
                  WorkId = workId
                  Dependencies = Set.singleton "B"
                  AccountApprovalVersion = 9
                  ApprovedByRole = "Lead"
                  CanonicallyVerified = true }
            let evolve (p: Project) eventName =
                match eventName with
                | "intent" ->
                    Contracts.startOriginalPush p.Version "one-intent-op" "integration-launch"
                        workId "one-original-push" binding.SourceSha binding.TargetRef p |> success
                | "edge" -> Contracts.setHardPrerequisite p.Version "one-hard-edge"
                                workId "B" 110L p |> success
                | "ungated" -> Contracts.recordUngatedPushEffect p.Version "stale-observed"
                                    workId evidence p |> success
                | "finalize" -> Contracts.finalizeIntegration p.Version "stale-final"
                                    workId "stale-final-receipt" p |> success
                | "complete" -> Contracts.completeOrdinary p.Version "stale-complete"
                                    "integration-launch" workId 115L p |> success
                | "observed" -> Contracts.reconcileExternalEffect p.Version "one-reconcile"
                                    "integration-launch" workId evidence 120L p |> success
                | "waiver" -> Contracts.resolveIntegrationGateException p.Version
                                  "one-waiver-op" workId (Some waiver) 130L p |> success
                | "prepare-D" ->
                    // Model-only: demand for D is admitted after D becomes
                    // ready. The other available B belongs to the review queue.
                    let registered = Wq05Fixture.launch "project" "D-launch" "Lead"
                    let p = Contracts.admitProject p.Version "D-admit" registered p |> success
                    let p = Contracts.dispatchOnce p.Version "D-dispatch"
                                "D-launch" DispatchState.Submitted p |> success
                    Contracts.initializeAgent p.Version "D-register"
                        "D-launch" "D-session" "D-uuid" 116L p |> success
                | item when item.StartsWith("claim-D|", StringComparison.Ordinal) ->
                    match item.Split('|') with
                    | [| "claim-D"; op; expected; graph; queueHighWater; stageGen; claimGen; availability |] ->
                        let d = p.Works.["downstream-D"]
                        Assert.That(p.Version, Is.EqualTo(int expected), "stale project CAS generation")
                        Assert.That(p.GraphVersion, Is.EqualTo(int graph), "stale hard-edge graph generation")
                        Assert.That(p.NextAvailability, Is.EqualTo(int64 queueHighWater), "stale queue high-water")
                        Assert.That(d.StageGeneration, Is.EqualTo(int stageGen), "stale D stage generation")
                        Assert.That(d.ClaimGeneration, Is.EqualTo(int claimGen), "stale D claim generation")
                        Assert.That(d.AvailableSequence, Is.EqualTo(int64 availability), "stale D FIFO position")
                        Assert.That(d.State, Is.EqualTo(WorkState.Available), "D must be ready to claim")
                        let claimed = Contracts.claimNext (int expected) op "D-launch" p |> success
                        Assert.That(claimed.Works.["downstream-D"].State,
                                    Is.EqualTo(WorkState.Claimed "D-launch"))
                        claimed
                    | _ -> failwithf "Invalid exact-generation D claim event: %s" item
                | item -> failwithf "Unexpected model event: %s" item
            let load client : Project = Wq05Fixture.gateEvents client |> List.fold evolve seed
            Wq05Fixture.beginGateModel first second
            let beforeA = load first
            let beforeB = load second
            Assert.That(beforeA.Version, Is.EqualTo(beforeB.Version))
            let preRemoteHead = Wq05Fixture.modelHead second
            Wq05Fixture.appendGateEvent first "intent"
            Assert.That(Wq05Fixture.pushGateModel first, Is.True)
            let launched = load first
            Assert.That(launched.Works.[workId].Push.Value.OperationId, Is.EqualTo("one-original-push"))
            // A lost acknowledgement may not synthesize a different push ID.
            let replay = Contracts.startOriginalPush seed.Version "one-intent-op"
                            "integration-launch" workId "one-original-push"
                            binding.SourceSha binding.TargetRef launched |> success
            Assert.That(replay, Is.EqualTo(launched))
            // B is still on the old source Git head. Its edge cannot overwrite
            // A's durable original intent even though the local contract accepts.
            Wq05Fixture.appendGateEvent second "edge"
            Assert.That(Wq05Fixture.pushGateModel second, Is.False)
            Wq05Fixture.refreshGateModel second
            Assert.That(Wq05Fixture.modelHead second, Is.Not.EqualTo(preRemoteHead))
            let afterRefresh = load second
            Assert.That(afterRefresh.Version, Is.EqualTo(launched.Version))
            Wq05Fixture.appendGateEvent second "edge"
            Assert.That(Wq05Fixture.pushGateModel second, Is.True)
            let gated = load second
            Assert.That(gated.GraphVersion, Is.EqualTo(seed.GraphVersion + 1))
            Assert.That(gated.Works.[workId].Gate,
                        Is.EqualTo(GateState.IntegrationGateHold(gated.GraphVersion, "one-original-push")))
            Assert.That(gated.Works.[workId].IntegrationAuthorized, Is.False)
            // The old clone would locally finalize from stale no-gate data,
            // but its final authorization / downstream-ready CAS cannot land.
            Wq05Fixture.appendGateEvent first "ungated"
            Wq05Fixture.appendGateEvent first "finalize"
            Wq05Fixture.appendGateEvent first "complete"
            let staleCompleted = load first
            Assert.That(staleCompleted.Works.["downstream-D"].State, Is.EqualTo(WorkState.Available))
            // Actually admit and claim D against the stale ungated generation,
            // including a pinned queue/project high-water, before B wins Git CAS.
            Wq05Fixture.appendGateEvent first "prepare-D"
            let stalePrepared = load first
            let staleDGeneration = stalePrepared.Works.["downstream-D"].StageGeneration
            let staleHighWater = stalePrepared.NextAvailability
            Wq05Fixture.appendGateEvent first (Wq05Fixture.dClaimEvent "D-claim-stale" stalePrepared)
            let staleClaimed = load first
            Assert.That(staleClaimed.Works.["downstream-D"].State,
                        Is.EqualTo(WorkState.Claimed "D-launch"))
            Assert.That(staleClaimed.Works.["downstream-D"].ClaimGeneration, Is.EqualTo(1))
            Assert.That(staleClaimed.Launches.["D-launch"].PrimaryWorkId, Is.EqualTo(Some "downstream-D"))
            Assert.That(staleClaimed.GraphVersion, Is.LessThan(gated.GraphVersion))
            Assert.That(staleClaimed.NextAvailability, Is.EqualTo(staleHighWater))
            // The stale claim and admission never reach the authoritative Git
            // head. A readiness-only check would miss this CAS race.
            Assert.That(Wq05Fixture.pushGateModel first, Is.False)
            Wq05Fixture.refreshGateModel first
            Assert.That(load first, Is.EqualTo(gated))
            Assert.That(gated.Works.["downstream-D"].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(gated.Works.["downstream-D"].StageGeneration, Is.EqualTo(staleDGeneration))
            Assert.That(gated.Works.["downstream-D"].ClaimGeneration, Is.EqualTo(0))
            Assert.That(gated.Launches.ContainsKey "D-launch", Is.False)
            Assert.That(gated.ProducerCount, Is.EqualTo(0))
            match Contracts.claimNext stalePrepared.Version "D-claim-stale" "D-launch" gated with
            | Error Rejection.CasConflict -> ()
            | other -> Assert.Fail(sprintf "Stale D claim passed post-B project CAS: %A" other)
            let unsafeJobs = { evidence with JobsReconciled = false }
            let unsafeAncestry = { evidence with AncestryVerified = false }
            let wrongUuid = { evidence with OriginalPushOperationId = "second-push" }
            for bad in [ unsafeJobs; unsafeAncestry; wrongUuid ] do
                match Contracts.reconcileExternalEffect gated.Version "untrusted"
                        "integration-launch" workId bad 119L gated with
                | Error Rejection.UnsafeExternalEffect -> ()
                | other -> Assert.Fail(sprintf "Untrusted effect was accepted: %A" other)
            let missingTerminal : DefinitiveNoEffectProof =
                { OriginalPushOperationId = "one-original-push"
                  DispatchedOperationTerminal = false
                  JobsReconciled = false }
            match Contracts.recordDefinitiveNoEffect gated.Version "negative-tip"
                    workId missingTerminal gated with
            | Error Rejection.UnsafeExternalEffect -> ()
            | other -> Assert.Fail(sprintf "Unknown original push erased: %A" other)
            Wq05Fixture.appendGateEvent second "observed"
            Assert.That(Wq05Fixture.pushGateModel second, Is.True)
            let held = load second
            Assert.That(held.Works.[workId].State, Is.EqualTo(WorkState.GateException))
            Assert.That(held.Works.[workId].Gate,
                        Is.EqualTo(GateState.ExternalEffectObservedPendingGate(held.GraphVersion, "one-original-push", Wq05Fixture.remoteCommit)))
            Assert.That(held.ProducerCount, Is.EqualTo(0))
            Assert.That(held.Works.["downstream-D"].State, Is.EqualTo(WorkState.Blocked))
            match Contracts.resolveIntegrationGateException held.Version "too-early" workId None 125L held with
            | Error Rejection.MissingProof -> ()
            | other -> Assert.Fail(sprintf "Unsatisfied B authorized integration: %A" other)
            Wq05Fixture.refreshGateModel first
            Assert.That((load first).Version, Is.EqualTo(held.Version))
            // Both resolvers race with one verified *synthetic* waiver; only
            // one Git CAS can finalize. This is not host approval evidence.
            Wq05Fixture.appendGateEvent first "waiver"
            Wq05Fixture.appendGateEvent second "waiver"
            Assert.That(Wq05Fixture.pushGateModel second, Is.True)
            Assert.That(Wq05Fixture.pushGateModel first, Is.False)
            Wq05Fixture.refreshGateModel first
            let finalized = load first
            Assert.That(finalized.Works.[workId].IntegrationAuthorized, Is.True)
            Assert.That(finalized.Works.[workId].Domain, Is.EqualTo(DomainState.Integrated Wq05Fixture.remoteCommit))
            Assert.That(finalized.ProducerCount, Is.EqualTo(1))
            Assert.That(finalized.Works.[workId].WaivedDependencies.["B"], Is.EqualTo(waiver.ReceiptId))
            Assert.That(finalized.Works.["downstream-D"].State, Is.EqualTo(WorkState.Available))
            let idempotent = Contracts.resolveIntegrationGateException held.Version
                                "one-waiver-op" workId (Some waiver) 130L finalized |> success
            Assert.That(idempotent.ProducerCount, Is.EqualTo(1))
            Assert.That(idempotent, Is.EqualTo(finalized))
            // Positive counterpart: after a verified synthetic waiver, D can
            // be admitted, but two clients racing for the same current D
            // generation still produce exactly one canonical claim.
            Wq05Fixture.appendGateEvent first "prepare-D"
            Assert.That(Wq05Fixture.pushGateModel first, Is.True)
            Wq05Fixture.refreshGateModel second
            let readyA, readyB = load first, load second
            Assert.That(readyA, Is.EqualTo(readyB))
            Assert.That(readyA.Works.["downstream-D"].StageGeneration, Is.EqualTo(staleDGeneration))
            Assert.That(readyA.Works.["downstream-D"].ClaimGeneration, Is.EqualTo(0))
            Assert.That(readyA.Works.["downstream-D"].State, Is.EqualTo(WorkState.Available))
            Wq05Fixture.appendGateEvent first (Wq05Fixture.dClaimEvent "D-claim-fresh-A" readyA)
            Wq05Fixture.appendGateEvent second (Wq05Fixture.dClaimEvent "D-claim-fresh-B" readyB)
            Assert.That((load first).Works.["downstream-D"].ClaimGeneration, Is.EqualTo(1))
            Assert.That((load second).Works.["downstream-D"].ClaimGeneration, Is.EqualTo(1))
            Assert.That(Wq05Fixture.pushGateModel first, Is.True)
            Assert.That(Wq05Fixture.pushGateModel second, Is.False)
            Wq05Fixture.refreshGateModel second
            let claimed = load second
            Assert.That(claimed, Is.EqualTo(load first))
            Assert.That(claimed.Version, Is.EqualTo(readyA.Version + 1))
            Assert.That(claimed.GraphVersion, Is.EqualTo(readyA.GraphVersion))
            Assert.That(claimed.NextAvailability, Is.EqualTo(readyA.NextAvailability))
            Assert.That(claimed.Works.["downstream-D"].StageGeneration, Is.EqualTo(staleDGeneration))
            Assert.That(claimed.Works.["downstream-D"].ClaimGeneration, Is.EqualTo(1))
            Assert.That(claimed.Works.["downstream-D"].State,
                        Is.EqualTo(WorkState.Claimed "D-launch"))
            Assert.That(claimed.Launches.["D-launch"].PrimaryWorkId, Is.EqualTo(Some "downstream-D"))
            Assert.That(claimed.Launches.["D-launch"].EverClaimed, Is.True)
            Assert.That(claimed.ProducerCount, Is.EqualTo(1))
            Assert.That(claimed.Works.[workId].Push.Value.OperationId, Is.EqualTo("one-original-push"))
            let replayD = Contracts.claimNext readyA.Version "D-claim-fresh-A" "D-launch" claimed |> success
            Assert.That(replayD, Is.EqualTo(claimed), "original D claim UUID must replay its only receipt")
            match Contracts.claimNext readyB.Version "D-claim-fresh-B" "D-launch" claimed with
            | Error Rejection.CasConflict -> ()
            | other -> Assert.Fail(sprintf "Second stale D claim bypassed high-water: %A" other)
            Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration finalized binding, Is.False)
            Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration claimed binding, Is.False)
        )

    [<Test>]
    member _.TwoGitClientsGateFirstRejectsStaleAndFreshOriginalPushIntents() =
        Wq05Fixture.withTwoClients (fun first second ->
            let binding, initial = Wq05Fixture.activeIntegration()
            let workId = WorkQueueReviewIntegrationAdapter.integrationStageId binding
            let d = { Wq05Fixture.stage "project" "downstream-D" "planning" WorkKind.Planning
                      with Dependencies = Set.singleton workId }
            let seed =
                Wq05Fixture.publish initial
                    (Wq05Fixture.stage "project" "B" "review" WorkKind.FrozenReview)
                |> fun p -> Wq05Fixture.publish p d
            let replay (p: Project) eventName =
                match eventName with
                | "edge" -> Contracts.setHardPrerequisite p.Version "gate-first"
                                workId "B" 140L p |> success
                | "intent" -> Contracts.startOriginalPush p.Version "stale-intent"
                                  "integration-launch" workId "one-original-push"
                                  binding.SourceSha binding.TargetRef p |> success
                | item -> failwithf "Unexpected model event: %s" item
            let load client : Project = Wq05Fixture.gateEvents client |> List.fold replay seed
            Wq05Fixture.beginGateModel first second
            let old = load first
            Assert.That(old.Version, Is.EqualTo((load second).Version))
            Wq05Fixture.appendGateEvent second "edge"
            Assert.That(Wq05Fixture.pushGateModel second, Is.True)
            let remoteGated = load second
            Assert.That(remoteGated.Works.[workId].Gate,
                        Is.EqualTo(GateState.DependencyGatePending remoteGated.GraphVersion))
            Wq05Fixture.appendGateEvent first "intent"
            Assert.That(Wq05Fixture.pushGateModel first, Is.False)
            Wq05Fixture.refreshGateModel first
            let fresh = load first
            Assert.That(fresh, Is.EqualTo(remoteGated))
            match Contracts.startOriginalPush fresh.Version "new-original"
                    "integration-launch" workId "one-original-push"
                    binding.SourceSha binding.TargetRef fresh with
            | Error (Rejection.InvalidState _) -> ()
            | other -> Assert.Fail(sprintf "Gate-first admitted original push: %A" other)
            Assert.That(fresh.Works.[workId].Push, Is.EqualTo(None))
            Assert.That(fresh.Works.[workId].IntegrationAuthorized, Is.False)
            Assert.That(fresh.Works.["downstream-D"].State, Is.EqualTo(WorkState.Blocked))
            Assert.That(fresh.Works.["downstream-D"].ClaimGeneration, Is.EqualTo(0))
            Assert.That(fresh.Launches.ContainsKey "D-launch", Is.False)
            Assert.That(fresh.ProducerCount, Is.EqualTo(0))
            // With B's edge committed first there is no D demand admission at
            // all; an earlier project generation cannot claim it either.
            match Contracts.admitProject fresh.Version "gate-first-D-admit"
                    (Wq05Fixture.launch "project" "D-launch" "Lead") fresh with
            | Error Rejection.NoCompatibleMatching -> ()
            | other -> Assert.Fail(sprintf "Gate-first admitted blocked D: %A" other)
            match Contracts.claimNext old.Version "gate-first-stale-D-claim" "D-launch" fresh with
            | Error Rejection.CasConflict -> ()
            | other -> Assert.Fail(sprintf "Gate-first stale D claim escaped CAS: %A" other)
            Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration fresh binding, Is.False)
        )
