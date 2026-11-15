module Client.WorkQueueReviewIntegrationAdapter

open System
open System.Text.RegularExpressions
open Jarvis.WorkQueues
open Jarvis.WorkQueues.Contracts
open Client.ReviewWorkflow

// WQ-05 isolated adapter. These plans do not publish WQ-04 queue entries,
// mutate canonical review verdicts, dispatch Git pushes or activate a scheduler.
// The WQ-04 producer must persist these immutable bindings with the stage
// generation in one authoritative project CAS before any claim is enabled.
[<RequireQualifiedAccess>]
type AdapterError =
    | InvalidCanonicalReview
    | ReviewNotUndecided
    | ReviewNotDecided
    | ReviewNotAccepted
    | InvalidCanonicalIntegration
    | StaleSourceLineage
    | DuplicateCorrection
    | StageIdentityMismatch
    | UnsafeGateState
    | ContractRejected of Rejection

type FrozenReviewBinding =
    { TaskId: string
      AttemptId: string
      ReviewId: string
      ReviewRef: string
      SourceVersion: int
      SourceSha: string
      SourceRef: string
      BaseCommit: string
      TargetRef: string
      Status: string
      ReviewerId: string
      DecisionSequence: int64 }

type AcceptedIntegrationBinding =
    { TaskId: string
      SubmissionId: string
      ReviewId: string
      ReviewRef: string
      SourceSha: string
      SourceRef: string
      TargetRef: string
      TargetHeadAtSubmission: string
      IntegrationRef: string
      ReviewVersion: int
      SubmissionStatus: string }

type StagePlan<'Provenance> =
    { Work: WorkStage
      Provenance: 'Provenance
      // The queue owner interprets these edges; they are not ordering hints.
      HardPrerequisites: Set<string> }

[<RequireQualifiedAccess>]
type ReviewDecision =
    | Accepted
    | ChangesRequested

