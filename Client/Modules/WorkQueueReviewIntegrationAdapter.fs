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
    // The WQ-04 immutable source/review/submission-to-stage CAS join does not exist yet.
    | AuthoritativeJoinRequired
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

// WQ-04 must consume this untrusted proposal and verify its complete binding
// under its authoritative project high-water CAS. No WorkStage is issued here.
type AcceptedIntegrationIntent =
    { ProjectId: string
      QueueId: string
      ImmutableBinding: AcceptedIntegrationBinding
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

    // WQ-04 has not yet supplied an authoritative review -> current attempt ->
    // exact frozen SHA/ref -> lineage/generation join. Task+target and local
    // WorkStage equality alone cannot establish source ownership. Never emit a
    // correction WorkStage, even for a plausible single-lineage fixture.
    let planCorrection (project: Project) queue (review: FrozenReviewBinding)
                       (source: WorkStage) : Result<StagePlan<FrozenReviewBinding>, AdapterError> =
        if review.Status <> "ChangesRequested" then Error AdapterError.ReviewNotDecided
        elif source.SubjectId <> review.TaskId
             || source.Kind <> WorkKind.Implementation
             || source.State <> WorkState.Completed
             || project.Works.TryFind source.Id <> Some source
             || not (subject queue)
             || (source.SourceLineage |> Option.exists (fun lineage ->
                 lineage.TaskId <> review.TaskId || lineage.TargetRef <> review.TargetRef))
             || source.SourceLineage.IsNone then
            Error AdapterError.StaleSourceLineage
        else
            Error AdapterError.AuthoritativeJoinRequired

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

    // Planning produces a non-runnable intent. There is intentionally no
    // WorkStage to publish: that would drop canonical Accepted identity and
    // allow substituted review/SHA/target fields after a later claim.
    let planAcceptedIntegration projectId queue (prerequisites: Set<string>)
                                (binding: AcceptedIntegrationBinding) =
        if not (nonBlank projectId) || not (subject queue)
           || binding.SubmissionStatus <> "Pending" then
            Error AdapterError.InvalidCanonicalIntegration
        else
            let prerequisites =
                prerequisites.Add (sprintf "frozen-review-%s-g%d" binding.ReviewId binding.ReviewVersion)
            Ok { ProjectId = projectId
                 QueueId = queue
                 ImmutableBinding = binding
                 HardPrerequisites = prerequisites }

    // Only the original immutable push may be recorded. The external caller
    // still must prove journal coverage, remote SHA/target ancestry and jobs
    // at the WQ-03 boundary; this pure contract adapter cannot self-attest them.
    // Every effecting adapter path stays disabled until WQ04 can prove a
    // canonical same-CAS join to stage/generation/current source high-water.
    // The pure Contracts transitions are covered by disposable tests only.
    let startPinnedOriginalPush (_binding: AcceptedIntegrationBinding)
                                _expected _op _launchId _pushOperation (_project: Project)
                                : Result<Project, AdapterError> =
        Error AdapterError.AuthoritativeJoinRequired

    let addHardEdge _expected _op (_binding: AcceptedIntegrationBinding)
                    _prerequisite _now (_project: Project) : Result<Project, AdapterError> =
        Error AdapterError.AuthoritativeJoinRequired

    let observeOriginalPushUnderGate _expected _op _launchId
                                     (_binding: AcceptedIntegrationBinding)
                                     (_verified: VerifiedRemoteEffect) _now
                                     (_project: Project) : Result<Project, AdapterError> =
        Error AdapterError.AuthoritativeJoinRequired

    let resolveObservedGate _expected _op (_binding: AcceptedIntegrationBinding)
                            _waiver _now (_project: Project) : Result<Project, AdapterError> =
        Error AdapterError.AuthoritativeJoinRequired

    // Even a pure contract Completed/Integrated result is not an authorized
    // WQ04 high-water projection and must never unblock dependent work here.
    let isAuthorizedIntegration (_project: Project) (_binding: AcceptedIntegrationBinding) = false
