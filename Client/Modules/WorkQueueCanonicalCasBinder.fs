module Client.WorkQueueCanonicalCasBinder

open System
open System.Text.Json
open FsToolkit.ErrorHandling
open Jarvis.WorkQueues
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalSourceEvidence
open Client.WorkQueueRoleAuthority

// No positive authority is representable in this module. The accepted
// canonical-event reader proves an original *orchestration* Git event, not a
// complete source-effect/high-water join. The frozen role catalog has not yet
// been integrated into the project writer's single CAS planner.
type RoleScope =
    | NoRoleTransition
    | NewRoleDemand of roleId: string * expectedRoleVersion: int
    | PinnedLaunchRole of launchId: string * roleId: string * pinnedRoleVersion: int

type CanonicalBindRequest =
    { SourceAnchor: CanonicalSourceAnchor
      SourceEvent: SourceEventSelector
      ExpectedProjectVersion: int
      RoleScope: RoleScope }

type MissingAuthority =
    | ProjectReadRejected of string
    | StaleProjectVersion of expected: int * observed: int
    | CanonicalHeadChanged of expected: string * observed: string
    | CanonicalEventRejected of string
    | SourceEffectsUncovered of string
    | RoleEvidenceRejected of string
    | RoleAdmissionUncovered of string
    | SameCasProducerUnproved of string

// Deliberately no Authorized/Ready/Accepted case. Consumers cannot accidentally
// mistake successful original operation lookup for stage/claim authority.
type CanonicalBindAssessment =
    | RecoveryBlocked of MissingAuthority list

