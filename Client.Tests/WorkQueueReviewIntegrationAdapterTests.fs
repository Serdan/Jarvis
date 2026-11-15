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
          CompletedAtUtc = null }

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
        let integrationWork =
            (WorkQueueReviewIntegrationAdapter.planAcceptedIntegration "project"
                "integration" Set.empty binding |> succeed).Work
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
    member _.CorrectionIsOneCurrentLineageStageAfterChangesRequested() =
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
                           LineageId = "same-source"
                           PinnedBaseCommit = Wq05Fixture.baseSha
                           ReachableRef = None
                           ContinuationSha = None
                           Generation = 2 } }
        let p = { emptyProject Wq05Fixture.subscriptions with
                    Works = Map.ofList [ "original", baseWork ] }
        let first = WorkQueueReviewIntegrationAdapter.planCorrection p "implementation" review baseWork |> success
        Assert.That(first.Work.StageGeneration, Is.EqualTo(3))
        Assert.That(first.Work.Dependencies = Set.singleton (WorkQueueReviewIntegrationAdapter.reviewStageId review), Is.True)
        Assert.That(first.Work.SourceLineage.Value.Generation, Is.EqualTo(3))
        let alreadyActive = { p with Works = p.Works.Add(first.Work.Id, first.Work) }
        match WorkQueueReviewIntegrationAdapter.planCorrection alreadyActive "implementation" review baseWork with
        | Error AdapterError.DuplicateCorrection -> ()
        | other -> Assert.Fail(sprintf "Duplicate source-lineage correction was admitted: %A" other)

    [<Test>]
    member _.AcceptedExactShaTargetAndImmutableSubmissionAreRequired() =
        let review, binding = Wq05Fixture.accepted()
        let staged =
            WorkQueueReviewIntegrationAdapter.planAcceptedIntegration "project" "integration"
                (Set.singleton "blocking-B") binding |> success
        Assert.That(staged.Work.Dependencies.Contains (WorkQueueReviewIntegrationAdapter.reviewStageId review), Is.True)
        Assert.That(staged.Work.Dependencies.Contains "blocking-B", Is.True)
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
    member _.PostGateOriginalPushIsObservedNotAuthorizedUntilEdgeWaived() =
        let binding, p = Wq05Fixture.activeIntegration()
        let p =
            WorkQueueReviewIntegrationAdapter.startPinnedOriginalPush binding p.Version
                "original-intent" "integration-launch" "original-push" p |> success
        let p = Wq05Fixture.publish p (Wq05Fixture.stage "project" "blocking-B" "planning" WorkKind.Planning)
        let p =
            WorkQueueReviewIntegrationAdapter.addHardEdge p.Version "insert-B"
                binding "blocking-B" 100L p |> success
        let stage = p.Works.[WorkQueueReviewIntegrationAdapter.integrationStageId binding]
        Assert.That(stage.Gate, Is.EqualTo(GateState.IntegrationGateHold(p.GraphVersion, "original-push")))
        let observed : VerifiedRemoteEffect =
            { OriginalPushOperationId = "original-push"
              AcceptedSourceSha = binding.SourceSha
              TargetRef = binding.TargetRef
              RemoteCommitSha = Wq05Fixture.remoteCommit
              AncestryVerified = true
              JobsReconciled = true }
        let p =
            WorkQueueReviewIntegrationAdapter.observeOriginalPushUnderGate
                p.Version "reconcile-original" "integration-launch" binding observed 101L p |> success
        let held = p.Works.[WorkQueueReviewIntegrationAdapter.integrationStageId binding]
        Assert.That(held.State, Is.EqualTo(WorkState.GateException))
        Assert.That(held.Domain, Is.EqualTo(DomainState.Undecided))
        Assert.That(held.IntegrationAuthorized, Is.False)
        Assert.That(p.ProducerCount, Is.EqualTo(0))
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration p binding, Is.False)
        match WorkQueueReviewIntegrationAdapter.resolveObservedGate p.Version "premature"
                binding None 102L p with
        | Error (AdapterError.ContractRejected Rejection.MissingProof) -> ()
        | other -> Assert.Fail(sprintf "Unsatisfied B was bypassed: %A" other)
        let waiver : VerifiedWaiverReceipt =
            { ReceiptId = "canonical-approval-1"
              WorkId = WorkQueueReviewIntegrationAdapter.integrationStageId binding
              Dependencies = Set.singleton "blocking-B"
              AccountApprovalVersion = 2
              ApprovedByRole = "Lead"
              CanonicallyVerified = true }
        let resolved =
            WorkQueueReviewIntegrationAdapter.resolveObservedGate p.Version "waive-B"
                binding (Some waiver) 103L p |> success
        Assert.That(WorkQueueReviewIntegrationAdapter.isAuthorizedIntegration resolved binding, Is.True)
        Assert.That(resolved.ProducerCount, Is.EqualTo(1))
        Assert.That(resolved.Works.[WorkQueueReviewIntegrationAdapter.integrationStageId binding].WaivedDependencies.["blocking-B"],
                    Is.EqualTo(waiver.ReceiptId))
        match WorkQueueReviewIntegrationAdapter.resolveObservedGate resolved.Version "repeat"
                binding (Some waiver) 104L resolved with
        | Error AdapterError.UnsafeGateState -> ()
        | other -> Assert.Fail(sprintf "Gate finalized twice: %A" other)
        Assert.That(resolved.ProducerCount, Is.EqualTo(1))

    [<Test>]
    member _.UnknownPushCannotBeReconciledAsNoEffectOrDuplicate() =
        let binding, p = Wq05Fixture.activeIntegration()
        let p =
            WorkQueueReviewIntegrationAdapter.startPinnedOriginalPush binding p.Version
                "start" "integration-launch" "push-original" p |> success
        let p = Wq05Fixture.publish p (Wq05Fixture.stage "project" "B" "planning" WorkKind.Planning)
        let p = WorkQueueReviewIntegrationAdapter.addHardEdge p.Version "gate" binding "B" 20L p |> success
        let invalid : VerifiedRemoteEffect =
            { OriginalPushOperationId = "other-push"
              AcceptedSourceSha = binding.SourceSha
              TargetRef = binding.TargetRef
              RemoteCommitSha = Wq05Fixture.remoteCommit
              AncestryVerified = true
              JobsReconciled = true }
        match WorkQueueReviewIntegrationAdapter.observeOriginalPushUnderGate p.Version "wrong-original"
                "integration-launch" binding invalid 21L p with
        | Error (AdapterError.ContractRejected Rejection.UnsafeExternalEffect) -> ()
        | other -> Assert.Fail(sprintf "Unknown new push was admitted: %A" other)
        match Contracts.resolveDependencyGate p.Version "unsafe-noeffect"
                "integration-launch" (WorkQueueReviewIntegrationAdapter.integrationStageId binding)
                (SourceHandoffProof.ReadOnly "only-checkout") 22L p with
        | Error Rejection.UnsafeExternalEffect -> ()
        | other -> Assert.Fail(sprintf "Unknown in-flight push released: %A" other)

    [<Test>]
    member _.TwoGitClientsRaceOnOneVersionedHardEdgeAndReplaySameReceipt() =
        Wq05Fixture.withTwoClients (fun first second ->
            let review = Wq05Fixture.review "Unclaimed"
                         |> WorkQueueReviewIntegrationAdapter.bindReview |> success
            let proposal =
                WorkQueueReviewIntegrationAdapter.planFrozenReview
                    (first.ProjectId.ToString("D")) "review" Set.empty review |> success
            let mutate binding id mutation =
                let _, current = WorkQueueGitLedger.load binding |> success
                WorkQueueGitLedger.execute binding "wq05-test" id current.Version mutation
                |> success |> ignore
            mutate first (Guid.NewGuid()) (PublishStage proposal.Work)
            mutate first (Guid.NewGuid())
                (PublishStage (Wq05Fixture.stage (first.ProjectId.ToString("D")) "B" "planning" WorkKind.Planning))
            let _, beforeA = WorkQueueGitLedger.load first |> success
            let _, beforeB = WorkQueueGitLedger.load second |> success
            Assert.That(beforeA.Version, Is.EqualTo(beforeB.Version))
            let operation = Guid.NewGuid()
            WorkQueueGitLedger.execute first "wq05-test" operation beforeA.Version
                (SetHardPrerequisite (proposal.Work.Id, "B")) |> success |> ignore
            // Second clone had a valid prior snapshot; its different request
            // cannot beat the exact same project Git CAS head.
            match WorkQueueGitLedger.execute second "wq05-test" (Guid.NewGuid()) beforeB.Version
                    (CancelUnclaimed (proposal.Work.Id, "stale-plan")) with
            | Error (StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale clone changed review work: %A" other)
            let _, after = WorkQueueGitLedger.load second |> success
            let blocked = after.Works.[proposal.Work.Id]
            Assert.That(blocked.State, Is.EqualTo(WorkState.Blocked))
            Assert.That(blocked.Dependencies = Set.singleton "B", Is.True)
            Assert.That(after.GraphVersion, Is.EqualTo(1))
            // Same UUID operation is recovered from the immutable original receipt.
            WorkQueueGitLedger.execute second "wq05-test" operation beforeA.Version
                (SetHardPrerequisite (proposal.Work.Id, "B")) |> success |> ignore
            let _, replay = WorkQueueGitLedger.load first |> success
            Assert.That(replay.GraphVersion, Is.EqualTo(1))
            Assert.That(replay.Works.[proposal.Work.Id].State, Is.EqualTo(WorkState.Blocked)))

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
            WorkQueueReviewIntegrationAdapter.startPinnedOriginalPush binding initial.Version
                "dispatch-once" "integration-launch" "original-operation" initial |> success
        let b = Wq05Fixture.stage "project" "B" "planning" WorkKind.Planning
        let withB = Wq05Fixture.publish launched b
        let gated = WorkQueueReviewIntegrationAdapter.addHardEdge withB.Version
                        "add-hard-edge" binding "B" 33L withB |> success
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
        Wq05Fixture.withTwoClients (fun first second ->
            let review = Wq05Fixture.review "Unclaimed"
                         |> WorkQueueReviewIntegrationAdapter.bindReview |> success
            let staged =
                WorkQueueReviewIntegrationAdapter.planFrozenReview
                    (first.ProjectId.ToString("D")) "review" Set.empty review |> success
            let projectId = first.ProjectId.ToString("D")
            let mutate binding mutation =
                let _, project = WorkQueueGitLedger.load binding |> success
                WorkQueueGitLedger.execute binding "wq05-test" (Guid.NewGuid()) project.Version mutation
                |> success |> ignore
            mutate first (PublishStage staged.Work)
            mutate first (PublishStage (Wq05Fixture.stage projectId "B" "planning" WorkKind.Planning))
            let _, a = WorkQueueGitLedger.load first |> success
            let _, b = WorkQueueGitLedger.load second |> success
            Assert.That(a.Version, Is.EqualTo(b.Version))
            WorkQueueGitLedger.execute second "wq05-test" (Guid.NewGuid()) b.Version
                (CancelUnclaimed(staged.Work.Id, "superseded")) |> success |> ignore
            match WorkQueueGitLedger.execute first "wq05-test" (Guid.NewGuid()) a.Version
                    (SetHardPrerequisite(staged.Work.Id, "B")) with
            | Error (StaleEntityVersion _) -> ()
            | other -> Assert.Fail(sprintf "Stale gate changed cancelled stage: %A" other)
            let _, after = WorkQueueGitLedger.load first |> success
            Assert.That(after.Works.[staged.Work.Id].State, Is.EqualTo(WorkState.Cancelled))
            match WorkQueueGitLedger.execute first "wq05-test" (Guid.NewGuid()) after.Version
                    (SetHardPrerequisite(staged.Work.Id, "B")) with
            | Error (InvalidBinding _) -> ()
            | other -> Assert.Fail(sprintf "New gate reopened terminal stage: %A" other)
            let _, finalState = WorkQueueGitLedger.load second |> success
            Assert.That(finalState.Works.[staged.Work.Id].State, Is.EqualTo(WorkState.Cancelled))
            Assert.That(finalState.GraphVersion, Is.EqualTo(0)))
