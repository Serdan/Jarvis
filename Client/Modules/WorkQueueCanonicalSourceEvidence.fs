module Client.WorkQueueCanonicalSourceEvidence

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.ReviewWorkflow
open Client.ChallengeWorkflow
open Client.RoleInstructions

// P1 WQ04: a read-only bridge, deliberately separate from the mutable WQ04 producer.
// A caller-supplied name, "source index", counter or witness flag is never authority.
type SourceEventSelector =
    | TaskCreated of taskId: string
    | TaskTransition of taskId: string * attemptId: string * action: string * generation: int
    | ReviewSubmitted of taskId: string * reviewId: Guid * reviewRef: string
                         * sourceVersion: int * reviewedSha: string * targetRef: string
    | AcceptedReview of taskId: string * reviewId: Guid * reviewRef: string
                      * sourceVersion: int * decisionVersion: int * reviewedSha: string * targetRef: string
    | ChallengeTransition of taskId: string * challengeId: Guid * action: string
    | IntegrationState of taskId: string * submissionId: Guid * reviewId: Guid
                         * submittedSha: string * targetRef: string * status: string
    | RoleMembership of agentId: string * roleId: string * configVersion: int * roleVersion: int

type CanonicalEventValue =
    | CreatedTask of ReviewTask
    | Lifecycle of TaskLifecycleEvent
    | ReviewRequested of BranchReview
    | ReviewVerdict of BranchReview
    | Challenge of ChallengeEvent
    | Integration of IntegrationSubmission
    | AttachedRole of AgentInstructionConfig * RoleDefinition

// No Authorized member exists. The source remote, original host job/push journal,
// and separate project-CAS state do not commit atomically with the orchestration ledger.
type SourceCoverage =
    | IncompleteCoverage of string

type CanonicalEventEvidence =
    { ProjectId: Guid
      LedgerId: Guid
      Epoch: int
      SnapshotHead: string
      SnapshotHighWater: int64
      OriginHead: string
      OperationId: Guid
      OperationSequence: int64
      OperationKind: string
      OperationActor: string
      RequestHash: string
      OriginalResultHash: string
      EntityPath: string
      EntityBlobOid: string
      EntitySha256: string
      Event: CanonicalEventValue
      Coverage: SourceCoverage }

// The caller's receipt/hash and snapshot are assertions checked against a freshly
// fetched, trusted canonical head, not grants of authority.
type CanonicalSourceAnchor =
    { SnapshotHead: string
      SnapshotHighWater: int64
      OriginalOperationId: Guid
      OriginalRequestHash: string }