module WorkQueueCanonicalCasBinder =
    let private roleGaps (binding: LedgerBinding) (snapshot: LedgerSnapshot) scope =
        match scope with
        | NoRoleTransition -> []
        | NewRoleDemand (roleId, roleVersion)
        | PinnedLaunchRole (_, roleId, roleVersion) ->
            // Verify actual original role issuance and admission at this pinned
            // CAS snapshot. A catalog path, number or legacy subscription is
            // not authority; nor is the result a permission to produce work.
            let uncovered =
                [RoleAdmissionUncovered
                    "Historical role preflight is not the executable same-CAS selected-work, account-credit and producer writer."]
            if String.IsNullOrWhiteSpace roleId || roleId = "Manager" || roleVersion < 1 then
                RoleEvidenceRejected "Invalid role identity or revision." :: uncovered
            else
                match WorkQueueRoleAuthority.loadAt binding snapshot with
                | Error error -> RoleEvidenceRejected(string error) :: uncovered
                | Ok view ->
                    let rejected =
                        match scope with
                        | NewRoleDemand _ ->
                            let selected =
                                view.Catalog.Versions.TryFind roleId
                                |> Option.bind (fun versions -> versions.TryFind roleVersion)
                            match view.Catalog.Current.TryFind roleId, selected with
                            | Some latest, Some role when latest = roleVersion && role.Enabled -> []
                            | _ -> [RoleEvidenceRejected
                                        "New demand requires the current enabled, originally issued role revision."]
                        | PinnedLaunchRole (launchId, _, _) ->
                            match view.Project.Launches.TryFind launchId with
                            | Some launch when launch.RoleId = roleId
                                               && launch.RoleVersion = roleVersion -> []
                            | _ -> [RoleEvidenceRejected
                                        "Pinned launch role/version lacks authenticated original admission."]
                        | NoRoleTransition -> []
                    rejected @ uncovered

    let private eventGaps (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                          (request: CanonicalBindRequest) =
        if request.SourceAnchor.SnapshotHead <> snapshot.Commit then
            [CanonicalHeadChanged(request.SourceAnchor.SnapshotHead, snapshot.Commit)]
        elif request.SourceAnchor.SnapshotHighWater <> snapshot.Header.NextSequence - 1L then
            [CanonicalEventRejected "Pinned canonical high-water does not equal the verified Git-ledger sequence."]
        else
            match WorkQueueCanonicalSourceEvidence.read binding request.SourceAnchor request.SourceEvent with
            | Error error -> [CanonicalEventRejected(string error)]
            | Ok event ->
                if event.SnapshotHead <> snapshot.Commit
                   || event.SnapshotHighWater <> snapshot.Header.NextSequence - 1L
                   || event.ProjectId <> binding.ProjectId
                   || event.LedgerId <> binding.LedgerId
                   || event.Epoch <> binding.Epoch then
                    [CanonicalEventRejected "Verified original event and project planner snapshot cannot be joined."]
                else
                    match event.Coverage with
                    | IncompleteCoverage reason -> [SourceEffectsUncovered reason]

    // Audit-only inspection of a pinned snapshot. Even a perfect original
    // canonical event receipt is insufficient to produce one WorkStage. No
    // caller-supplied ID, event, high-water counter or role witness can change
    // the result. Never use this result as a Scheduler capability.
    let inspectAt (binding: LedgerBinding) (snapshot: LedgerSnapshot)
                  (request: CanonicalBindRequest) : CanonicalBindAssessment =
        let projectGaps =
            match WorkQueueGitLedger.loadAt binding snapshot with
            | Error error -> [ProjectReadRejected(string error)]
            | Ok project when project.Version <> request.ExpectedProjectVersion ->
                [StaleProjectVersion(request.ExpectedProjectVersion, project.Version)]
            | Ok _ -> []
        let gaps =
            projectGaps
            @ eventGaps binding snapshot request
            @ roleGaps binding snapshot request.RoleScope
            @ [SameCasProducerUnproved(
                "No trusted atomic join of original source-effects/terminal evidence, " +
                "canonical event-index/high-water, versioned role matching, account credit " +
                "and project WorkQueue mutation exists. PublishStage/ProduceEvent/AdmitProject/ClaimNext remain closed.")]
        RecoveryBlocked gaps

    let inspect (binding: LedgerBinding) request : CanonicalBindAssessment =
        match LedgerStore.fetch binding with
        | Error error -> RecoveryBlocked [ProjectReadRejected(string error)]
        | Ok snapshot -> inspectAt binding snapshot request

    // A deliberately non-publishing transaction-seam regression target:
    // runs verification inside the SAME project Git CAS planner, but the
    // planner ALWAYS returns Error. There is no successful write/receipt to
    // replay or convert into scheduling authority, even under a two-clone race.
    let attemptUnderCas (binding: LedgerBinding) (actor: string)
                        (operationId: Guid) (request: CanonicalBindRequest) =
        let payload =
            JsonSerializer.Serialize(
                {| schemaVersion = 1
                   command = "WQ04.IncompleteCanonicalBinder"
                   expectedProjectVersion = request.ExpectedProjectVersion
                   sourceHead = request.SourceAnchor.SnapshotHead
                   sourceHighWater = request.SourceAnchor.SnapshotHighWater
                   originalOperationId = request.SourceAnchor.OriginalOperationId.ToString("D")
                   originalRequestHash = request.SourceAnchor.OriginalRequestHash
                   eventSelector = sprintf "%A" request.SourceEvent
                   roleScope = sprintf "%A" request.RoleScope |})
        let intent =
            { OperationId = operationId
              Actor = actor
              Kind = "WQ04.IncompleteCanonicalBinder"
              RequestJson = payload }
        LedgerStore.transact binding intent (fun snapshot ->
            match inspectAt binding snapshot request with
            | RecoveryBlocked gaps ->
                Error(LedgerIntegrityError(
                    "IncompleteCoverage / RecoveryBlocked: no WQ04 canonical producer authority; " +
                    sprintf "%A" gaps)))

    // Exact-UUID lost-reply reconciliation. An applied publication already has
    // its original immutable Git receipt and project-origin record; do not
    // re-run the old source-head preflight against a newer ledger head.
    // An alternative request under that UUID is still an explicit collision.
    let private findPracticalReplay (binding: LedgerBinding) (actor: string)
                                    (operationId: Guid) (request: CanonicalBindRequest) = result {
        if request.RoleScope <> NoRoleTransition then
            return! Error OperationIdReused
        let! snapshot = LedgerStore.fetch binding
        let! existing = LedgerStore.getOperationAt binding snapshot operationId
        match existing with
        | None -> return None
        | Some operation ->
            if operation.Type <> "WQ04.PracticalProducer"
               || operation.Actor <> actor then
                return! Error OperationIdReused
            let! expectedVersion, workId, originalId, requestHash, sourceHead, sourceHighWater, selector =
                try
                    use outer = JsonDocument.Parse(operation.CanonicalRequestJson)
                    use input = JsonDocument.Parse(outer.RootElement.GetProperty("request").GetString())
                    let root = input.RootElement
                    let origin = root.GetProperty("origin")
                    let value (name: string) = origin.GetProperty(name).GetString()
                    Ok(root.GetProperty("expectedProjectVersion").GetInt32(),
                       value "WorkId", value "SourceOperationId", value "SourceRequestHash",
                       value "SourceHead", origin.GetProperty("SourceHighWater").GetInt64(),
                       value "SourceSelector")
                with ex ->
                    Error(LedgerIntegrityError ("WQ04 unreadable original producer request: " + ex.Message))
            if expectedVersion <> request.ExpectedProjectVersion
               || originalId <> request.SourceAnchor.OriginalOperationId.ToString("D")
               || requestHash <> request.SourceAnchor.OriginalRequestHash
               || sourceHead <> request.SourceAnchor.SnapshotHead
               || sourceHighWater <> request.SourceAnchor.SnapshotHighWater
               || selector <> sprintf "%A" request.SourceEvent
               || not (Array.contains (WorkQueueGitLedger.originPath workId) operation.Paths) then
                return! Error OperationIdReused
            let! origin = WorkQueueGitLedger.readOriginAt binding snapshot workId
            let! state = WorkQueueGitLedger.loadAt binding snapshot
            match origin with
            | Some(stored, _) when stored.SourceOperationId = originalId
                                   && stored.SourceSelector = selector
                                   && stored.SourceHead = sourceHead
                                   && stored.SourceHighWater = sourceHighWater
                                   && state.Works.ContainsKey workId ->
                return Some { Operation = operation
                              CurrentHead = snapshot.Commit
                              AlreadyApplied = true }
            | _ ->
                return! Error(LedgerIntegrityError
                    ("WQ04 original producer receipt exists but stage/provenance needs recovery: "
                      + WorkQueueGitLedger.originPath workId))
    }

    // A practical, opt-in producer path. It is deliberately not registered
    // with Scheduler, account admission or source-effect dispatch. Unlike the
    // old speculative binder, an original task/review operation can create
    // useful inspected project work, under one Git CAS and with a separate
    // immutable provenance record. Missing links are reported, not invented.
    let private publishFreshPractical (binding: LedgerBinding) actor operationId
                         (request: CanonicalBindRequest) = result {
        match request.RoleScope with
        | NoRoleTransition -> ()
        | _ ->
            return! Error(InvalidBinding
                "WQ04 role launch/credit admission remains separate; project stage publication reserves no role or account credit.")
        let! proof =
            WorkQueueCanonicalSourceEvidence.read binding request.SourceAnchor request.SourceEvent
        let! snapshot = LedgerStore.fetch binding
        if snapshot.Commit <> proof.SnapshotHead
           || snapshot.Header.NextSequence - 1L <> proof.SnapshotHighWater then
            return! Error(StaleEntityVersion
                "WQ04 canonical source head changed during verification; refresh and reconcile.")
        let! project = WorkQueueGitLedger.loadAt binding snapshot
        if project.Version <> request.ExpectedProjectVersion then
            return! Error(StaleEntityVersion
                (sprintf "WQ04 project version conflict: expected %d, observed %d."
                    request.ExpectedProjectVersion project.Version))
        let projectId = binding.ProjectId.ToString("D")
        let make sourceId generation source hard =
            { ProjectId = projectId; SourceId = sourceId; StageGeneration = generation
              Origin = source; HardPrerequisites = hard }
        let! projection, sourceRef, sourceCommit =
            match proof.Event with
            | CreatedTask task when task.Lifecycle = "Open" ->
                Ok(make task.TaskId 1 (QueueProducerOrigin.TaskRequested task.TaskId) Set.empty,
                   "", "")
            | ReviewRequested review when review.Status = "Unclaimed" ->
                Ok(make review.ReviewId review.SourceVersion
                        (QueueProducerOrigin.FrozenReviewRequested
                            (review.TaskId, review.ReviewId, review.ReviewedHeadOid, review.ReviewRef))
                        Set.empty,
                   review.ReviewRef, review.ReviewedHeadOid)
            | Integration submission when submission.Status = "Pending" ->
                // The submitted integration must point to a previously
                // published review stage with its original source receipt.
                let previous =
                    make submission.ReviewId submission.ReviewVersion
                        (QueueProducerOrigin.FrozenReviewRequested
                            (submission.TaskId, submission.ReviewId,
                             submission.ReviewedHeadOid, submission.ReviewRef)) Set.empty
                let reviewStageId = WorkQueueProducers.workId previous
                WorkQueueGitLedger.readOriginAt binding snapshot reviewStageId
                |> Result.bind (fun reviewOrigin ->
                    match reviewOrigin, project.Works.TryFind reviewStageId with
                    | Some(origin, _), Some stage
                        when origin.SourceOperationKind = "SubmitBranchReview"
                             && origin.SourceCommit = submission.ReviewedHeadOid
                             && origin.SourceRef = submission.ReviewRef
                             && stage.Kind = WorkKind.FrozenReview
                             && stage.SubjectId = submission.TaskId ->
                        Ok(make submission.SubmissionId 1
                                (QueueProducerOrigin.AcceptedIntegration
                                    (submission.TaskId, submission.ReviewId,
                                     submission.SubmittedCommit, submission.TargetRef))
                                (Set.singleton reviewStageId),
                           submission.SourceRef, submission.SubmittedCommit)
                    | _ ->
                        Error(InvalidBinding
                            ("WQ04 accepted integration has no matching originally published review stage: " + reviewStageId)))
            | _ ->
                Error(InvalidBinding
                    "WQ04 practical producer currently supports CreateTask, unclaimed SubmitBranchReview, and accepted SubmitIntegration events.")
        let origin : CanonicalWorkOrigin =
            { SchemaVersion = 1
              ProjectId = projectId
              WorkId = WorkQueueProducers.workId projection
              SourceHead = proof.SnapshotHead
              SourceHighWater = proof.SnapshotHighWater
              SourceOperationId = proof.OperationId.ToString("D")
              SourceOperationSequence = proof.OperationSequence
              SourceOperationKind = proof.OperationKind
              SourceOperationActor = proof.OperationActor
              SourceRequestHash = proof.RequestHash
              SourceOriginHead = proof.OriginHead
              SourceEntityPath = proof.EntityPath
              SourceEntityBlobOid = proof.EntityBlobOid
              SourceEntitySha256 = proof.EntitySha256
              SourceSelector = sprintf "%A" request.SourceEvent
              SourceRef = sourceRef
              SourceCommit = sourceCommit
              RoleId = ""
              RoleVersion = 0
              AccountCreditStatus = "NotReserved"
              Ambiguity = "Producer records project work only. Account credit, source-effect terminal and live scheduler evidence remain separate; an agent must reconcile before activation." }
        return! WorkQueueGitLedger.publishCanonicalEvent
                    binding actor operationId request.ExpectedProjectVersion
                    (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) origin projection
    }

    let publishPractical (binding: LedgerBinding) actor operationId
                         (request: CanonicalBindRequest) =
        match findPracticalReplay binding actor operationId request with
        | Error error -> Error error
        | Ok(Some original) -> Ok original
        | Ok None -> publishFreshPractical binding actor operationId request
