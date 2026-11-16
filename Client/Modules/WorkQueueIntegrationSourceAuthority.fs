module Client.WorkQueueIntegrationSourceAuthority

open System
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Client.WorkQueueReviewIntegrationAdapter

// Disposable, default-off WQ11 adapter. Its only possible source is the
// canonical WQ04 integration stage and the original Accepted review plus
// exact-source submission. This does not authorize a push or a public route.
[<RequireQualifiedAccess>]
type IntegrationSourceAuthorityDecision =
    | Verified of SourceLineage
    | IntegrationAuthorityUnavailable of string

module WorkQueueIntegrationSourceAuthority =
    let private unavailable reason =
        Error(InvalidBinding("IntegrationAuthorityUnavailable: " + reason))

    let private require condition reason =
        if condition then Ok() else unavailable reason

    // Invoked inside the SAME verified WQ02 project Git snapshot as a grant
    // revision or Work claim. A caller-supplied stage, source ref or actor label
    // is never an independent attestation of integration permission.
    let private proveAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                        (stage: WorkStage) = result {
        let projectId = binding.ProjectId.ToString("D")
        do! require (stage.ProjectId = projectId
                     && stage.Kind = WorkKind.Integration
                     && stage.QueueId = "integration"
                     && stage.StageGeneration = 1
                     && stage.SourceLineage.IsNone
                     && stage.Push.IsNone
                     && not stage.IntegrationAuthorized
                     && stage.Domain = DomainState.Undecided
                     && stage.Gate = GateState.Clear
                     && (match stage.State with
                         | WorkState.Available | WorkState.Claimed _ -> true
                         | _ -> false))
                "No live, unfinalized original WQ04 Integration-stage generation."

        let! state = WorkQueueGitLedger.loadAt binding snapshot
        do! require (state.Works.TryFind stage.Id = Some stage)
                "Integration Work is not the current canonical WQ02 stage."
        let! stored = WorkQueueGitLedger.readOriginAt binding snapshot stage.Id
        let! origin =
            match stored with
            | Some (origin, _) when origin.SchemaVersion = 1
                                    && origin.ProjectId = projectId
                                    && origin.WorkId = stage.Id
                                    && origin.SourceOperationKind = "SubmitIntegration"
                                    && origin.AccountCreditStatus = "NotReserved"
                                    && origin.RoleId = ""
                                    && origin.RoleVersion = 0
                                    && origin.SourceHighWater < snapshot.Header.NextSequence ->
                Ok origin
            | _ -> unavailable "Original WQ04 SubmitIntegration stage/origin CAS receipt is absent."
        let! submissionId =
            match Guid.TryParse origin.SourceOperationId with
            | true, guid when guid <> Guid.Empty -> Ok guid
            | _ -> unavailable "Original integration operation ID is missing."
        let! original = LedgerStore.getOperationAt binding snapshot submissionId
        let! operation =
            match original with
            | Some item when item.Type = "SubmitIntegration"
                             && item.Sequence = origin.SourceOperationSequence
                             && item.Actor = origin.SourceOperationActor
                             && item.RequestHash = origin.SourceRequestHash
                             && (item.Paths |> Array.contains origin.SourceEntityPath) ->
                Ok item
            | _ -> unavailable "Original integration operation receipt differs from WQ04 provenance."
        let! introductionHead = LedgerStore.getOperationOriginAt binding snapshot operation
        do! require (introductionHead = origin.SourceOriginHead)
                "Original integration receipt has a different introducing commit."
        let! introduction = LedgerStore.verifiedHistoricalSnapshot binding introductionHead
        let! originalSubmissionId =
            let prefix = sprintf "tasks/%s/integrations/" stage.SubjectId
            if not (origin.SourceEntityPath.StartsWith(prefix, StringComparison.Ordinal)
                    && origin.SourceEntityPath.EndsWith(".json", StringComparison.Ordinal)) then
                unavailable "Integration receipt belongs to a different original task."
            else
                let suffix = origin.SourceEntityPath.Substring(prefix.Length)
                let key = suffix.Substring(0, suffix.Length - ".json".Length)
                match Guid.TryParseExact(key, "N") with
                | true, id when id <> Guid.Empty -> Ok id
                | _ -> unavailable "Integration receipt has malformed submission identity."
        let! originalSubmission =
            ReviewWorkflow.getIntegrationSubmissionAt binding introduction stage.SubjectId
                originalSubmissionId
        let! originalRaw, originalBlob =
            LedgerStore.readAt binding introduction origin.SourceEntityPath
        let originalReceiptIsExact = (
              originalSubmission.CreatedSequence = operation.Sequence
              && originalSubmission.SubmissionId = originalSubmissionId.ToString("N")
              && originalBlob = Some origin.SourceEntityBlobOid
              && (originalRaw |> Option.exists (fun raw ->
                  CanonicalCodec.digest raw = origin.SourceEntitySha256)))
        do! require originalReceiptIsExact "Integration submission or original entity blob differs from the WQ04 receipt."

        let! reviewId =
            match Guid.TryParseExact(originalSubmission.ReviewId, "N") with
            | true, guid when guid <> Guid.Empty -> Ok guid
            | _ -> unavailable "Integration submission has no original reviewed UUID."
        let! submission =
            ReviewWorkflow.getIntegrationSubmissionAt binding snapshot stage.SubjectId
                originalSubmissionId
        let! originalReview =
            ReviewWorkflow.getBranchReviewAt binding introduction stage.SubjectId reviewId
        let! review =
            ReviewWorkflow.getBranchReviewAt binding snapshot stage.SubjectId reviewId
        let! frozen =
            WorkQueueReviewIntegrationAdapter.bindReview review
            |> Result.mapError (fun _ ->
                InvalidBinding "IntegrationAuthorityUnavailable: Malformed frozen canonical review.")
        let! accepted =
            WorkQueueReviewIntegrationAdapter.bindAcceptedIntegration frozen submission
            |> Result.mapError (fun _ ->
                InvalidBinding "IntegrationAuthorityUnavailable: Accepted review and exact-source submission disagree.")

        let originalReviewId = reviewId.ToString("N")
        let reviewEvent =
            { ProjectId = projectId; SourceId = originalReviewId
              StageGeneration = review.SourceVersion
              Origin = QueueProducerOrigin.FrozenReviewRequested(
                  review.TaskId, originalReviewId, review.ReviewedHeadOid, review.ReviewRef)
              HardPrerequisites = Set.empty }
        let reviewStageId = WorkQueueProducers.workId reviewEvent
        // Completion of the hard review edge is NOT a caller assertion or an
        // operator waiver. Require the original WQ05 final-domain receipt
        // written into the WQ02 reviewed Work by its own verified CAS.
        let! reviewWork =
            match state.Works.TryFind reviewStageId with
            | Some completed when completed.Kind = WorkKind.FrozenReview
                                  && completed.SubjectId = review.TaskId
                                  && completed.State = WorkState.Completed
                                  && completed.StageGeneration = review.SourceVersion ->
                Ok completed
            | _ -> unavailable "Original WQ04 FrozenReview prerequisite is not terminal."
        let! decidedId =
            match reviewWork.Domain with
            | DomainState.FinalizedPendingTerminal receipt
                when receipt.StartsWith("wq05-accepted:", StringComparison.Ordinal) ->
                match Guid.TryParseExact(receipt.Substring("wq05-accepted:".Length), "N") with
                | true, id when id <> Guid.Empty -> Ok id
                | _ -> unavailable "Malformed original WQ05 review-terminal decision receipt."
            | _ -> unavailable "Original WQ05 Accepted final-domain receipt is missing."
        let! decision =
            LedgerStore.getOperationAt binding snapshot decidedId
            |> Result.bind (function
                | Some op when op.Type = "DecideBranchReview"
                               && op.Sequence = review.DecisionSequence
                               && op.Actor = review.ReviewerId -> Ok op
                | _ -> unavailable "WQ02 review terminal does not match original WQ05 Accepted decision.")
        let! originalReviewWork =
            WorkQueueGitLedger.readOriginAt binding snapshot reviewStageId
            |> Result.bind (function
                | Some (origin, _) when origin.SchemaVersion = 1
                                        && origin.WorkId = reviewStageId
                                        && origin.ProjectId = projectId
                                        && origin.SourceOperationKind = "SubmitBranchReview"
                                        && origin.SourceCommit = review.ReviewedHeadOid
                                        && origin.SourceRef = review.ReviewRef -> Ok origin
                | _ -> unavailable "FrozenReview terminal has no original WQ04 source receipt.")
        let integrationEvent =
            { ProjectId = projectId; SourceId = submission.SubmissionId
              StageGeneration = 1
              Origin = QueueProducerOrigin.AcceptedIntegration(
                  submission.TaskId, review.ReviewId, accepted.SourceSha, accepted.TargetRef)
              HardPrerequisites = Set.singleton reviewStageId }

        let acceptedIntegrationIsExact = (
              stage.Id = WorkQueueProducers.workId integrationEvent
              && stage.SubjectId = submission.TaskId
              && stage.Dependencies.Contains reviewStageId
              && not (stage.WaivedDependencies.ContainsKey reviewStageId)
              && review.Status = "Accepted"
              && originalReview.Status = "Accepted"
              && originalReview.ReviewId = review.ReviewId
              && originalReview.ReviewedHeadOid = review.ReviewedHeadOid
              && originalReview.DecisionSequence = review.DecisionSequence
              && originalReview.ReviewRef = review.ReviewRef
              && originalReview.TargetRef = review.TargetRef
              && review.ReviewerId <> review.OwnerAgentId
              && review.DecisionSequence > review.CreatedSequence
              && review.DecisionSequence < submission.CreatedSequence
              && submission.Status = "Pending"
              && submission.SubmissionId = originalSubmission.SubmissionId
              && submission.SourceRef = originalSubmission.SourceRef
              && submission.SubmittedCommit = originalSubmission.SubmittedCommit
              && submission.ReviewRef = originalSubmission.ReviewRef
              && submission.TargetRef = originalSubmission.TargetRef
              && submission.ReviewVersion = originalSubmission.ReviewVersion
              && origin.SourceRef = submission.SourceRef
              && origin.SourceCommit = accepted.SourceSha
              && accepted.SourceRef = review.TaskRef
              && accepted.SourceRef <> accepted.TargetRef
              && review.ReviewRef <> accepted.TargetRef)
        do! require acceptedIntegrationIsExact
                "Frozen Accepted review, original WQ05 submission, WQ04 stage, or source/target do not match."

        // The original WQ05 frozen review ref is a source ref, NOT the
        // integration destination. WQ03 verifies its actual remote tip,
        // linked physical checkout, HEAD, cleanliness and claim generation.
        return
            { RepositoryId = WorkQueueGitLedger.sourceRepositoryId binding
              TaskId = submission.TaskId
              TargetRef = accepted.TargetRef
              LineageId = "wq05-accepted-" + originalReviewId
              PinnedBaseCommit = review.BaseCommit
              ReachableRef = Some review.ReviewRef
              ContinuationSha = Some accepted.SourceSha
              Generation = review.SourceVersion }
    }

    let inspectAt binding snapshot stage =
        match proveAt binding snapshot stage with
        | Ok lineage -> IntegrationSourceAuthorityDecision.Verified lineage
        | Error reason ->
            IntegrationSourceAuthorityDecision.IntegrationAuthorityUnavailable(sprintf "%A" reason)

    // Callers must check again inside the winning grant/claim CAS.
    let internal verifiedStageAt binding snapshot stage =
        match proveAt binding snapshot stage with
        | Ok lineage -> Ok { stage with SourceLineage = Some lineage }
        | Error why -> Error why

    // The original WQ04 FrozenReview hard edge must become terminal before the
    // integration stage may be Available. Ordinary completion correctly denies
    // review stages without a final-domain receipt. This PRIVATE disposable
    // bridge supplies that receipt from the actual WQ05 Accepted decision only;
    // WQ02 performs the transition and verification in the SAME Git CAS.
    // It is not a public review-completion or Scheduler route.
    let internal finalizeAcceptedReviewFixtureAt
            (binding: LedgerBinding) actor (operationId: Guid) expectedVersion now
            launchId reviewWorkId expectedClaimGeneration (reviewId: Guid)
            (decisionOperationId: Guid) =
        if operationId = Guid.Empty || decisionOperationId = Guid.Empty
           || reviewId = Guid.Empty || String.IsNullOrWhiteSpace reviewWorkId
           || expectedClaimGeneration < 1 then
            unavailable "Original Accepted review, live claim and operation UUIDs are required."
        else
            let immutableReceipt = "wq05-accepted:" + decisionOperationId.ToString("N")
            let verifyAtCas snapshot (before: Project) (after: Project) = result {
                let projectId = binding.ProjectId.ToString("D")
                let! stage, owner =
                    match before.Works.TryFind reviewWorkId, before.Launches.TryFind launchId with
                    | Some stage, Some owner
                        when stage.ProjectId = projectId
                             && stage.Kind = WorkKind.FrozenReview
                             && stage.QueueId = "review"
                             && stage.State = WorkState.Claimed launchId
                             && stage.ClaimGeneration = expectedClaimGeneration
                             && stage.Gate = GateState.Clear
                             && stage.Domain = DomainState.Undecided
                             && owner.PrimaryWorkId = Some reviewWorkId ->
                        Ok(stage, owner)
                    | _ -> unavailable "FrozenReview was not claimed by the original live WQ02 execution."
                let! origin =
                    WorkQueueGitLedger.readOriginAt binding snapshot stage.Id
                    |> Result.bind (function
                        | Some (origin, _) when origin.SchemaVersion = 1
                                                && origin.ProjectId = projectId
                                                && origin.WorkId = stage.Id
                                                && origin.SourceOperationKind = "SubmitBranchReview"
                                                && origin.RoleId = ""
                                                && origin.AccountCreditStatus = "NotReserved" -> Ok origin
                        | _ -> unavailable "No original WQ04 immutable FrozenReview source receipt.")
                let! review = ReviewWorkflow.getBranchReviewAt binding snapshot stage.SubjectId reviewId
                let expectedEvent =
                    { ProjectId = projectId; SourceId = review.ReviewId
                      StageGeneration = review.SourceVersion
                      Origin = QueueProducerOrigin.FrozenReviewRequested(
                          review.TaskId, review.ReviewId, review.ReviewedHeadOid, review.ReviewRef)
                      HardPrerequisites = Set.empty }
                let originalFrozenStageMatches = (
                    stage.Id = WorkQueueProducers.workId expectedEvent
                    && stage.StageGeneration = review.SourceVersion
                    && stage.SubjectId = review.TaskId
                    && review.Status = "Accepted"
                    && review.DecisionSequence > review.CreatedSequence
                    && review.ReviewerId <> review.OwnerAgentId
                    && origin.SourceRef = review.ReviewRef
                    && origin.SourceCommit = review.ReviewedHeadOid)
                do! require originalFrozenStageMatches
                        "FrozenReview WQ04 origin and current Accepted WQ05 review differ."

                let! decision = LedgerStore.getOperationAt binding snapshot decisionOperationId
                let! decisionOperation =
                    match decision with
                    | Some decision when decision.Type = "DecideBranchReview"
                                         && decision.Actor = review.ReviewerId
                                         && decision.Sequence = review.DecisionSequence -> Ok decision
                    | _ -> unavailable "Original Accepted-review decision operation does not match."
                let! decisionHead = LedgerStore.getOperationOriginAt binding snapshot decisionOperation
                let! decidedSnapshot = LedgerStore.verifiedHistoricalSnapshot binding decisionHead
                let! atDecision =
                    ReviewWorkflow.getBranchReviewAt binding decidedSnapshot stage.SubjectId reviewId
                let originalDecisionMatches = (
                    atDecision.Status = "Accepted"
                    && atDecision.DecisionSequence = decisionOperation.Sequence
                    && atDecision.ReviewId = review.ReviewId
                    && atDecision.ReviewRef = review.ReviewRef
                    && atDecision.ReviewedHeadOid = review.ReviewedHeadOid
                    && atDecision.TargetRef = review.TargetRef
                    && atDecision.OwnerAgentId = review.OwnerAgentId
                    && atDecision.ReviewerId = review.ReviewerId)
                do! require originalDecisionMatches
                        "The original decision commit is not the same frozen Accepted review."

                let! submitId =
                    match Guid.TryParse origin.SourceOperationId with
                    | true, id when id <> Guid.Empty -> Ok id
                    | _ -> unavailable "Original review submission receipt UUID is malformed."
                let! submission = LedgerStore.getOperationAt binding snapshot submitId
                let! submitOperation =
                    match submission with
                    | Some item when item.Type = "SubmitBranchReview"
                                     && item.Sequence = origin.SourceOperationSequence
                                     && item.RequestHash = origin.SourceRequestHash
                                     && item.Actor = origin.SourceOperationActor
                                     && (item.Paths |> Array.contains origin.SourceEntityPath) ->
                        Ok item
                    | _ -> unavailable "WQ04 original review submission operation has changed."
                let! submittedHead = LedgerStore.getOperationOriginAt binding snapshot submitOperation
                do! require (submittedHead = origin.SourceOriginHead)
                        "WQ04 original review submission commit is different."
                let! submittedSnapshot =
                    LedgerStore.verifiedHistoricalSnapshot binding submittedHead
                let! atSubmission =
                    ReviewWorkflow.getBranchReviewAt binding submittedSnapshot stage.SubjectId reviewId
                let originalSubmissionMatches = (
                    atSubmission.Status = "Unclaimed"
                    && atSubmission.DecisionSequence = 0L
                    && atSubmission.CreatedSequence = origin.SourceOperationSequence
                    && atSubmission.ReviewRef = review.ReviewRef
                    && atSubmission.ReviewedHeadOid = review.ReviewedHeadOid
                    && atSubmission.TargetRef = review.TargetRef
                    && atSubmission.SourceVersion = review.SourceVersion)
                do! require originalSubmissionMatches
                        "WQ04 source has no original unclaimed frozen review matching Accepted source."

                let! complete =
                    match after.Works.TryFind stage.Id with
                    | Some later when later.Domain = DomainState.FinalizedPendingTerminal immutableReceipt
                                      && later.State = stage.State
                                      && later.ClaimGeneration = stage.ClaimGeneration
                                      && after.Launches.TryFind launchId = Some owner -> Ok later
                    | _ -> unavailable "WQ02 did not atomically write the original final-domain receipt."
                return ()
            }
            WorkQueueGitLedger.executePinnedDisposableLifecycle binding actor operationId
                expectedVersion now
                (FinalizeVerifiedReview(launchId, reviewWorkId, expectedClaimGeneration,
                                        immutableReceipt))
                verifyAtCas
