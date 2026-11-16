module Client.WorkCompositionAcceptanceAdapter

open System
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.WorkCompositionGitLedger
open Client.WorkQueueGitLedger
open Client.ReviewWorkflow
open Jarvis.WorkQueues

// The composition graph is not a second review or integration workflow.
// Validation uses the existing canonical receipts at the same verified Git
// ledger snapshot as the compare-and-swap graph mutation.
module WorkCompositionAcceptanceAdapter =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)

    // The review purpose binds the independent review to one exact unit target.
    // A subsequent spec revision requires a new acceptance review, even when
    // the same implementation commit remains reachable from the target branch.
    let reviewPurpose (unit: CompositeWorkUnit) =
        let specHash = CanonicalCodec.digest (CanonicalCodec.encode unit.Spec)
        let target = sprintf "%s\n%d\n%s" unit.Id unit.SpecVersion specHash
        "WorkComposition.Acceptance." + CanonicalCodec.digest target

    let private checkAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
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
                && submission.IntegratedCommit = submission.SubmittedCommit
                && nonBlank submission.SubmittedCommit
                && submission.ReviewedHeadOid = submission.SubmittedCommit
                && submission.ReviewRef = review.ReviewRef
                && submission.ReviewVersion = review.SourceVersion
                && submission.ReviewId = review.ReviewId
                && submission.TargetRef = review.TargetRef
                && submission.CreatedSequence > review.DecisionSequence
                && review.ReviewedHeadOid = submission.SubmittedCommit
                && review.Status = "Accepted"
                && review.Kind = reviewPurpose unit
                && review.DecisionSequence > review.CreatedSequence
                && nonBlank review.ReviewerId
                && nonBlank review.OwnerAgentId
                && review.ReviewerId <> review.OwnerAgentId
            if not valid then
                return! Error(InvalidBinding
                    "Composition acceptance requires an independent review of this exact spec and an integrated canonical source receipt.")
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
