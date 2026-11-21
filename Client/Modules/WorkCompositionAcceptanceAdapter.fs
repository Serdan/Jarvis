module Client.WorkCompositionAcceptanceAdapter

open System
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.WorkCompositionGitLedger
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Jarvis.WorkQueues

// A parent review binds the current composition closure, not just its spec.
// New or re-accepted descendants invalidate old approval evidence.
type AcceptanceProofTarget =
    { SpecVersion: int
      GraphVersion: int
      EvidenceRef: string }

type ChildAcceptanceTarget =
    { SpecVersion: int
      CancellationReason: string option
      CurrentAcceptance: AcceptanceProofTarget option }

type ReviewAcceptanceTarget =
    { UnitId: string
      Kind: WorkUnitKind
      ParentId: string option
      SpecVersion: int
      SpecHash: string
      Children: Map<string, ChildAcceptanceTarget>
      Prerequisites: Map<string, AcceptanceProofTarget option> }

// The composition graph is not a second review or integration workflow.
// Validation uses the existing canonical receipts at the same verified Git
// ledger snapshot as the compare-and-swap graph mutation.
module WorkCompositionAcceptanceAdapter =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    let private proofTarget (proof: WorkUnitAcceptance) =
        { SpecVersion = proof.SpecVersion
          GraphVersion = proof.GraphVersion
          EvidenceRef = proof.EvidenceRef }

    // Reviewed target includes the composition closure: immediate children
    // and prerequisite proof versions also bind the transitive descendants.
    // Unrelated graph mutations do not stale this target.
    let reviewPurpose (graph: WorkCompositionGraph) (unit: CompositeWorkUnit) =
        let children =
            graph.Units
            |> Map.toList
            |> List.choose (fun (id, child) ->
                if child.ParentId = Some unit.Id then
                    Some(id,
                         { SpecVersion = child.SpecVersion
                           CancellationReason = child.CancellationReason
                           CurrentAcceptance =
                             WorkComposition.currentAcceptance graph id |> Option.map proofTarget })
                else None)
            |> Map.ofList
        let prerequisites =
            unit.HardPrerequisites
            |> Seq.map (fun id ->
                id, WorkComposition.currentAcceptance graph id |> Option.map proofTarget)
            |> Map.ofSeq
        let target: ReviewAcceptanceTarget =
            { UnitId = unit.Id
              Kind = unit.Kind
              ParentId = unit.ParentId
              SpecVersion = unit.SpecVersion
              SpecHash = CanonicalCodec.digest (CanonicalCodec.encode unit.Spec)
              Children = children
              Prerequisites = prerequisites }
        "WorkComposition.Acceptance." + CanonicalCodec.digest (CanonicalCodec.encode target)

    // Every managed acceptance is rooted in the original canonical
    // CompleteIntegration transaction, irrespective of whether I equals S.
    // A mutable I-to-S downgrade (or cleared publisher fields) can never turn
    // a modern completion into an unauthenticated historical direct receipt.
    let private requireManagedCompletionAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                                           taskId (submissionId: Guid) (submission: IntegrationSubmission) = result {
        let path = $"tasks/{taskId}/integrations/{submissionId:N}.json"
        let! completion = LedgerStore.findCompletionOperationForPathAt binding snapshot path
        if completion.Sequence <= submission.CreatedSequence
           || String.IsNullOrWhiteSpace completion.Actor then
            return! Error(InvalidBinding
                "Composition integration lacks a valid canonical completion sequence and actor.")
        let! originalHead = LedgerStore.getOperationOriginAt binding snapshot completion
        let! originalSnapshot = LedgerStore.verifiedHistoricalSnapshot binding originalHead
        let! originalReceipt =
            ReviewWorkflow.getIntegrationSubmissionAt binding originalSnapshot taskId submissionId
        if originalReceipt <> submission then
            return! Error(InvalidBinding
                "Composition integration receipt differs from its original immutable completion.")
        do! LedgerStore.fetchReviewRef binding submission.ReviewRef submission.SubmittedCommit
        do! LedgerStore.fetchIntegrationRef binding submission.IntegrationRef submission.SubmittedCommit

        let legacyDirect =
            originalReceipt.IntegratedCommit = originalReceipt.SubmittedCommit
            && String.IsNullOrWhiteSpace originalReceipt.PublicationOperationId
            && String.IsNullOrWhiteSpace originalReceipt.PublicationMechanism
            && String.IsNullOrWhiteSpace originalReceipt.PublicationPreviousHead
            && String.IsNullOrWhiteSpace originalReceipt.PublicationObservedHead
        if legacyDirect then
            // Pre-publisher completed direct receipts have no v1 non-force
            // fields. Their compatibility is grounded in the unchanged
            // historical CompleteIntegration operation, not a mutable shape.
            // They only claim the old remote-ancestry contract, not a new
            // retroactive non-force publication attestation.
            do! LedgerStore.verifySourceAncestry binding
                    originalReceipt.TargetHeadAtSubmission submission.SubmittedCommit
            do! LedgerStore.verifyIntegrationOnRemote binding
                    originalReceipt.TargetRef submission.SubmittedCommit submission.IntegratedCommit
        else
            let! _ =
                match Guid.TryParseExact(submission.PublicationOperationId, "D") with
                | true, id when id <> Guid.Empty
                                && submission.PublicationMechanism = "VerifiedNonForceGitPush/v1"
                                && completion.OperationId = id.ToString("D") -> Ok id
                | _ -> Error(InvalidBinding
                    "Managed integration has no original verified non-force publisher receipt.")
            if not (LedgerStore.validSourceCommitOid submission.PublicationPreviousHead)
               || not (LedgerStore.validSourceCommitOid submission.PublicationObservedHead)
               || submission.PublicationPreviousHead = submission.IntegratedCommit then
                return! Error(InvalidBinding "Invalid original integration publication heads.")
            if submission.IntegratedCommit = submission.SubmittedCommit then
                let! remoteHead = LedgerStore.readRemoteTarget binding submission.TargetRef
                do! LedgerStore.verifySourceAncestry binding
                        submission.TargetHeadAtSubmission submission.PublicationPreviousHead
                do! LedgerStore.verifySourceAncestry binding
                        submission.PublicationPreviousHead submission.IntegratedCommit
                do! LedgerStore.verifySourceAncestry binding
                        submission.IntegratedCommit submission.PublicationObservedHead
                do! LedgerStore.verifySourceAncestry binding
                        submission.PublicationObservedHead remoteHead
            else
                do! LedgerStore.verifyPublishedMechanicalIntegration binding
                        submission.TargetRef submission.TargetHeadAtSubmission
                        submission.SubmittedCommit submission.IntegratedCommit
                        submission.PublicationPreviousHead submission.PublicationObservedHead
    }

    let internal checkAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                        (graph: WorkCompositionGraph) (unitId: string) (receipt: string) =
        result {
            let! unit =
                match graph.Units.TryFind unitId with
                | Some unit -> Ok unit
                | None -> Error(InvalidBinding "Unknown composition unit.")
            let! taskId =
                match unit.Spec.LinkedTaskId with
                | Some taskId when nonBlank taskId -> Ok taskId
                | _ -> Error(InvalidBinding "Composition acceptance requires a linked canonical validation task.")
            let! submissionId =
                if not (receipt.StartsWith("integration:", StringComparison.Ordinal)) then
                    Error(InvalidBinding "Invalid composition acceptance receipt.")
                else
                    match Guid.TryParseExact(receipt.Substring("integration:".Length), "N") with
                    | true, id when id <> Guid.Empty -> Ok id
                    | _ -> Error(InvalidBinding "Invalid composition integration receipt ID.")
            let! submission =
                ReviewWorkflow.getIntegrationSubmissionAt binding snapshot taskId submissionId
            let! reviewId =
                match Guid.TryParseExact(submission.ReviewId, "N") with
                | true, id when id <> Guid.Empty -> Ok id
                | _ -> Error(LedgerIntegrityError "Composition integration has an invalid review ID.")
            let! review =
                ReviewWorkflow.getBranchReviewAt binding snapshot taskId reviewId
            let valid =
                submission.Status = "Integrated"
                && nonBlank submission.IntegrationRef
                && submission.IntegrationRef = "refs/heads/jarvis-integration/" + submission.SubmissionId
                && nonBlank submission.IntegratedCommit
                && nonBlank submission.SubmittedCommit
                && submission.ReviewedHeadOid = submission.SubmittedCommit
                && submission.ReviewRef = review.ReviewRef
                && submission.ReviewVersion = review.SourceVersion
                && submission.ReviewId = review.ReviewId
                && submission.TargetRef = review.TargetRef
                && submission.CreatedSequence > review.DecisionSequence
                && review.ReviewedHeadOid = submission.SubmittedCommit
                && review.Status = "Accepted"
                && review.Kind = reviewPurpose graph unit
                && review.DecisionSequence > review.CreatedSequence
                && nonBlank review.ReviewerId
                && nonBlank review.OwnerAgentId
                && review.ReviewerId <> review.OwnerAgentId
            if not valid then
                return! Error(InvalidBinding
                    "Composition acceptance requires an independent review of this exact spec and an integrated canonical source receipt.")
            do! requireManagedCompletionAt binding snapshot taskId submissionId submission
            return ()
        }

    // The adapter constructs the evidence identity; a caller cannot provide a
    // free-form acceptance string. All reads are inside the graph's ledger CAS.
    let acceptIntegrated binding actor (operationId: Guid) expectedGraphVersion
                         unitId expectedSpecVersion (submissionId: Guid) =
        if submissionId = Guid.Empty then
            Error(InvalidBinding "Integration submission ID is required.")
        else
            let receipt = "integration:" + submissionId.ToString("N")
            WorkCompositionGitLedger.executeVerifiedAcceptance binding actor
                operationId expectedGraphVersion unitId expectedSpecVersion
                receipt (checkAt binding)
