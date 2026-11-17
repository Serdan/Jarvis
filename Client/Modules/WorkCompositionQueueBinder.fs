module Client.WorkCompositionQueueBinder

open System
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.WorkCompositionGitLedger
open Client.WorkCompositionAcceptanceAdapter
open Client.WorkQueueGitLedger
open Jarvis.WorkQueues

// Read-only verification and an intentionally closed transaction seam.
// A composition proof alone must never turn a WQ02 stage into launch authority.
type CompositionQueueBindRequest =
    { ProjectId: Guid
      SnapshotHead: string
      UnitId: string
      UnitSpecVersion: int
      GraphVersion: int
      ExpectedProjectVersion: int
      WorkId: string
      StageGeneration: int
      SameHeadWorkOriginOid: string
      IntegrationSubmissionId: Guid }

type CompositionQueueBlock =
    | InvalidRequest of string
    | ChangedHead of expected: string * observed: string
    | GraphUnavailable of string
    | StaleGraph of expected: int * observed: int
    | MissingOrChangedUnit of string
    | UnacceptedDependency of string
    | UnverifiedIntegration of string
    | QueueUnavailable of string
    | StaleQueue of expected: int * observed: int
    | MissingOrChangedStage of string
    | ContradictoryReadiness of workId: string
    | MissingOrChangedOrigin of string
    | UnprovenRoleOrSource of string
    | CyclicQueueDependencies of string list
    | SameHeadAtomicWriterUnavailable of string

type CompositionQueueAssessment =
    | RecoveryBlocked of verifiedHead: string * blockers: CompositionQueueBlock list

