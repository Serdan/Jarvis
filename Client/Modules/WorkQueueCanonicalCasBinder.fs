module Client.WorkQueueCanonicalCasBinder

open System
open System.Text.Json
open Client.LedgerStore
open Client.WorkQueueGitLedger
open Client.WorkQueueCanonicalSourceEvidence

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
    | RoleAdmissionUncovered of string
    | SameCasProducerUnproved of string

// Deliberately no Authorized/Ready/Accepted case. Consumers cannot accidentally
// mistake successful original operation lookup for stage/claim authority.
type CanonicalBindAssessment =
    | RecoveryBlocked of MissingAuthority list

module WorkQueueCanonicalCasBinder =
    [<Literal>]
    let private CatalogPath = "records/work-queue/role-catalog.json"

    let private roleGaps (binding: LedgerBinding) (snapshot: LedgerSnapshot) scope =
        match scope with
        | NoRoleTransition -> []
        | NewRoleDemand (roleId, roleVersion)
        | PinnedLaunchRole (_, roleId, roleVersion) ->
            if String.IsNullOrWhiteSpace roleId || roleId = "Manager" || roleVersion < 1 then
                [RoleAdmissionUncovered "Invalid/non-versioned role demand is never admission authority."]
            else
                // Presence of catalog bytes is not an authorization: the
                // Worker11 proposal has a pinned preflight, not the required
                // integrated SAME-CAS decision over role, work and credits.
                let detail =
                    match LedgerStore.readAt binding snapshot CatalogPath with
                    | Ok (None, None) ->
                        "No historical role catalog is provisioned at the verified project head."
                    | Ok (Some _, Some _) ->
                        "Role catalog exists, but its versioned admission/rematch/claim predicate is not an integrated SAME-CAS writer."
                    | Ok _ -> "Role catalog path/blob provenance is inconsistent."
                    | Error error -> "Role catalog cannot be read safely: " + string error
                [RoleAdmissionUncovered detail]

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