module WorkQueueCanonicalSourceEvidence =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                              PropertyNameCaseInsensitive = true)

    let private invalid why = LedgerIntegrityError ("Canonical source evidence: " + why)
    let private stale why = StaleEntityVersion ("Canonical source evidence: " + why)
    let private digest (raw: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes raw)
        |> Convert.ToHexString
        |> fun s -> s.ToLowerInvariant()

    let private safeTask (s: string) =
        not (isNull s) && Regex.IsMatch(s, "^[A-Za-z0-9_-]{1,80}$")

    let private asGuidN (id: Guid) = id.ToString("N")

    let private entityPath = function
        | TaskCreated task -> sprintf "tasks/%s/task.json" task
        | TaskTransition (task, _, _, _) -> sprintf "tasks/%s/lifecycle/%%SEQ%%.json" task
        | ReviewSubmitted (task, id, _, _, _, _)
        | AcceptedReview (task, id, _, _, _, _, _) ->
            sprintf "tasks/%s/reviews/%s/review.json" task (asGuidN id)
        | ChallengeTransition (_, id, _) ->
            sprintf "challenges/%s/events/%%SEQ%%.json" (asGuidN id)
        | IntegrationState (task, id, _, _, _, _) ->
            sprintf "tasks/%s/integrations/%s.json" task (asGuidN id)
        | RoleMembership (agent, _, _, _) ->
            let key =
                Convert.ToBase64String(Encoding.UTF8.GetBytes agent)
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_')
            sprintf "records/agent-instructions/%s.json" key

    let private required<'t> (raw: string) : Result<'t, LedgerFailure> =
        try
            use doc = JsonDocument.Parse(raw)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                Error(invalid "Entity is not a JSON object.")
            else
                let unique = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let members = doc.RootElement.EnumerateObject() |> Seq.toList
                let expectedSchema =
                    if typeof<'t> = typeof<RoleDefinition>
                       || typeof<'t> = typeof<AgentInstructionConfig> then 1
                    elif typeof<'t> = typeof<BranchReview> then 3
                    else 2
                let mutable schema = Unchecked.defaultof<JsonElement>
                let schemaValid =
                    doc.RootElement.TryGetProperty("schemaVersion", &schema)
                    && schema.ValueKind = JsonValueKind.Number
                    && schema.GetInt32() = expectedSchema
                let required =
                    typeof<'t>.GetProperties()
                    |> Array.forall (fun memberInfo ->
                        let field = options.PropertyNamingPolicy.ConvertName(memberInfo.Name)
                        members |> List.exists (fun m -> m.Name = field))
                if not schemaValid || not (members |> List.forall (fun m -> unique.Add m.Name))
                   || not required then
                    Error(invalid "Entity schema is unsupported or typed members are duplicate/missing.")
                else
                    let value = JsonSerializer.Deserialize<'t>(raw, options)
                    if isNull (box value) then Error(invalid "Null typed entity.")
                    else Ok value
        with ex -> Error(invalid ("Malformed typed event: " + ex.Message))

    let private receiptChanges (binding: LedgerBinding) (operation: LedgerOperation) =
        try
            use request = JsonDocument.Parse(operation.CanonicalRequestJson)
            let requestRoot = request.RootElement
            let reqText (field: string) = requestRoot.GetProperty(field).GetString()
            let reqSequence = requestRoot.GetProperty("schemaVersion").GetInt32()
            let requestValid =
                reqSequence = 2
                && reqText "projectId" = binding.ProjectId.ToString("D")
                && reqText "operationId" = operation.OperationId
                && reqText "actor" = operation.Actor
                && reqText "kind" = operation.Type
                && not (String.IsNullOrWhiteSpace(reqText "request"))
            if not requestValid then Error(invalid "Operation request envelope contradicts its canonical actor/type/project.")
            else
                use doc = JsonDocument.Parse(operation.OriginalResultJson)
                let root = doc.RootElement
                if root.GetProperty("schemaVersion").GetInt32() <> 2
                   || root.GetProperty("operationId").GetString() <> operation.OperationId
                   || root.GetProperty("sequence").GetInt64() <> operation.Sequence
                   || root.GetProperty("kind").GetString() <> operation.Type then
                    Error(invalid "Original operation receipt disagrees with operation identity or sequence.")
                else
                    let changes =
                        root.GetProperty("changes").EnumerateArray()
                        |> Seq.map (fun item ->
                            item.GetProperty("path").GetString(),
                            item.GetProperty("contentSha256").GetString(),
                            item.GetProperty("beforeOid"))
                        |> Seq.toList
                    let paths = changes |> List.map (fun (p, _, _) -> p)
                    if changes.IsEmpty || changes.Length > 256
                       || paths <> (operation.Paths |> Array.toList)
                       || paths.Length <> (paths |> List.distinct |> List.length)
                       || paths |> List.exists String.IsNullOrWhiteSpace then
                        Error(invalid "Original receipt paths are absent, duplicated or differ from the operation.")
                    elif changes |> List.exists (fun (_, sha, _) ->
                        isNull sha || not (Regex.IsMatch(sha, "^[0-9a-f]{64}$"))) then
                        Error(invalid "Original receipt contains an invalid entity hash.")
                    else Ok (changes |> List.map (fun (path, sha, _) -> path, sha))
        with ex -> Error(invalid ("Malformed original operation receipt: " + ex.Message))

    let private readRequired binding snapshot path = result {
        let! raw, oid = LedgerStore.readAt binding snapshot path
        match raw, oid with
        | Some content, Some hash -> return content, hash
        | _ -> return! Error(invalid ("Missing canonical event: " + path))
    }

    let private sourceUnproven =
        IncompleteCoverage(
            "Verified original orchestration Git operation and immutable entity only. " +
            "No atomic source Git / project WorkQueue CAS, original host mutating-job and " +
            "remote-push journal, source-ref protection or integration gate proof. " +
            "Do not authorize PublishStage, ClaimNext, or integration from this evidence.")

    let private validateEvent selector (operation: LedgerOperation)
                              (originRaw: string) (currentRaw: string) binding originSnapshot snapshot =
        let reject () = Error(invalid "Typed event identity, decision or source ancestry is inconsistent.")
        let sameOrStale test = if test then Ok () else Error(stale "Canonical event was superseded.")
        match selector with
        | TaskCreated task ->
            match required<ReviewTask> originRaw, required<ReviewTask> currentRaw with
            | Ok created, Ok current
                when operation.Type = "CreateTask"
                     && created.TaskId = task && created.Version = 1
                     && created.SpecVersion = 1 && created.Lifecycle = "Open"
                     && isNull created.AttemptId
                     && current.TaskId = task && current.Version >= created.Version
                     && current.Lifecycle = "Open" && isNull current.AttemptId ->
                Ok(CreatedTask created)
            | Error e, _ | _, Error e -> Error e
            | _ -> reject ()
        | TaskTransition (task, attempt, action, generation) ->
            match required<TaskLifecycleEvent> originRaw with
            | Error e -> Error e
            | Ok item ->
                if item.TaskId <> task || item.AttemptId <> attempt
                   || item.OperationId <> operation.OperationId
                   || item.Sequence <> operation.Sequence
                   || item.ActorId <> operation.Actor || item.Action <> action
                   || operation.Type <> action || item.NewGeneration <> generation
                   || originRaw <> currentRaw then reject ()
                else
                    result {
                        let path = sprintf "tasks/%s/task.json" task
                        let! raw, _ = readRequired binding originSnapshot path
                        let! recordedTask = required<ReviewTask> raw
                        if recordedTask.TaskId <> task
                           || (not (String.IsNullOrWhiteSpace item.NewLifecycle)
                               && recordedTask.Lifecycle <> item.NewLifecycle) then
                            return! reject ()
                        return Lifecycle item
                    }
        | ReviewSubmitted (task, id, expectedRef, sourceVersion, sha, target) ->
            match required<BranchReview> originRaw, required<BranchReview> currentRaw with
            | Ok first, Ok current ->
                let managed = sprintf "refs/heads/jarvis-review/%s/%s/r%d" task (asGuidN id) sourceVersion
                if operation.Type <> "SubmitBranchReview"
                   || first.ReviewId <> asGuidN id || first.TaskId <> task
                   || first.Status <> "Unclaimed" || first.Version <> 1
                   || first.SourceVersion <> sourceVersion || first.ReviewRef <> managed
                   || first.ReviewRef <> expectedRef || first.CreatedSequence <> operation.Sequence
                   || first.OwnerAgentId <> operation.Actor
                   || first.ReviewedHeadOid <> sha || not (LedgerStore.validSourceCommitOid sha)
                   || first.TargetRef <> target then reject ()
                else
                    match sameOrStale
                            (current.ReviewId = first.ReviewId && current.TaskId = task
                             && current.SourceVersion = first.SourceVersion
                             && current.ReviewRef = first.ReviewRef
                             && current.ReviewedHeadOid = sha && current.TargetRef = target
                             && current.Status = "Unclaimed" && current.Version = first.Version) with
                    | Ok () -> Ok(ReviewRequested current)
                    | Error e -> Error e
            | Error e, _ | _, Error e -> Error e
        | AcceptedReview (task, id, expectedRef, sourceVersion, decisionVersion, sha, target) ->
            match required<BranchReview> originRaw, required<BranchReview> currentRaw with
            | Ok prior, Ok current ->
                let key = asGuidN id
                let expectedManagedRef = sprintf "refs/heads/jarvis-review/%s/%s/r%d" task key sourceVersion
                if operation.Type <> "DecideBranchReview" || prior.ReviewId <> key || prior.TaskId <> task
                   || prior.Status <> "Accepted" || prior.DecisionSequence <> operation.Sequence
                   || prior.ReviewerId <> operation.Actor || prior.SourceVersion <> sourceVersion
                   || prior.Version <> decisionVersion || prior.ReviewRef <> expectedManagedRef
                   || expectedRef <> expectedManagedRef || prior.ReviewedHeadOid <> sha
                   || not (LedgerStore.validSourceCommitOid sha) || prior.TargetRef <> target then reject ()
                else
                    match sameOrStale
                            (current.Status = "Accepted" && current.DecisionSequence = prior.DecisionSequence
                             && current.ReviewId = prior.ReviewId && current.TaskId = prior.TaskId
                             && current.SourceVersion = prior.SourceVersion && current.ReviewRef = prior.ReviewRef
                             && current.ReviewedHeadOid = prior.ReviewedHeadOid
                             && current.TargetRef = prior.TargetRef && current.Version >= prior.Version) with
                    | Ok () -> Ok(ReviewVerdict current)
                    | Error e -> Error e
            | Error e, _ | _, Error e -> Error e
        | ChallengeTransition (task, id, action) ->
            match required<ChallengeEvent> originRaw with
            | Error e -> Error e
            | Ok item ->
                let challengePath = sprintf "challenges/%s/challenge.json" (asGuidN id)
                let check = result {
                    let! originalChallengeRaw, _ = readRequired binding originSnapshot challengePath
                    let! originalChallenge = required<AgentChallenge> originalChallengeRaw
                    let! currentChallengeRaw, _ = readRequired binding snapshot challengePath
                    let! currentChallenge = required<AgentChallenge> currentChallengeRaw
                    if originalChallenge.TaskId <> task
                       || originalChallenge.ChallengeId <> asGuidN id
                       || currentChallenge.TaskId <> originalChallenge.TaskId
                       || currentChallenge.ChallengeId <> originalChallenge.ChallengeId
                       || (action = "Created" && originalChallenge.IssuerAgentId <> operation.Actor) then
                        return! reject ()
                    return ()
                }
                match check with
                | Error e -> Error e
                | Ok () ->
                    if item.ChallengeId <> asGuidN id
                       || item.Sequence <> operation.Sequence || item.ActorId <> operation.Actor
                       || item.Action <> action
                       || not (operation.Type = action || (operation.Type = "CreateChallenge" && action = "Created"))
                       || originRaw <> currentRaw then reject ()
                    else Ok(Challenge item)
        | IntegrationState (task, id, reviewId, sha, target, status) ->
            match required<IntegrationSubmission> originRaw, required<IntegrationSubmission> currentRaw with
            | Ok prior, Ok current ->
                let kind =
                    match status with
                    | "Pending" -> "SubmitIntegration"
                    | "Integrated" -> "CompleteIntegration"
                    | _ -> ""
                if kind = "" || operation.Type <> kind || prior.SubmissionId <> asGuidN id
                   || prior.TaskId <> task || prior.ReviewId <> asGuidN reviewId
                   || prior.SubmittedCommit <> sha || prior.ReviewedHeadOid <> sha
                   || not (LedgerStore.validSourceCommitOid sha)
                   || prior.TargetRef <> target || prior.Status <> status
                   || prior.ReviewRef <> sprintf "refs/heads/jarvis-review/%s/%s/r%d" task (asGuidN reviewId) prior.ReviewVersion
                   || prior.ReviewVersion < 1
                   || prior.IntegrationRef <> sprintf "refs/heads/jarvis-integration/%s" (asGuidN id)
                   || (status = "Pending" && prior.CreatedSequence <> operation.Sequence)
                   || (status = "Integrated" && prior.IntegratedCommit <> sha) then reject ()
                else
                    result {
                        // A submission is not proof of its own Accepted review.
                        // Resolve that original canonical review separately at
                        // the integration operation's introducing commit.
                        let path = sprintf "tasks/%s/reviews/%s/review.json" task (asGuidN reviewId)
                        let! reviewRaw, _ = readRequired binding originSnapshot path
                        let! review = required<BranchReview> reviewRaw
                        if review.TaskId <> task || review.ReviewId <> asGuidN reviewId
                           || review.Status <> "Accepted" || review.DecisionSequence < 1L
                           || review.ReviewRef <> prior.ReviewRef
                           || review.SourceVersion <> prior.ReviewVersion
                           || review.ReviewedHeadOid <> sha || review.TargetRef <> target then
                            return! reject ()
                        do! sameOrStale (current.Status = prior.Status && current.SubmissionId = prior.SubmissionId
                             && current.TaskId = prior.TaskId && current.ReviewId = prior.ReviewId
                             && current.ReviewRef = prior.ReviewRef && current.ReviewVersion = prior.ReviewVersion
                             && current.SubmittedCommit = prior.SubmittedCommit
                             && current.IntegrationRef = prior.IntegrationRef && current.TargetRef = prior.TargetRef
                             && current.IntegratedCommit = prior.IntegratedCommit)
                        return Integration current
                    }
            | Error e, _ | _, Error e -> Error e
        | RoleMembership (agent, roleId, configVersion, roleVersion) ->
            match required<AgentInstructionConfig> originRaw, required<AgentInstructionConfig> currentRaw with
            | Ok prior, Ok current ->
                let scoped = result {
                    if operation.Type <> "AttachRole" || prior.AgentId <> agent
                       || prior.Version <> configVersion || prior.UpdatedSequence <> operation.Sequence
                       || not (List.contains roleId prior.RoleIds) then
                        return! reject ()
                    if prior <> current then
                        return! Error(stale "Role membership version drifted after original AttachRole.")
                    let! resolution = RoleInstructions.resolveAt binding snapshot agent
                    if resolution.SnapshotCommit <> snapshot.Commit
                       || resolution.AgentConfigVersion <> configVersion
                       || not (List.contains roleId resolution.RoleIds) then
                        return! Error(stale "Role assignment no longer applies at the pinned head.")
                    let path = sprintf "records/roles/%s.json" roleId
                    let! originalRoleRaw, _ = readRequired binding originSnapshot path
                    let! originalRole = required<RoleDefinition> originalRoleRaw
                    let! raw, _ = readRequired binding snapshot path
                    let! role = required<RoleDefinition> raw
                    if originalRole.RoleId <> roleId || originalRole.Version <> roleVersion
                       || role.RoleId <> roleId || role.Version <> roleVersion
                       || not (resolution.Documents |> List.exists (fun layer ->
                           layer.Scope = "role" && layer.Identifier = roleId
                           && layer.Version = roleVersion)) then
                        return! Error(stale "Role definition is missing or changed.")
                    return AttachedRole(current, role)
                }
                scoped
            | Error e, _ | _, Error e -> Error e

    /// Proof from one exact remote-backed, trusted Git ledger head. The source
    /// snapshot and payload/receipt are never selected by mutable caller ID semantics.
    let read (binding: LedgerBinding) (anchor: CanonicalSourceAnchor)
             (selector: SourceEventSelector) : Result<CanonicalEventEvidence, LedgerFailure> = result {
        if anchor.OriginalOperationId = Guid.Empty
           || not (LedgerStore.validSourceCommitOid anchor.SnapshotHead)
           || String.IsNullOrWhiteSpace anchor.OriginalRequestHash
           || not (Regex.IsMatch(anchor.OriginalRequestHash, "^[0-9a-f]{64}$")) then
            return! Error(InvalidBinding "Pinned canonical head, original UUID and receipt hash required.")
        let! snapshot = LedgerStore.fetch binding
        if snapshot.Commit <> anchor.SnapshotHead
           || snapshot.Header.NextSequence - 1L <> anchor.SnapshotHighWater then
            return! Error(stale "High-water or canonical head drifted; refresh both from Git.")
        let taskId =
            match selector with
            | TaskCreated task
            | TaskTransition (task, _, _, _)
            | ReviewSubmitted (task, _, _, _, _, _)
            | AcceptedReview (task, _, _, _, _, _, _)
            | ChallengeTransition (task, _, _)
            | IntegrationState (task, _, _, _, _, _) -> Some task
            | RoleMembership _ -> None
        if taskId |> Option.exists (safeTask >> not) then
            return! Error(InvalidBinding "Invalid canonical task selector.")
        let! maybeOperation = LedgerStore.getOperationAt binding snapshot anchor.OriginalOperationId
        let! operation =
            maybeOperation |> Option.map Ok
            |> Option.defaultValue (Error(invalid "Original operation is absent at this canonical head."))
        if operation.RequestHash <> anchor.OriginalRequestHash
           || operation.Sequence > anchor.SnapshotHighWater then
            return! Error(invalid "Replayed UUID has a different payload or a future sequence.")
        let! originHead = LedgerStore.getOperationOriginAt binding snapshot operation
        let! origin = LedgerStore.verifiedHistoricalSnapshot binding originHead
        if origin.Header.NextSequence <> operation.Sequence + 1L then
            return! Error(invalid "Originating commit and original operation sequence disagree.")
        let! receipt = receiptChanges binding operation
        // Every original receipt change, not merely the selected entity, must
        // match its actual blob in the originating commit.
        for (path, sha) in receipt do
            let! raw, _ = readRequired binding origin path
            if digest raw <> sha then
                return! Error(invalid ("Original operation receipt has an altered entity: " + path))
        let path = (entityPath selector).Replace("%SEQ%", string operation.Sequence)
        if not (receipt |> List.exists (fun (p, _) -> p = path)) then
            return! Error(invalid "Typed event path is not an original operation effect.")
        let! original, oid = readRequired binding origin path
        let! current, currentOid = readRequired binding snapshot path
        // Mutable records may advance only through their own separately checked
        // canonical state transitions. Immutable event paths must remain identical.
        let! value = validateEvent selector operation original current binding origin snapshot
        match selector with
        | TaskTransition _ | ChallengeTransition _ when oid <> currentOid ->
            return! Error(invalid "Immutable audit event changed after its original commit.")
        | _ -> ()
        let! newest = LedgerStore.fetch binding
        if newest.Commit <> snapshot.Commit then
            return! Error(stale "Another clone advanced the canonical head during proof.")
        return
            { ProjectId = binding.ProjectId; LedgerId = binding.LedgerId; Epoch = binding.Epoch
              SnapshotHead = snapshot.Commit
              SnapshotHighWater = anchor.SnapshotHighWater
              OriginHead = originHead
              OperationId = anchor.OriginalOperationId; OperationSequence = operation.Sequence
              OperationKind = operation.Type; OperationActor = operation.Actor
              RequestHash = operation.RequestHash; OriginalResultHash = operation.OriginalResultHash
              EntityPath = path; EntityBlobOid = oid; EntitySha256 = digest original
              Event = value; Coverage = sourceUnproven }
    }