module WorkQueueReviewIntegrationAdapter =
    let private nonBlank (value: string) = not (String.IsNullOrWhiteSpace value)
    let private sha (value: string) =
        nonBlank value && Regex.IsMatch(value, "^([a-f0-9]{40}|[a-f0-9]{64})$")
    let private guidN (value: string) =
        match Guid.TryParseExact(value, "N") with
        | true, parsed -> parsed <> Guid.Empty && parsed.ToString("N") = value
        | _ -> false
    let private branch (value: string) =
        nonBlank value
        && value.StartsWith("refs/heads/", StringComparison.Ordinal)
        && value.Length < 400
        && not (value.Contains("..", StringComparison.Ordinal))
        && not (value.Contains("@{", StringComparison.Ordinal))
        && not (value.Contains('\\'))
        && not (value.Contains('\n'))
        && not (value.Contains('\r'))
        && not (value.EndsWith("/", StringComparison.Ordinal))
    let private subject (value: string) =
        nonBlank value && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$")

    let reviewStageId (review: FrozenReviewBinding) =
        sprintf "frozen-review-%s-g%d" review.ReviewId review.SourceVersion

    let integrationStageId (binding: AcceptedIntegrationBinding) =
        "integration-" + binding.SubmissionId

    // ReviewWorkflow is the sole source of truth for claim and verdict.
    // This function validates its pinned identity, not a copy with its own status.
    let bindReview (canonical: BranchReview) : Result<FrozenReviewBinding, AdapterError> =
        let valid =
            not (isNull (box canonical))
            && subject canonical.TaskId && guidN canonical.ReviewId
            && canonical.SourceVersion > 0
            && canonical.Version >= canonical.SourceVersion
            && canonical.CreatedSequence > 0L
            && sha canonical.ReviewedHeadOid
            && sha canonical.BaseCommit
            && branch canonical.ReviewRef
            && canonical.ReviewRef =
               sprintf "refs/heads/jarvis-review/%s/%s/r%d"
                   canonical.TaskId canonical.ReviewId canonical.SourceVersion
            && branch canonical.TargetRef
            && (not (nonBlank canonical.TaskRef) || branch canonical.TaskRef)
            && (match canonical.Status with
                | "Unclaimed" -> canonical.DecisionSequence = 0L
                                 && not (nonBlank canonical.ReviewerId)
                | "Claimed" -> canonical.DecisionSequence = 0L
                               && nonBlank canonical.ReviewerId
                | "Accepted" | "ChangesRequested" ->
                    canonical.DecisionSequence > 0L
                    && nonBlank canonical.ReviewerId
                | _ -> false)
        if not valid then Error AdapterError.InvalidCanonicalReview
        else
            Ok
                { TaskId = canonical.TaskId
                  AttemptId = canonical.AttemptId
                  ReviewId = canonical.ReviewId
                  ReviewRef = canonical.ReviewRef
                  SourceVersion = canonical.SourceVersion
                  SourceSha = canonical.ReviewedHeadOid
                  SourceRef = canonical.TaskRef
                  BaseCommit = canonical.BaseCommit
                  TargetRef = canonical.TargetRef
                  Status = canonical.Status
                  ReviewerId = canonical.ReviewerId
                  DecisionSequence = canonical.DecisionSequence }

    let private draft projectId id taskId kind queue generation prerequisites =
        { Id = id
          ProjectId = projectId
          SubjectId = taskId
          Kind = kind
          QueueId = queue
          StageGeneration = generation
          State = WorkState.Available
          Domain = DomainState.Undecided
          Gate = GateState.Clear
          ClaimGeneration = 0
          Dependencies = prerequisites
          WaivedDependencies = Map.empty
          AvailableAt = 0L
          AvailableSequence = 0L
          SourceLineage = None
          Push = None
          IntegrationAuthorized = false }

    // Blocked reviews are projected as hard-edge work, never as merely higher
    // priority. WQ-04 owns FIFO and blocking-first index computation.
    let planFrozenReview projectId queue (prerequisites: Set<string>) review =
        if review.Status <> "Unclaimed" then Error AdapterError.ReviewNotUndecided
        elif not (subject queue) || not (nonBlank projectId) then
            Error AdapterError.InvalidCanonicalReview
        else
            Ok
                { Work =
                    draft projectId (reviewStageId review) review.TaskId
                          WorkKind.FrozenReview queue review.SourceVersion prerequisites
                  Provenance = review
                  HardPrerequisites = prerequisites }

    let planCorrection (project: Project) queue (review: FrozenReviewBinding)
                       (source: WorkStage) =
        if review.Status <> "ChangesRequested" then Error AdapterError.ReviewNotDecided
        elif source.SubjectId <> review.TaskId
             || source.Kind <> WorkKind.Implementation
             || source.State <> WorkState.Completed
             || project.Works.TryFind source.Id <> Some source then
            Error AdapterError.StaleSourceLineage
        else
            match source.SourceLineage with
            | None -> Error AdapterError.StaleSourceLineage
            | Some lineage when lineage.TaskId <> review.TaskId
                                || lineage.TargetRef <> review.TargetRef
                                || not (sha lineage.PinnedBaseCommit)
                                || not (subject queue) ->
                Error AdapterError.StaleSourceLineage
            | Some lineage ->
                let duplicate =
                    project.Works
                    |> Map.exists (fun id work ->
                        id <> source.Id
                        && work.SubjectId = review.TaskId
                        && work.Kind = WorkKind.Implementation
                        && (work.StageGeneration >= source.StageGeneration
                            || (work.State <> WorkState.Completed
                                && work.State <> WorkState.Cancelled))
                        && (work.SourceLineage |> Option.exists (fun value ->
                            value.LineageId = lineage.LineageId)))
                if duplicate then Error AdapterError.DuplicateCorrection
                else
                    let next =
                        draft source.ProjectId
                            (sprintf "source-correction-%s-g%d" review.ReviewId (source.StageGeneration + 1))
                            review.TaskId WorkKind.Implementation queue
                            (source.StageGeneration + 1) (Set.singleton (reviewStageId review))
                    Ok
                        { Work =
                            { next with
                                SourceLineage = Some { lineage with Generation = lineage.Generation + 1 } }
                          Provenance = review
                          HardPrerequisites = Set.singleton (reviewStageId review) }

    // Submission and review are independently immutable canonical records;
    // exact identity is checked again by the eventual WQ-04 same-CAS producer.
    let bindAcceptedIntegration (review: FrozenReviewBinding)
                                (submission: IntegrationSubmission) =
        if review.Status <> "Accepted" || review.DecisionSequence <= 0L then
            Error AdapterError.ReviewNotAccepted
        else
            let valid =
                not (isNull (box submission))
                && guidN submission.SubmissionId
                && submission.TaskId = review.TaskId
                && submission.ReviewId = review.ReviewId
                && submission.ReviewRef = review.ReviewRef
                && submission.ReviewVersion = review.SourceVersion
                && submission.ReviewedHeadOid = review.SourceSha
                && submission.SubmittedCommit = review.SourceSha
                && submission.TargetRef = review.TargetRef
                && submission.SourceRef = review.SourceRef
                && submission.IntegrationRef =
                    "refs/heads/jarvis-integration/" + submission.SubmissionId
                && sha submission.TargetHeadAtSubmission
                && submission.CreatedSequence > 0L
                && (submission.Status = "Pending"
                    || (submission.Status = "Integrated"
                        && submission.IntegratedCommit = review.SourceSha))
            if not valid then Error AdapterError.InvalidCanonicalIntegration
            else
                Ok
                    { TaskId = review.TaskId
                      SubmissionId = submission.SubmissionId
                      ReviewId = review.ReviewId
                      ReviewRef = review.ReviewRef
                      SourceSha = review.SourceSha
                      SourceRef = review.SourceRef
                      TargetRef = review.TargetRef
                      TargetHeadAtSubmission = submission.TargetHeadAtSubmission
                      IntegrationRef = submission.IntegrationRef
                      ReviewVersion = review.SourceVersion
                      SubmissionStatus = submission.Status }

    let planAcceptedIntegration projectId queue (prerequisites: Set<string>)
                                (binding: AcceptedIntegrationBinding) =
        if not (nonBlank projectId) || not (subject queue) then
            Error AdapterError.InvalidCanonicalIntegration
        elif binding.SubmissionStatus <> "Pending" then
            Error AdapterError.InvalidCanonicalIntegration
        else
            let prerequisites = prerequisites.Add
                                   (sprintf "frozen-review-%s-g%d" binding.ReviewId binding.ReviewVersion)
            Ok
                { Work =
                    draft projectId (integrationStageId binding) binding.TaskId
                          WorkKind.Integration queue 1 prerequisites
                  Provenance = binding
                  HardPrerequisites = prerequisites }

    // Only the original immutable push may be recorded. The external caller
    // still must prove journal coverage, remote SHA/target ancestry and jobs
    // at the WQ-03 boundary; this pure contract adapter cannot self-attest them.
    let startPinnedOriginalPush (binding: AcceptedIntegrationBinding)
                                expected op launchId pushOperation (project: Project) =
        let workId = integrationStageId binding
        match project.Works.TryFind workId with
        | Some work when work.Kind = WorkKind.Integration
                         && work.SubjectId = binding.TaskId
                         && work.State = WorkState.Claimed launchId
                         && work.Gate = GateState.Clear
                         && not (String.IsNullOrWhiteSpace pushOperation) ->
            Contracts.startOriginalPush expected op launchId workId pushOperation
                binding.SourceSha binding.TargetRef project
            |> Result.mapError AdapterError.ContractRejected
        | _ -> Error AdapterError.StageIdentityMismatch

    let addHardEdge expected op (binding: AcceptedIntegrationBinding)
                    prerequisite now (project: Project) =
        let workId = integrationStageId binding
        match project.Works.TryFind workId with
        | Some work when work.SubjectId = binding.TaskId
                         && work.Kind = WorkKind.Integration ->
            Contracts.setHardPrerequisite expected op workId prerequisite now project
            |> Result.mapError AdapterError.ContractRejected
        | _ -> Error AdapterError.StageIdentityMismatch

    let observeOriginalPushUnderGate expected op launchId
                                     (binding: AcceptedIntegrationBinding)
                                     (verified: VerifiedRemoteEffect) now
                                     (project: Project) =
        if verified.OriginalPushOperationId = ""
           || verified.AcceptedSourceSha <> binding.SourceSha
           || verified.TargetRef <> binding.TargetRef
           || not verified.AncestryVerified
           || not verified.JobsReconciled
           || not (sha verified.RemoteCommitSha) then
            Error AdapterError.UnsafeGateState
        else
            Contracts.reconcileExternalEffect expected op launchId
                (integrationStageId binding) verified now project
            |> Result.mapError AdapterError.ContractRejected

    let resolveObservedGate expected op (binding: AcceptedIntegrationBinding)
                            waiver now (project: Project) =
        match project.Works.TryFind (integrationStageId binding) with
        | Some stage when stage.Kind = WorkKind.Integration
                          && stage.SubjectId = binding.TaskId
                          && stage.State = WorkState.GateException
                          && stage.Domain = DomainState.Undecided
                          && not stage.IntegrationAuthorized
                          && (match stage.Push with
                              | Some journal ->
                                  journal.AcceptedSourceSha = binding.SourceSha
                                  && journal.TargetRef = binding.TargetRef
                              | None -> false) ->
            Contracts.resolveIntegrationGateException expected op stage.Id waiver now project
            |> Result.mapError AdapterError.ContractRejected
        | _ -> Error AdapterError.UnsafeGateState

    // The adapter only *checks* the post-CAS gate. Readiness of a downstream
    // stage requires the canonical authorized producer and complete WQ-04 index.
    let isAuthorizedIntegration (project: Project) binding =
        project.GraphCovered && project.QueueCovered
        && project.MessageCovered && project.SourceCovered
        && (match project.Works.TryFind (integrationStageId binding) with
            | Some stage ->
                stage.Kind = WorkKind.Integration
                && stage.SubjectId = binding.TaskId
                && stage.State = WorkState.Completed
                && stage.IntegrationAuthorized
                && stage.Gate = GateState.Clear
                && (stage.Dependencies
                    |> Set.forall (fun id ->
                        stage.WaivedDependencies.ContainsKey id
                        || Contracts.isSatisfied project id))
                && (match stage.Domain, stage.Push with
                    | DomainState.Integrated integratedCommit,
                      Some { AcceptedSourceSha = sourceSha
                             TargetRef = targetRef
                             Outcome = PushOutcome.Observed observedCommit } ->
                        integratedCommit = observedCommit
                        && sha integratedCommit
                        && sourceSha = binding.SourceSha
                        && targetRef = binding.TargetRef
                    | _ -> false)
            | None -> false)