module WorkCompositionQueueBinder =
    let private nonBlank (text: string) = not (String.IsNullOrWhiteSpace text)

    // The accepted composition reviewer verifies these exact identities in
    // its own writer. This independent read is diagnostic only: it CANNOT
    // substitute for calling that verifier inside a shared CAS transaction.
    let private receiptGaps binding snapshot graph (unit: CompositeWorkUnit) (submissionId: Guid) =
        let receipt = "integration:" + submissionId.ToString("N")
        match unit.Spec.LinkedTaskId with
        | None -> [ UnverifiedIntegration "Composition unit has no linked canonical task." ]
        | Some taskId when not (nonBlank taskId) ->
            [ UnverifiedIntegration "Composition task ID is empty." ]
        | Some taskId when submissionId = Guid.Empty ->
            [ UnverifiedIntegration "A caller-supplied Accept flag or empty submission ID is not evidence." ]
        | Some taskId ->
            match ReviewWorkflow.getIntegrationSubmissionAt binding snapshot taskId submissionId with
            | Error error -> [ UnverifiedIntegration (string error) ]
            | Ok submission ->
                match Guid.TryParseExact(submission.ReviewId, "N") with
                | false, _ -> [ UnverifiedIntegration "Integration has no canonical review ID." ]
                | true, reviewId when reviewId = Guid.Empty ->
                    [ UnverifiedIntegration "Integration has an empty review ID." ]
                | true, reviewId ->
                    match ReviewWorkflow.getBranchReviewAt binding snapshot taskId reviewId with
                    | Error error -> [ UnverifiedIntegration (string error) ]
                    | Ok review ->
                        let matches =
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
                            && review.Kind = WorkCompositionAcceptanceAdapter.reviewPurpose graph unit
                            && review.DecisionSequence > review.CreatedSequence
                            && nonBlank review.ReviewerId
                            && nonBlank review.OwnerAgentId
                            && review.ReviewerId <> review.OwnerAgentId
                        if not matches then
                            [ UnverifiedIntegration "Current graph closure lacks an independently accepted review and matching integrated source receipt." ]
                        else
                            match WorkComposition.currentAcceptance graph unit.Id with
                            | Some proof when proof.EvidenceRef = receipt
                                              && proof.SpecVersion = unit.SpecVersion -> []
                            | Some _ ->
                                [ UnverifiedIntegration "Existing acceptance targets a different spec or integrated receipt." ]
                            | None ->
                                match WorkComposition.apply graph.Version
                                      (WorkCompositionMutation.Accept(unit.Id, unit.SpecVersion, receipt)) graph with
                                | Ok _ -> []
                                | Error error ->
                                    [ UnverifiedIntegration ("Current graph rejects proposed acceptance: " + string error) ]

    let private graphGaps binding snapshot (request: CompositionQueueBindRequest) =
        match WorkCompositionGitLedger.loadAt binding snapshot with
        | Error error -> [ GraphUnavailable (string error) ], None, None
        | Ok (graph, _) ->
            let version =
                if request.GraphVersion <> graph.Version then
                    [ StaleGraph(request.GraphVersion, graph.Version) ]
                else []
            match graph.Units.TryFind request.UnitId with
            | None -> version @ [ MissingOrChangedUnit "Unknown composition unit." ], Some graph, None
            | Some unit ->
                let identity =
                    if unit.SpecVersion <> request.UnitSpecVersion || unit.CancellationReason.IsSome then
                        [ MissingOrChangedUnit "Composition unit spec changed or was cancelled." ]
                    else []
                let pending =
                    unit.HardPrerequisites
                    |> Set.toList
                    |> List.choose (fun id ->
                        if WorkComposition.currentAcceptance graph id |> Option.isSome then None
                        else Some (UnacceptedDependency id))
                let accepted = receiptGaps binding snapshot graph unit request.IntegrationSubmissionId
                version @ identity @ pending @ accepted, Some graph, Some unit

    let private originGaps binding snapshot workId expectedOid =
        match WorkQueueGitLedger.readOriginAt binding snapshot workId with
        | Error error -> [ MissingOrChangedOrigin (string error) ]
        | Ok None -> [ MissingOrChangedOrigin "No original WQ04 stage provenance at this head." ]
        | Ok (Some (origin, blobOid)) ->
            let identity =
                if not (nonBlank expectedOid) || blobOid <> expectedOid
                   || origin.ProjectId <> binding.ProjectId.ToString("D")
                   || origin.WorkId <> workId then
                    [ MissingOrChangedOrigin "Pinned same-head origin blob or project/work identity differs." ]
                else []
            let roleSource =
                if not (nonBlank origin.RoleId) || origin.RoleVersion < 1
                   || (origin.SourceOperationKind <> "CreateTask"
                       && (not (nonBlank origin.SourceRef) || not (nonBlank origin.SourceCommit)))
                   || (origin.SourceOperationKind = "CreateTask"
                       && (origin.SourceRef <> "" || origin.SourceCommit <> "")) then
                    [ UnprovenRoleOrSource "Original stage does not carry approved role and source lineage." ]
                else []
            let original =
                match Guid.TryParse origin.SourceOperationId with
                | false, _ -> [ MissingOrChangedOrigin "Missing original operation UUID." ]
                | true, id when id = Guid.Empty ->
                    [ MissingOrChangedOrigin "Original operation UUID is empty." ]
                | true, id ->
                    match LedgerStore.getOperationAt binding snapshot id with
                    | Error error -> [ MissingOrChangedOrigin (string error) ]
                    | Ok None -> [ MissingOrChangedOrigin "Original operation receipt is absent." ]
                    | Ok (Some operation) ->
                        if operation.RequestHash <> origin.SourceRequestHash
                           || operation.Sequence <> origin.SourceOperationSequence
                           || operation.Type <> origin.SourceOperationKind
                           || operation.Actor <> origin.SourceOperationActor then
                            [ MissingOrChangedOrigin "Original operation ID, request hash or sequence differs." ]
                        else []
            identity @ roleSource @ original

    let private queueGaps binding snapshot (request: CompositionQueueBindRequest)
                          (graph: WorkCompositionGraph option) (unit: CompositeWorkUnit option) =
        match WorkQueueGitLedger.loadAt binding snapshot with
        | Error error -> [ QueueUnavailable (string error) ]
        | Ok project ->
            let version =
                if project.Version <> request.ExpectedProjectVersion then
                    [ StaleQueue(request.ExpectedProjectVersion, project.Version) ]
                else []
            let stageIssues =
                match project.Works.TryFind request.WorkId with
                | None -> [ MissingOrChangedStage "No matching canonical WQ02 Work stage." ]
                | Some stage ->
                    let mismatch =
                        if stage.ProjectId <> binding.ProjectId.ToString("D")
                           || stage.StageGeneration <> request.StageGeneration
                           || stage.StageGeneration < 1
                           || (unit |> Option.bind (fun u -> u.Spec.LinkedTaskId)
                               <> Some stage.SubjectId) then
                            [ MissingOrChangedStage "Work ID/generation/project/task differs from composition binding." ]
                        else []
                    let graphBlocked =
                        match graph, unit with
                        | Some actual, Some u ->
                            u.HardPrerequisites
                            |> Set.exists (fun id ->
                                WorkComposition.currentAcceptance actual id |> Option.isNone)
                        | _ -> true
                    let readiness =
                        if graphBlocked && stage.State = WorkState.Available then
                            [ ContradictoryReadiness stage.Id ]
                        else []
                    mismatch @ readiness
            let cycles =
                match Contracts.circularHardDependencies project with
                | Ok found -> found |> List.map (fun cycle -> CyclicQueueDependencies cycle.Members)
                | Error error -> [ QueueUnavailable ("WQ04 coverage prevents cycle inspection: " + string error) ]
            version @ stageIssues @ originGaps binding snapshot request.WorkId request.SameHeadWorkOriginOid @ cycles

    // All inputs are checked against one *verified* project Git snapshot.
    // Even with perfect evidence this is NOT a readiness or launch capability.
    // WorkCompositionGitLedger and WorkQueueGitLedger currently each own a
    // separate writer/operation shape. Combining them by calling both writers
    // sequentially would leave an observable inconsistent intermediate head.
    let inspectAt (binding: LedgerBinding) (snapshot: LedgerSnapshot) (request: CompositionQueueBindRequest) =
        let malformed =
            [ if request.ProjectId <> binding.ProjectId then
                  InvalidRequest "Project identity differs from the trusted ledger binding."
              if not (nonBlank request.UnitId) || not (nonBlank request.WorkId)
                 || request.UnitSpecVersion < 1 || request.StageGeneration < 1 then
                  InvalidRequest "Unit/work identities and positive generations are required."
              if request.IntegrationSubmissionId = Guid.Empty then
                  InvalidRequest "A nonempty canonical integration receipt ID is required."
              if not (nonBlank request.SnapshotHead) then
                  InvalidRequest "A pinned canonical Git head is required." ]
        let head =
            if request.SnapshotHead <> snapshot.Commit then
                [ ChangedHead(request.SnapshotHead, snapshot.Commit) ]
            else []
        let graphGaps, graph, unit = graphGaps binding snapshot request
        let queue = queueGaps binding snapshot request graph unit
        let atomic =
            [ SameHeadAtomicWriterUnavailable
                "No accepted composition-to-WQ02 stage planner shares the original canonical mutation, provenance and receipt writer in one validated Git CAS. Do not publish or activate a stage." ]
        RecoveryBlocked(snapshot.Commit, malformed @ head @ graphGaps @ queue @ atomic)

    let inspect binding request =
        match LedgerStore.fetch binding with
        | Error error ->
            RecoveryBlocked("", [ GraphUnavailable (string error)
                                  SameHeadAtomicWriterUnavailable "Verified Git snapshot unavailable; no stage authority." ])
        | Ok snapshot -> inspectAt binding snapshot request

    // Enforce the missing-primitive decision *inside* the original Git CAS.
    // No arbitrary Accept input, graph write, WQ02 write, or operation receipt
    // can be produced from this seam, including after a lost or raced reply.
    let attemptUnderCas binding actor (operationId: Guid) (request: CompositionQueueBindRequest) =
        if operationId = Guid.Empty then Error(InvalidBinding "Original operation UUID is required.")
        else
            let intent =
                { OperationId = operationId
                  Actor = actor
                  Kind = "WorkCompositionQueue.AtomicBindingUnimplemented"
                  RequestJson = System.Text.Json.JsonSerializer.Serialize request }
            LedgerStore.transact binding intent (fun snapshot ->
                match inspectAt binding snapshot request with
                | RecoveryBlocked (_, gaps) ->
                    Error(InvalidBinding ("RecoveryBlocked: same-head composition/WQ02 atomicity unproven; "
                                          + sprintf "%A" gaps)))
