module Client.ChallengeWorkflow

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography
open FsToolkit.ErrorHandling
open Client.LedgerStore
open Client.ReviewWorkflow

[<CLIMutable>]
type ChallengeConditionSpec =
    { OwnerKind: string
      OwnerAgentId: string
      SuggestedAgentId: string
      Description: string }

[<CLIMutable>]
type ChallengeCondition =
    { ConditionId: string
      OwnerKind: string
      OwnerAgentId: string
      SuggestedAgentId: string
      Description: string
      Status: string
      Evidence: string
      Reason: string
      UpdatedAtUtc: string }

[<CLIMutable>]
type AgentChallenge =
    { ChallengeId: string
      IssuerAgentId: string
      Title: string
      TaskId: string
      Version: int
      CompletionPolicy: string
      Status: string
      Conditions: ChallengeCondition list
      CreatedAtUtc: string
      UpdatedAtUtc: string }

[<CLIMutable>]
type ChallengeEvent =
    { ChallengeId: string
      ConditionId: string
      ActorId: string
      Action: string
      BeforeStatus: string
      AfterStatus: string
      BeforeOwnerAgentId: string
      AfterOwnerAgentId: string
      Evidence: string
      Reason: string
      Sequence: int64
      OccurredAtUtc: string }

[<CLIMutable>]
type ChallengeSubscription =
    { AgentId: string
      Enabled: bool
      Version: int
      UpdatedAtUtc: string }

[<CLIMutable>]
type ChallengeDetails =
    { SnapshotCommit: string
      Challenge: AgentChallenge
      History: ChallengeEvent list
      NextEventSequence: int64 }

[<CLIMutable>]
type ChallengeSummary =
    { ChallengeId: string
      IssuerAgentId: string
      Title: string
      TaskId: string
      Status: string
      Version: int
      TotalConditions: int
      PendingConditions: int
      BlockedConditions: int
      SatisfiedConditions: int
      OwnedUnresolvedConditionIds: string list }

[<CLIMutable>]
type ChallengeList =
    { SnapshotCommit: string
      Challenges: ChallengeSummary list
      NextCursor: string }

[<CLIMutable>]
type ChallengeCreated =
    { ChallengeId: string
      ConditionIds: string list
      Receipt: LedgerWrite }

[<RequireQualifiedAccess>]
type ChallengeOwner =
    | Unassigned
    | Agent of string
    | Operator

module ChallengeWorkflow =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private json value = JsonSerializer.Serialize(value, options)
    let private stored<'t> (value: 't) =
        let body = json value
        if not (body.StartsWith("{", StringComparison.Ordinal)) then invalidArg "value" "Expected record."
        let schema =
            if List.contains typeof<'t>.Name [ "ReviewAgent"; "ReviewAttempt"; "BranchReview" ] then 3 else 2
        $"{{\"schemaVersion\":{schema}," + body.Substring(1)

    let private path (challengeId: Guid) =
        "challenges/" + challengeId.ToString("N") + "/challenge.json"
    let private eventPath (challengeId: Guid) sequence =
        "challenges/" + challengeId.ToString("N") + "/events/" + string sequence + ".json"
    let private subscriptionPath (agent: string) =
        let safe = Convert.ToBase64String(Encoding.UTF8.GetBytes agent)
                       .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        "agents/" + safe + ".challenge-subscription.json"
    let private taskPath taskId = "tasks/" + taskId + "/task.json"
    let private agentPath (agentId: string) =
        let safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId))
                       .TrimEnd('=').Replace('+', '-').Replace('/', '_')
        "agents/" + safe + ".json"
    let private guidN (text: string) =
        match Guid.TryParseExact(text, "N") with
        | true, id when id <> Guid.Empty -> Some id
        | _ -> None
    let private agentId (name: string) =
        not (String.IsNullOrWhiteSpace name) && Encoding.UTF8.GetByteCount name <= 120
    // The persisted discriminator is explicit for new conditions; older schema-2
    // challenges had only OwnerAgentId and are interpreted without rewriting them.
    let private owner kind agent =
        match kind with
        | null | "" ->
            if String.IsNullOrWhiteSpace agent then Some ChallengeOwner.Unassigned
            else Some(ChallengeOwner.Agent agent)
        | "Unassigned" when String.IsNullOrWhiteSpace agent -> Some ChallengeOwner.Unassigned
        | "Agent" when not (String.IsNullOrWhiteSpace agent) -> Some(ChallengeOwner.Agent agent)
        | "Operator" when String.IsNullOrWhiteSpace agent -> Some ChallengeOwner.Operator
        | _ -> None

    let private ownerOf (condition: ChallengeCondition) =
        owner condition.OwnerKind condition.OwnerAgentId

    let private ownerKind kind agent =
        match owner kind agent with
        | Some ChallengeOwner.Operator -> "Operator"
        | Some ChallengeOwner.Unassigned -> "Unassigned"
        | Some(ChallengeOwner.Agent _) -> "Agent"
        | None -> invalidArg "condition" "Invalid condition owner."

    let private text maxBytes (value: string) =
        not (String.IsNullOrWhiteSpace value) && Encoding.UTF8.GetByteCount value <= maxBytes
    let private taskId (value: string) =
        String.IsNullOrEmpty value
        || (value.Length <= 80 && value |> Seq.forall (fun c ->
            Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'))
    let private change path oid value : LedgerFileChange =
        { Path = path; ExpectedBlobOid = oid; Content = stored value }
    let private intent operationId actor kind payload : LedgerIntent =
        { OperationId = operationId; Actor = actor
          Kind = kind; RequestJson = json payload }

    // UUIDs are reproducible from a caller-stable operation ID. Retried Create
    // operations retain the identical challenge AND condition IDs on every client.
    let private conditionId (challengeId: Guid) (index: int) =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            challengeId.ToString("N") + ":condition:" + string index))
        Guid(bytes.AsSpan(0, 16)).ToString("N")

    let private status (conditions: ChallengeCondition list) =
        if conditions |> List.exists (fun c -> c.Status = "Blocked") then "Blocked"
        elif conditions |> List.forall (fun c -> c.Status = "Satisfied") then "Satisfied"
        else "Pending"

    let private check (challenge: AgentChallenge) =
        let ids = challenge.Conditions |> List.map _.ConditionId
        challenge.Version > 0
        && (guidN challenge.ChallengeId).IsSome
        && agentId challenge.IssuerAgentId
        && text 2048 challenge.Title
        && taskId challenge.TaskId
        && challenge.CompletionPolicy = "All"
        && List.contains challenge.Status [ "Pending"; "Blocked"; "Satisfied"; "Cancelled" ]
        && not (List.isEmpty ids)
        && ids.Length <= 32
        && ids.Length = (ids |> List.distinct |> List.length)
        && (challenge.Conditions |> List.forall (fun c ->
            (guidN c.ConditionId).IsSome
            && (ownerOf c).IsSome
            && (String.IsNullOrEmpty c.OwnerAgentId || agentId c.OwnerAgentId)
            && (c.OwnerKind <> "Operator" || String.IsNullOrWhiteSpace c.SuggestedAgentId)
            && (String.IsNullOrEmpty c.SuggestedAgentId || agentId c.SuggestedAgentId)
            && text 2048 c.Description
            && List.contains c.Status [ "Pending"; "Blocked"; "Satisfied" ]
            && (c.Status <> "Blocked" || text 4096 c.Reason)))
        && (challenge.Status = "Cancelled" || challenge.Status = status challenge.Conditions)

    let private readChallenge binding snapshot id = result {
        let! contents, oid = LedgerStore.readAt binding snapshot (path id)
        match contents, oid with
        | Some content, Some blob ->
            try
                use document = JsonDocument.Parse(content)
                let schema = document.RootElement.GetProperty("schemaVersion").GetInt32()
                let value = JsonSerializer.Deserialize<AgentChallenge>(content, options)
                if schema <> 2 || isNull (box value) || value.ChallengeId <> id.ToString("N")
                   || not (check value) then
                    return! Error(LedgerIntegrityError "Invalid or corrupted challenge ledger entity.")
                return value, blob
            with ex ->
                return! Error(LedgerIntegrityError ("Cannot deserialize challenge: " + ex.Message))
        | None, None -> return! Error(StaleEntityVersion "Challenge does not exist.")
        | _ -> return! Error(LedgerIntegrityError "Inconsistent challenge entity and blob.")
    }

    let private registered binding snapshot actor = result {
        let! content, _ = LedgerStore.readAt binding snapshot (agentPath actor)
        match content with
        | Some content ->
            try
                use doc = JsonDocument.Parse(content)
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 3
                   || doc.RootElement.GetProperty("agentId").GetString() <> actor then
                    return! Error(LedgerIntegrityError "Agent registration has invalid identity or schema.")
            with ex ->
                return! Error(LedgerIntegrityError ("Invalid registered agent: " + ex.Message))
        | None -> return! Error(ReviewAccessDenied "Challenge actor or condition owner is not registered.")
    }

    let private existingTask binding snapshot id = result {
        if not (String.IsNullOrEmpty id) then
            let! content, _ = LedgerStore.readAt binding snapshot (taskPath id)
            match content with
            | Some _ -> ()
            | None -> return! Error(StaleEntityVersion "Challenge task context no longer exists.")
    }

    /// Keep list previews bounded; full descriptions and evidence remain in
    /// canonical challenge records and audit events.
    let private compact maxBytes (value: string) =
        let input =
            if isNull value then ""
            else value.Replace("\r", " ").Replace("\n", " ")
        if Encoding.UTF8.GetByteCount(input) <= maxBytes then input
        else
            let builder = StringBuilder()
            let mutable count = 0
            for rune in input.EnumerateRunes() do
                if count + rune.Utf8SequenceLength <= maxBytes - 3 then
                    builder.Append(rune.ToString()) |> ignore
                    count <- count + rune.Utf8SequenceLength
            builder.ToString() + "..."

    let private changed binding operationId actor kind payload
                        (plan: LedgerSnapshot -> Result<LedgerFileChange list, LedgerFailure>) =
        LedgerStore.transact binding (intent operationId actor kind payload) plan

    /// An explicit, per-agent project subscription controls visibility of
    /// unassigned conditions. No Lead-name privilege and no notification state.
    let private subscribedAt binding snapshot actor = result {
        let! body, _ = LedgerStore.readAt binding snapshot (subscriptionPath actor)
        match body with
        | None -> return false
        | Some value ->
            try
                use doc = JsonDocument.Parse(value)
                let sub = JsonSerializer.Deserialize<ChallengeSubscription>(value, options)
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 2
                   || isNull (box sub) || sub.AgentId <> actor || sub.Version < 1 then
                    return! Error(LedgerIntegrityError "Invalid challenge subscription identity or schema.")
                return sub.Enabled
            with ex ->
                return! Error(LedgerIntegrityError ("Invalid challenge subscription: " + ex.Message))
    }

    let setSubscription binding operationId actor enabled =
        if operationId = Guid.Empty || not (agentId actor) then
            Error(InvalidBinding "Subscription requires registered actor and stable operation ID.")
        else
            changed binding operationId actor "SetChallengeSubscription"
                {| enabled = enabled |}
                (fun snapshot -> result {
                    do! registered binding snapshot actor
                    let target = subscriptionPath actor
                    let! previous, blob = LedgerStore.readAt binding snapshot target
                    let! version =
                        match previous with
                        | None -> Ok 1
                        | Some _ ->
                            subscribedAt binding snapshot actor
                            |> Result.map (fun _ ->
                                use doc = JsonDocument.Parse(previous.Value)
                                doc.RootElement.GetProperty("version").GetInt32() + 1)
                    let record =
                        { AgentId = actor; Enabled = enabled; Version = version
                          UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") }
                    return [ change target blob record ]
                })

    let create binding operationId issuer title task
               (definitions: ChallengeConditionSpec list) =
        if operationId = Guid.Empty || not (agentId issuer)
           || not (text 2048 title) || not (taskId task)
           || List.isEmpty definitions || List.length definitions > 32
           || (definitions |> List.exists (fun d ->
               (owner d.OwnerKind d.OwnerAgentId).IsNone
               || (d.OwnerKind = "Operator" && not (String.IsNullOrWhiteSpace d.SuggestedAgentId))
               || (not (String.IsNullOrWhiteSpace d.OwnerAgentId) && not (agentId d.OwnerAgentId))
               || (not (String.IsNullOrWhiteSpace d.SuggestedAgentId) && not (agentId d.SuggestedAgentId))
               || not (text 2048 d.Description))) then
            Error(InvalidBinding "Challenge requires valid issuer, title, optional task, and 1-32 owned conditions.")
        else
            let challengeId = operationId
            let conditions =
                definitions |> List.mapi (fun index definition ->
                    { ConditionId = conditionId challengeId index
                      OwnerKind = ownerKind definition.OwnerKind definition.OwnerAgentId
                      OwnerAgentId = if String.IsNullOrWhiteSpace definition.OwnerAgentId then "" else definition.OwnerAgentId
                      SuggestedAgentId = if String.IsNullOrWhiteSpace definition.SuggestedAgentId then "" else definition.SuggestedAgentId
                      Description = definition.Description
                      Status = "Pending"; Evidence = null; Reason = null
                      UpdatedAtUtc = null })
            let created =
                changed binding operationId issuer "CreateChallenge"
                    ({| title = title; taskId = task; conditions = definitions |})
                    (fun snapshot -> result {
                    do! registered binding snapshot issuer
                    for definition in definitions do
                        if owner definition.OwnerKind definition.OwnerAgentId <> Some ChallengeOwner.Operator
                           && not (String.IsNullOrWhiteSpace definition.OwnerAgentId) then
                            do! registered binding snapshot definition.OwnerAgentId
                        if not (String.IsNullOrWhiteSpace definition.SuggestedAgentId) then
                            do! registered binding snapshot definition.SuggestedAgentId
                    do! existingTask binding snapshot task
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let entity =
                        { ChallengeId = challengeId.ToString("N")
                          IssuerAgentId = issuer; Title = title
                          TaskId = if isNull task then "" else task
                          Version = 1; CompletionPolicy = "All"
                          Status = "Pending"; Conditions = conditions
                          CreatedAtUtc = timestamp; UpdatedAtUtc = timestamp }
                    let event =
                        { ChallengeId = entity.ChallengeId
                          ConditionId = ""; ActorId = issuer; Action = "Created"
                          BeforeStatus = ""; AfterStatus = "Pending"
                          BeforeOwnerAgentId = ""; AfterOwnerAgentId = ""
                          Evidence = ""; Reason = ""; Sequence = snapshot.Header.NextSequence
                          OccurredAtUtc = timestamp }
                    return [ change (path challengeId) None entity
                             change (eventPath challengeId snapshot.Header.NextSequence) None event ]
                })
            created |> Result.map (fun receipt ->
                { ChallengeId = challengeId.ToString("N")
                  ConditionIds = conditions |> List.map _.ConditionId
                  Receipt = receipt })

// Resolve challenge state and dependent attempt wakeups in one Git CAS.
    // Scanning canonical waits is bounded; no side-channel notification is used.
    let private pinned<'t> binding snapshot file schema = result {
        let! content, oid = LedgerStore.readAt binding snapshot file
        match content, oid with
        | Some body, Some blob ->
            try
                use doc = JsonDocument.Parse(body)
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> schema then
                    return! Error(LedgerIntegrityError ("Invalid dependency schema: " + file))
                let record = JsonSerializer.Deserialize<'t>(body, options)
                if isNull (box record) then
                    return! Error(LedgerIntegrityError ("Null dependency entity: " + file))
                return record, blob
            with ex ->
                return! Error(LedgerIntegrityError ("Invalid dependency entity: " + ex.Message))
        | _ -> return! Error(LedgerIntegrityError ("Missing dependency entity: " + file))
    }

    let private wakeDependents binding snapshot (operationId: Guid) actor
                               (completed: AgentChallenge) timestamp = result {
        let prefix = ReviewWorkflow.challengeWaitIndexPrefix completed.ChallengeId
        let! paths = LedgerStore.listAt binding snapshot prefix
        let linkedWaits =
            paths
            |> List.filter (fun p -> p.EndsWith(".json", StringComparison.Ordinal))
            |> List.sort
        let mutable writes : LedgerFileChange list = []
        let mutable claimedOwners = Set.empty<string>
        for indexPath in linkedWaits do
            let! locator, _ = pinned<AttemptChallengeWaitIndex> binding snapshot indexPath 2
            if indexPath <> ReviewWorkflow.waitIndexPath prefix locator.TaskId locator.AttemptId locator.AssignmentGeneration
               || locator.AssignmentGeneration < 1 then
                return! Error(LedgerIntegrityError "Challenge wait-index identity mismatch.")
            let file = $"tasks/{locator.TaskId}/attempts/{locator.AttemptId}/challenge-wait.json"
            let! wait, waitOid = pinned<AttemptChallengeWait> binding snapshot file 2
            if file <> $"tasks/{wait.TaskId}/attempts/{wait.AttemptId}/challenge-wait.json" then
                return! Error(LedgerIntegrityError "Challenge wait identity/path mismatch.")
            if wait.Status = "Waiting" && wait.OwnerAgentId = locator.OwnerAgentId
               && List.contains completed.ChallengeId wait.ChallengeIds then
                let mutable allSatisfied = true
                for dependency in wait.ChallengeIds do
                    match guidN dependency with
                    | None -> return! Error(LedgerIntegrityError "Invalid stored dependency UUID.")
                    | Some id ->
                        let! linked =
                            if dependency = completed.ChallengeId then Ok completed
                            else readChallenge binding snapshot id |> Result.map fst
                        if linked.Status <> "Satisfied" then allSatisfied <- false
                if allSatisfied then
                    let taskPath = $"tasks/{wait.TaskId}/task.json"
                    let attemptPath = $"tasks/{wait.TaskId}/attempts/{wait.AttemptId}.json"
                    let! task, _ = pinned<ReviewTask> binding snapshot taskPath 2
                    let! attempt, attemptOid = pinned<ReviewAttempt> binding snapshot attemptPath 3
                    let! owner, ownerOid =
                        pinned<ReviewAgent> binding snapshot (agentPath wait.OwnerAgentId) 3
                    // Terminal or superseded attempt links are inert.
                    if task.TaskId = wait.TaskId && task.Lifecycle = "InProgress"
                       && task.AttemptId = wait.AttemptId && attempt.TaskId = wait.TaskId
                       && attempt.AttemptId = wait.AttemptId
                       && attempt.AssignmentGeneration = locator.AssignmentGeneration
                       && attempt.OwnerAgentId = wait.OwnerAgentId
                       && attempt.Phase = "Blocked" then
                        let free =
                            isNull (box owner.CurrentAssignment)
                            && owner.Execution = "Stopped"
                            && owner.UnassignedDisposition = "NeedsWork"
                            && not (Set.contains owner.AgentId claimedOwners)
                        if free then
                            let sequence = snapshot.Header.NextSequence
                            let generation = attempt.AssignmentGeneration + 1
                            let reason = "All explicitly linked challenges satisfied"
                            let nextAttempt =
                                { attempt with Version = attempt.Version + 1
                                               Phase = "Queued"
                                               AssignmentGeneration = generation
                                               IssuedSequence = sequence }
                            let nextOwner =
                                { owner with Version = owner.Version + 1
                                             CurrentAssignment = { TaskId = wait.TaskId; AttemptId = wait.AttemptId }
                                             Execution = "Stopped"; UnassignedDisposition = null
                                             StateReason = reason; StateSequence = sequence
                                             StateEnteredAtUtc = timestamp
                                             WorkingTurnStartedAtUtc = null
                                             ActiveSessionId = null; AssociatedSessionIds = [] }
                            let nextWait =
                                { wait with Version = wait.Version + 1
                                            Status = "Requeued"; RequeuedAtUtc = timestamp }
                            let issue : AssignmentIssue =
                                { TaskId = wait.TaskId; AttemptId = wait.AttemptId
                                  Generation = generation; OwnerAgentId = wait.OwnerAgentId
                                  IssuerId = actor; IssuedSequence = sequence
                                  Instructions = reason; Reason = "ChallengeUnblock"
                                  SpecBlobOid = nextAttempt.SpecBlobOid
                                  SourceBaseCommit = nextAttempt.BaseCommit
                                  TargetRef = nextAttempt.TargetRef
                                  SourceRepositoryId = nextAttempt.SourceRepositoryId
                                  SourceRemote = nextAttempt.SourceRemote
                                  SourcePushRemote = nextAttempt.SourcePushRemote
                                  SourceRef = nextAttempt.SourceRef
                                  PreviousGeneration = attempt.AssignmentGeneration }
                            let audit : TaskLifecycleEvent =
                                { TaskId = wait.TaskId; AttemptId = wait.AttemptId
                                  OperationId = operationId.ToString("D")
                                  Sequence = sequence; Action = "RequeueChallengeBlockedAttempt"
                                  ActorId = actor; Reason = reason
                                  PreviousLifecycle = task.Lifecycle; NewLifecycle = task.Lifecycle
                                  PreviousPhase = attempt.Phase; NewPhase = "Queued"
                                  PreviousOwnerId = attempt.OwnerAgentId
                                  NewOwnerId = attempt.OwnerAgentId
                                  PreviousGeneration = attempt.AssignmentGeneration
                                  NewGeneration = generation
                                  ReviewRequestId = null
                                  ReviewDisposition = "Unchanged"; Evidence = null }
                            writes <- [
                                change file (Some waitOid) nextWait
                                change attemptPath (Some attemptOid) nextAttempt
                                change (agentPath owner.AgentId) (Some ownerOid) nextOwner
                                change ($"tasks/{wait.TaskId}/attempts/{wait.AttemptId}/assignments/{generation}.json") None issue
                                change ($"tasks/{wait.TaskId}/lifecycle/{sequence}.json") None audit
                            ] @ writes
                            claimedOwners <- Set.add owner.AgentId claimedOwners
                        else
                            // Persist wake eligibility while the owner is occupied;
                            // it can be reconciled when their slot is later released.
                            writes <- change file (Some waitOid)
                                          { wait with Version = wait.Version + 1; Status = "Eligible" } :: writes
        return List.rev writes
    }
    let private update binding operationId actor challengeId conditionId expectedVersion kind
                       evidence reason =
        match guidN challengeId with
        | None -> Error(InvalidBinding "Challenge ID must be a non-empty UUID.")
        | Some id when operationId = Guid.Empty || not (agentId actor)
                       || expectedVersion < 1 ->
            Error(InvalidBinding "Challenge actor, operation ID, and expected version are required.")
        | Some id ->
            let validEvidence = String.IsNullOrEmpty evidence || Encoding.UTF8.GetByteCount evidence <= 8192
            let validReason = String.IsNullOrEmpty reason || Encoding.UTF8.GetByteCount reason <= 4096
            if not validEvidence || not validReason then
                Error(InvalidBinding "Challenge evidence or reason exceeds its maximum size.")
            else
                changed binding operationId actor kind
                    ({| challengeId = challengeId; conditionId = conditionId; expectedVersion = expectedVersion;
                       evidence = evidence; reason = reason |})
                    (fun snapshot -> result {
                        let! challenge, blob = readChallenge binding snapshot id
                        if challenge.Version <> expectedVersion then
                            return! Error(StaleEntityVersion "Challenge changed; re-read its current version.")
                        if List.contains challenge.Status [ "Satisfied"; "Cancelled" ] then
                            return! Error(StaleEntityVersion "Completed and cancelled challenges cannot be mutated.")
                        if kind <> "RespondOperatorCondition" then
                            do! registered binding snapshot actor
                        let priorState = challenge.Status
                        let timestamp = DateTimeOffset.UtcNow.ToString("O")
                        let target =
                            challenge.Conditions |> List.tryFind (fun c -> c.ConditionId = conditionId)
                        let! nextConditions, eventCondition =
                            match kind with
                            | "CancelChallenge" when actor = challenge.IssuerAgentId
                                && text 4096 reason ->
                                Ok(challenge.Conditions, "")
                            | "ReopenChallengeCondition" when actor = challenge.IssuerAgentId
                                                           && text 4096 reason ->
                                match target with
                                | Some condition when condition.Status = "Blocked" ->
                                    Ok(challenge.Conditions |> List.map (fun c ->
                                        if c.ConditionId = conditionId then
                                            { c with Status = "Pending"; UpdatedAtUtc = timestamp }
                                        else c), conditionId)
                                | _ -> Error(StaleEntityVersion "Only an existing Blocked condition can be reopened.")
                            | "RespondOperatorCondition" ->
                                match target with
                                | Some condition when ownerOf condition = Some ChallengeOwner.Operator
                                                      && condition.Status = "Pending"
                                                      && text 8192 evidence ->
                                    Ok(challenge.Conditions |> List.map (fun c ->
                                        if c.ConditionId = conditionId then
                                            { c with Status = "Satisfied"; Evidence = evidence
                                                     Reason = null; UpdatedAtUtc = timestamp }
                                        else c), conditionId)
                                | Some condition when ownerOf condition <> Some ChallengeOwner.Operator ->
                                    Error(ReviewAccessDenied "This challenge condition is not assigned to the human operator.")
                                | _ -> Error(StaleEntityVersion "Operator condition is not pending or response is empty.")
                            | "ResolveChallengeCondition" ->
                                match target with
                                | Some condition when ownerOf condition = Some(ChallengeOwner.Agent actor)
                                                      && condition.Status = "Pending"
                                                      && (text 8192 evidence || text 4096 reason) ->
                                    let blocked = text 4096 reason
                                    let next = challenge.Conditions |> List.map (fun c ->
                                        if c.ConditionId = conditionId then
                                            { c with Status = if blocked then "Blocked" else "Satisfied"
                                                     Evidence = evidence; Reason = reason
                                                     UpdatedAtUtc = timestamp }
                                        else c)
                                    Ok(next, conditionId)
                                | Some _ when ownerOf target.Value <> Some(ChallengeOwner.Agent actor) ->
                                    Error(ReviewAccessDenied "Only the condition owner can resolve their condition.")
                                | _ -> Error(StaleEntityVersion "Condition is not pending or lacks evidence/reason.")
                            | "CancelChallenge" | "ReopenChallengeCondition" ->
                                Error(ReviewAccessDenied "Only the issuer may cancel or reopen a challenge.")
                            | _ -> Error(InvalidBinding "Unsupported challenge mutation.")
                        let nextStatus =
                            if kind = "CancelChallenge" then "Cancelled" else status nextConditions
                        let nextRecord =
                            { challenge with
                                Version = challenge.Version + 1
                                Status = nextStatus; Conditions = nextConditions
                                UpdatedAtUtc = timestamp }
                        let event =
                            { ChallengeId = challengeId
                              ConditionId = eventCondition; ActorId = actor
                              Action = kind; BeforeStatus = priorState
                              AfterStatus = nextStatus
                              BeforeOwnerAgentId = target |> Option.map _.OwnerAgentId |> Option.defaultValue ""
                              AfterOwnerAgentId = target |> Option.map _.OwnerAgentId |> Option.defaultValue ""
                              Evidence = evidence
                              Reason = reason; Sequence = snapshot.Header.NextSequence
                              OccurredAtUtc = timestamp }
                        // The one Git ledger transaction records only challenge
                        // state and its immutable event. Notification settings,
                        // recipient blocks, and delivery cannot affect progress.
                        let! wakeups =
                            if nextStatus = "Satisfied" && priorState <> "Satisfied" then
                                wakeDependents binding snapshot operationId actor nextRecord timestamp
                            else Ok []
                        return [ change (path id) (Some blob) nextRecord
                                 change (eventPath id snapshot.Header.NextSequence) None event ] @ wakeups
                    })

    // Caller is the authenticated local TUI, never a remote agent command.
    // One response resolves the operator input request; it is not a chat message.
    let respondOperator binding operationId operatorActor challengeId conditionId version response =
        update binding operationId operatorActor challengeId conditionId version
            "RespondOperatorCondition" response ""

    let resolve binding operationId actor challengeId conditionId version evidence blockedReason =
        update binding operationId actor challengeId conditionId version
            "ResolveChallengeCondition" evidence blockedReason
    let reopen binding operationId issuer challengeId conditionId version reason =
        update binding operationId issuer challengeId conditionId version
            "ReopenChallengeCondition" "" reason
    let cancel binding operationId issuer challengeId version reason =
        update binding operationId issuer challengeId "" version "CancelChallenge" "" reason

    /// The issuer may assign/reassign a Pending condition directly. A
    /// subscriber may dispatch only an unassigned condition. Both routes use
    /// the challenge version as one CAS precondition and preserve its ID.
    let assignCondition binding operationId actor challengeId conditionId
                        expectedVersion newOwner =
        match guidN challengeId with
        | None -> Error(InvalidBinding "Invalid challenge ID.")
        | Some id when operationId = Guid.Empty || not (agentId actor)
                       || not (agentId newOwner) || expectedVersion < 1 ->
            Error(InvalidBinding "Assignment requires actor, target owner, ID and exact version.")
        | Some id ->
            changed binding operationId actor "AssignChallengeCondition"
                {| challengeId = challengeId; conditionId = conditionId
                   expectedVersion = expectedVersion; newOwner = newOwner |}
                (fun snapshot -> result {
                    do! registered binding snapshot actor
                    do! registered binding snapshot newOwner
                    let! challenge, blob = readChallenge binding snapshot id
                    if challenge.Version <> expectedVersion
                       || List.contains challenge.Status [ "Satisfied"; "Cancelled" ] then
                        return! Error(StaleEntityVersion "Challenge version or terminal status changed before dispatch.")
                    let! subscribed = subscribedAt binding snapshot actor
                    let target = challenge.Conditions |> List.tryFind (fun c -> c.ConditionId = conditionId)
                    let! condition =
                        match target with
                        | Some c when c.Status <> "Pending" ->
                            Error(StaleEntityVersion "Only Pending conditions can be assigned.")
                        | Some c when ownerOf c = Some ChallengeOwner.Operator ->
                            Error(ReviewAccessDenied "Operator input cannot be reassigned or dispatched.")
                        | Some c when actor = challenge.IssuerAgentId ||
                                      (subscribed && ownerOf c = Some ChallengeOwner.Unassigned) -> Ok c
                        | Some _ -> Error(ReviewAccessDenied "Only issuer or a subscribed dispatcher of unassigned work may assign.")
                        | None -> Error(InvalidBinding "Unknown condition ID.")
                    if condition.OwnerAgentId = newOwner then
                        return! Error(StaleEntityVersion "Condition already has this assignee.")
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let conditions =
                        challenge.Conditions |> List.map (fun c ->
                            if c.ConditionId = conditionId then
                                { c with OwnerKind = "Agent"; OwnerAgentId = newOwner; UpdatedAtUtc = timestamp }
                            else c)
                    let updated = { challenge with Version = challenge.Version + 1; Conditions = conditions; UpdatedAtUtc = timestamp }
                    let event =
                        { ChallengeId = challengeId; ConditionId = conditionId
                          ActorId = actor; Action = "AssignChallengeCondition"
                          BeforeStatus = challenge.Status; AfterStatus = challenge.Status
                          BeforeOwnerAgentId = condition.OwnerAgentId; AfterOwnerAgentId = newOwner
                          Evidence = ""; Reason = ""; Sequence = snapshot.Header.NextSequence
                          OccurredAtUtc = timestamp }
                    return [ change (path id) (Some blob) updated
                             change (eventPath id snapshot.Header.NextSequence) None event ]
                })



    // The first split part retains the original condition ID; additional IDs
    // are derived from the stable operation ID. The event preserves prior
    // meaning/evidence so a split never rewrites history invisibly.
    let splitCondition binding (operationId: Guid) actor challengeId conditionId
                       expectedVersion (parts: ChallengeConditionSpec list) =
        match guidN challengeId with
        | None -> Error(InvalidBinding "Split requires a canonical challenge UUID.")
        | Some id when operationId = Guid.Empty || not (agentId actor)
                       || expectedVersion < 1 || parts.Length < 2 || parts.Length > 32
                       || (parts |> List.exists (fun d ->
                           (owner d.OwnerKind d.OwnerAgentId).IsNone
                           || (d.OwnerKind = "Operator" && not (String.IsNullOrWhiteSpace d.SuggestedAgentId))
                           || not (text 2048 d.Description)
                           || (not (String.IsNullOrWhiteSpace d.OwnerAgentId) && not (agentId d.OwnerAgentId))
                           || (not (String.IsNullOrWhiteSpace d.SuggestedAgentId) && not (agentId d.SuggestedAgentId)))) ->
            Error(InvalidBinding "Split requires 2-32 valid condition definitions and exact version.")
        | Some id ->
            let childId index =
                let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
                    id.ToString("N") + ":split:" + operationId.ToString("N") + ":" + string index))
                Guid(bytes.AsSpan(0, 16)).ToString("N")
            let ids = conditionId :: [ for index in 1 .. parts.Length - 1 -> childId index ]
            let result =
                changed binding operationId actor "SplitChallengeCondition"
                    {| challengeId = challengeId; conditionId = conditionId
                       expectedVersion = expectedVersion; parts = parts |}
                    (fun snapshot -> result {
                        do! registered binding snapshot actor
                        for part in parts do
                            if owner part.OwnerKind part.OwnerAgentId <> Some ChallengeOwner.Operator
                               && not (String.IsNullOrWhiteSpace part.OwnerAgentId) then
                                do! registered binding snapshot part.OwnerAgentId
                            if not (String.IsNullOrWhiteSpace part.SuggestedAgentId) then
                                do! registered binding snapshot part.SuggestedAgentId
                        let! challenge, blob = readChallenge binding snapshot id
                        if challenge.Version <> expectedVersion
                           || List.contains challenge.Status [ "Satisfied"; "Cancelled" ] then
                            return! Error(StaleEntityVersion "Challenge version or terminal state changed before split.")
                        if challenge.Conditions.Length + parts.Length - 1 > 32 then
                            return! Error(InvalidBinding "A challenge cannot exceed 32 conditions after splitting.")
                        let! subscribed = subscribedAt binding snapshot actor
                        let! original =
                            match challenge.Conditions |> List.tryFind (fun c -> c.ConditionId = conditionId) with
                            | None -> Error(InvalidBinding "Unknown challenge condition for split.")
                            | Some c when ownerOf c = Some ChallengeOwner.Operator ->
                                Error(ReviewAccessDenied "Operator input conditions cannot be split.")
                            | Some c when c.Status <> "Pending" ->
                                Error(StaleEntityVersion "Only Pending conditions can be split.")
                            | Some c when actor = challenge.IssuerAgentId
                                      || (subscribed && ownerOf c = Some ChallengeOwner.Unassigned) ->
                                Ok c
                            | Some _ ->
                                Error(ReviewAccessDenied "Only issuer or subscribed dispatcher of unassigned work can split.")
                        let collisions = ids.Tail |> List.exists (fun nextId ->
                            challenge.Conditions |> List.exists (fun c -> c.ConditionId = nextId))
                        if collisions then
                            return! Error(LedgerIntegrityError "Deterministic split condition ID collision.")
                        let timestamp = DateTimeOffset.UtcNow.ToString("O")
                        let normalized (value: string) =
                            if String.IsNullOrWhiteSpace value then "" else value
                        let replacement =
                            (parts, ids)
                            ||> List.map2 (fun part targetId ->
                                { ConditionId = targetId
                                  OwnerKind = ownerKind part.OwnerKind part.OwnerAgentId
                                  OwnerAgentId = normalized part.OwnerAgentId
                                  SuggestedAgentId = normalized part.SuggestedAgentId
                                  Description = part.Description
                                  Status = "Pending"; Evidence = null; Reason = null
                                  UpdatedAtUtc = timestamp })
                        let revised =
                            { challenge with Version = challenge.Version + 1
                                             Conditions = challenge.Conditions |> List.collect (fun c ->
                                                 if c.ConditionId = original.ConditionId then replacement else [c])
                                             UpdatedAtUtc = timestamp }
                        let audit =
                            { ChallengeId = challenge.ChallengeId; ConditionId = original.ConditionId
                              ActorId = actor; Action = "SplitChallengeCondition"
                              BeforeStatus = challenge.Status; AfterStatus = revised.Status
                              BeforeOwnerAgentId = original.OwnerAgentId
                              AfterOwnerAgentId = replacement.Head.OwnerAgentId
                              Evidence = json {| previousDescription = original.Description
                                                 previousEvidence = original.Evidence
                                                 previousReason = original.Reason
                                                 parts = replacement |> List.map (fun c ->
                                                    {| id = c.ConditionId; owner = c.OwnerAgentId
                                                       suggestion = c.SuggestedAgentId
                                                       description = c.Description |}) |}
                              Reason = ""
                              Sequence = snapshot.Header.NextSequence
                              OccurredAtUtc = timestamp }
                        return [ change (path id) (Some blob) revised
                                 change (eventPath id snapshot.Header.NextSequence) None audit ]
                    })
            result |> Result.map (fun receipt ->
                { ChallengeId = id.ToString("N"); ConditionIds = ids; Receipt = receipt })
    let private accessible actor (challenge: AgentChallenge) =
        challenge.IssuerAgentId = actor
        || challenge.Conditions |> List.exists (fun c -> ownerOf c = Some(ChallengeOwner.Agent actor))

    let private visible actor subscribed (challenge: AgentChallenge) =
        accessible actor challenge
        || (subscribed && challenge.Conditions |> List.exists (fun c ->
            c.Status = "Pending" && ownerOf c = Some ChallengeOwner.Unassigned))

    let private listAt binding snapshot = result {
        let! paths = LedgerStore.listAt binding snapshot "challenges"
        let paths =
            paths |> List.filter (fun p -> p.EndsWith("/challenge.json", StringComparison.Ordinal))
        if paths.Length > 4096 then
            return! Error(InvalidBinding "Too many challenges to scan; use a narrower project.")
        let mutable records = []
        for item in paths do
            let components = item.Split('/')
            if components.Length <> 3 then
                return! Error(LedgerIntegrityError "Invalid challenge ledger path.")
            match guidN components[1] with
            | None -> return! Error(LedgerIntegrityError "Invalid challenge identity in ledger path.")
            | Some id ->
                let! record, _ = readChallenge binding snapshot id
                records <- record :: records
        return records
    }

    /// Local authenticated operator UI only. This has no MCP route and does
    /// not treat the human as a registered agent or a subscriber.
    let listOperator binding projectName = result {
        let! snapshot = LedgerStore.fetch binding
        let! challenges = listAt binding snapshot
        return
            [ for challenge in challenges do
                for condition in challenge.Conditions do
                    if ownerOf condition = Some ChallengeOwner.Operator then
                        yield { ProjectName = projectName
                                ChallengeId = challenge.ChallengeId
                                ConditionId = condition.ConditionId
                                IssuerAgentId = challenge.IssuerAgentId
                                Title = challenge.Title
                                TaskId = challenge.TaskId
                                Question = condition.Description
                                ChallengeStatus = challenge.Status
                                ConditionStatus = condition.Status
                                Version = challenge.Version
                                Evidence = condition.Evidence
                                CreatedAtUtc = challenge.CreatedAtUtc
                                UpdatedAtUtc = condition.UpdatedAtUtc } ]
    }

    /// Enrich the ordinary self-inbox using the SAME verified snapshot.
    /// This is a bounded read projection, never a notification or activation.
    let enrichInboxAt binding snapshot actor (inbox: AgentInbox) = result {
        if inbox.SnapshotCommit <> snapshot.Commit || inbox.Agent.AgentId <> actor then
            return! Error(LedgerIntegrityError "Challenge inbox must use the same self-agent snapshot.")
        let! enabled = subscribedAt binding snapshot actor
        let! challenges = listAt binding snapshot
        let item (challenge: AgentChallenge) (condition: ChallengeCondition) : InboxChallengeItem =
            { ChallengeId = challenge.ChallengeId
              ConditionId = condition.ConditionId
              IssuerAgentId = challenge.IssuerAgentId
              OwnerAgentId = condition.OwnerAgentId
              SuggestedAgentId = condition.SuggestedAgentId
              Title = compact 120 challenge.Title
              DescriptionPreview = compact 144 condition.Description
              ChallengeStatus = challenge.Status
              ConditionStatus = condition.Status
              Version = challenge.Version
              EvidencePreview = compact 120 condition.Evidence
              ReasonPreview = compact 120 condition.Reason
              UpdatedAtUtc = condition.UpdatedAtUtc }
        let allItems =
            [ for challenge in challenges do
                for condition in challenge.Conditions do
                    yield challenge, condition, item challenge condition ]
        let newest items =
            items |> List.sortByDescending (fun (entry: InboxChallengeItem) ->
                entry.UpdatedAtUtc, entry.ChallengeId, entry.ConditionId)
        let assigned =
            allItems
            |> List.choose (fun (_, c, item) ->
                if ownerOf c = Some(ChallengeOwner.Agent actor) && c.Status = "Pending" then Some item else None)
            |> newest
        let triage =
            if enabled then
                allItems
                |> List.choose (fun (_, c, item) ->
                    if c.Status = "Pending" && ownerOf c = Some ChallengeOwner.Unassigned
                    then Some item else None)
                |> newest
            else []
        let recent =
            allItems
            |> List.choose (fun (challenge, c, item) ->
                if challenge.IssuerAgentId = actor && c.Status <> "Pending"
                then Some item else None)
            |> newest
        let linked =
            inbox.BlockedChallengeWaits |> List.collect _.ChallengeIds |> Set.ofList
        let dependencies =
            allItems
            |> List.choose (fun (challenge, _, item) ->
                if Set.contains challenge.ChallengeId linked
                   && challenge.Conditions.Head.ConditionId = item.ConditionId
                then Some item else None)
            |> newest
        let cap items = items |> List.truncate 12
        return { inbox with
                    ChallengeSubscribed = enabled
                    ChallengeAssigned = cap assigned
                    ChallengeTriage = cap triage
                    ChallengeRecent = cap recent
                    ChallengeDependencies = cap dependencies
                    ChallengeAssignedOmitted = max 0 (assigned.Length - 12)
                    ChallengeTriageOmitted = max 0 (triage.Length - 12)
                    ChallengeRecentOmitted = max 0 (recent.Length - 12)
                    ChallengeDependenciesOmitted = max 0 (dependencies.Length - 12) }
    }

    /// Bounded cursor paging prevents a project with many large evidence
    /// records from overflowing the MCP response. Titles and unresolved IDs
    /// suffice to discover work; full evidence stays in get_challenge.
    let listPage binding actor limit cursor expectedSnapshot =
        if not (agentId actor) || limit < 1 || limit > 50
           || (not (String.IsNullOrWhiteSpace cursor) && (guidN cursor).IsNone)
           || (not (String.IsNullOrWhiteSpace expectedSnapshot)
               && (expectedSnapshot.Length <> 40
                   || not (expectedSnapshot |> Seq.forall Uri.IsHexDigit))) then
            Error(InvalidBinding "Challenge list needs a registered actor, page size 1-50, and an optional canonical cursor/snapshot.")
        else result {
            let! snapshot = LedgerStore.fetch binding
            if not (String.IsNullOrWhiteSpace expectedSnapshot)
               && snapshot.Commit <> expectedSnapshot then
                return! Error(StaleEntityVersion "Challenge list snapshot moved; start pagination again.")
            do! registered binding snapshot actor
            let! subscribed = subscribedAt binding snapshot actor
            let! all = listAt binding snapshot
            let sorted =
                all
                |> List.filter (visible actor subscribed)
                |> List.filter (fun challenge ->
                    String.IsNullOrWhiteSpace cursor
                    || String.CompareOrdinal(challenge.ChallengeId, cursor) > 0)
                |> List.sortBy _.ChallengeId
                |> List.truncate (limit + 1)
            let page = sorted |> List.truncate limit
            let summaries =
                page |> List.map (fun c ->
                    { ChallengeId = c.ChallengeId
                      IssuerAgentId = c.IssuerAgentId
                      Title = compact 160 c.Title
                      TaskId = c.TaskId
                      Status = c.Status; Version = c.Version
                      TotalConditions = c.Conditions.Length
                      PendingConditions = c.Conditions |> List.filter (fun x -> x.Status = "Pending") |> List.length
                      BlockedConditions = c.Conditions |> List.filter (fun x -> x.Status = "Blocked") |> List.length
                      SatisfiedConditions = c.Conditions |> List.filter (fun x -> x.Status = "Satisfied") |> List.length
                      OwnedUnresolvedConditionIds =
                        c.Conditions
                        |> List.filter (fun x -> ownerOf x = Some(ChallengeOwner.Agent actor) && x.Status <> "Satisfied")
                        |> List.map _.ConditionId })
            return
                { SnapshotCommit = snapshot.Commit
                  Challenges = summaries
                  NextCursor = if sorted.Length > limit then (page |> List.last).ChallengeId else null }
        }

    let list binding actor = listPage binding actor 50 "" ""

    // Audit events are immutable but can grow without limit across repeated
    // block/reopen cycles. Read a bounded page, tied to a verified snapshot,
    // instead of making a single unlimited MCP response.
    let private getPageFor operatorView binding actor challengeId limit afterSequence expectedSnapshot =
        match guidN challengeId with
        | None -> Error(InvalidBinding "Invalid challenge ID.")
        | Some id when (not operatorView && not (agentId actor)) || limit < 1 || limit > 16
                       || afterSequence < 0L
                       || (not (String.IsNullOrWhiteSpace expectedSnapshot)
                           && (expectedSnapshot.Length <> 40
                               || not (expectedSnapshot |> Seq.forall Uri.IsHexDigit))) ->
            Error(InvalidBinding "Challenge detail needs actor, event page size 1-16, and valid sequence/snapshot.")
        | Some id -> result {
            let! snapshot = LedgerStore.fetch binding
            if not (String.IsNullOrWhiteSpace expectedSnapshot)
               && snapshot.Commit <> expectedSnapshot then
                return! Error(StaleEntityVersion "Challenge detail snapshot moved; start paging again.")
            if not operatorView then
                do! registered binding snapshot actor
            let! record, _ = readChallenge binding snapshot id
            let! subscribed =
                if operatorView then Ok false else subscribedAt binding snapshot actor
            let canView =
                if operatorView then
                    record.Conditions |> List.exists (fun c -> ownerOf c = Some ChallengeOwner.Operator)
                else visible actor subscribed record
            if not canView then
                return! Error(ReviewAccessDenied "Only issuer and condition owners can inspect a challenge.")
            let! paths =
                LedgerStore.listAt binding snapshot
                    ("challenges/" + id.ToString("N") + "/events")
            let mutable available = []
            for item in paths do
                let basename = item.Substring(item.LastIndexOf('/') + 1)
                let raw = basename.Substring(0, basename.Length - 5)
                match Int64.TryParse raw with
                | true, sequence when sequence > 0L ->
                    if sequence > afterSequence then
                        available <- (sequence, item) :: available
                | _ -> return! Error(LedgerIntegrityError "Invalid immutable challenge event filename.")
            let page =
                available |> List.sortBy fst |> List.truncate (limit + 1)
            let selected = page |> List.truncate limit
            let mutable events = []
            for sequence, item in selected do
                let! contents, _ = LedgerStore.readAt binding snapshot item
                match contents with
                | None -> return! Error(LedgerIntegrityError "Missing immutable challenge event.")
                | Some value ->
                    try
                        use doc = JsonDocument.Parse(value)
                        if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 2 then
                            return! Error(LedgerIntegrityError "Invalid challenge event schema.")
                        let entry = JsonSerializer.Deserialize<ChallengeEvent>(value, options)
                        if isNull (box entry) || entry.ChallengeId <> record.ChallengeId
                           || entry.Sequence <> sequence then
                            return! Error(LedgerIntegrityError "Invalid immutable challenge event identity.")
                        events <- entry :: events
                    with ex ->
                        return! Error(LedgerIntegrityError ("Invalid challenge event: " + ex.Message))
            return
                { SnapshotCommit = snapshot.Commit; Challenge = record
                  History = List.rev events
                  NextEventSequence =
                    if page.Length > limit then selected |> List.last |> fst else 0L }
        }

    let getPage binding actor challengeId limit afterSequence expectedSnapshot =
        getPageFor false binding actor challengeId limit afterSequence expectedSnapshot

    // The Program/TUI access closure is the authentication boundary; no
    // remote TaskRead or MCP path calls this operator-only reader.
    let getOperatorPage binding challengeId limit afterSequence expectedSnapshot =
        getPageFor true binding "AuthenticatedOperator" challengeId limit afterSequence expectedSnapshot

    let get binding actor challengeId = getPage binding actor challengeId 16 0L ""

    let private waitPath task attempt =
        $"tasks/{task}/attempts/{attempt}/challenge-wait.json"

    let private canonical<'t> binding snapshot file = result {
        let! content, oid = LedgerStore.readAt binding snapshot file
        match content, oid with
        | Some body, Some blob ->
            try
                use doc = JsonDocument.Parse(body)
                let expectedSchema =
                    if typeof<'t> = typeof<ReviewTask> || typeof<'t> = typeof<AttemptChallengeWait> then 2
                    else 3
                if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> expectedSchema then
                    return! Error(LedgerIntegrityError ("Unexpected ledger schema for " + file))
                let decoded = JsonSerializer.Deserialize<'t>(body, options)
                if isNull (box decoded) then
                    return! Error(LedgerIntegrityError ("Null canonical ledger entity: " + file))
                return decoded, blob
            with ex ->
                return! Error(LedgerIntegrityError ("Invalid canonical ledger entity: " + ex.Message))
        | None, None -> return! Error(StaleEntityVersion ("Missing canonical ledger entity: " + file))
        | _ -> return! Error(LedgerIntegrityError ("Inconsistent canonical ledger entity: " + file))
    }

    let private lifecycle (task: ReviewTask) (attempt: ReviewAttempt) (operationId: Guid)
                          actor action reason afterPhase afterGeneration sequence : TaskLifecycleEvent =
        { TaskId = task.TaskId; AttemptId = attempt.AttemptId
          OperationId = operationId.ToString("D")
          Sequence = sequence; Action = action; ActorId = actor; Reason = reason
          PreviousLifecycle = task.Lifecycle; NewLifecycle = task.Lifecycle
          PreviousPhase = attempt.Phase; NewPhase = afterPhase
          PreviousOwnerId = attempt.OwnerAgentId; NewOwnerId = attempt.OwnerAgentId
          PreviousGeneration = attempt.AssignmentGeneration
          NewGeneration = afterGeneration
          ReviewRequestId = null; ReviewDisposition = "Unchanged"; Evidence = null }

    let private issue (task: ReviewTask) (attempt: ReviewAttempt) (actor: string)
                      (reason: string) sequence : AssignmentIssue =
        { TaskId = task.TaskId; AttemptId = attempt.AttemptId
          Generation = attempt.AssignmentGeneration
          OwnerAgentId = attempt.OwnerAgentId; IssuerId = actor
          IssuedSequence = sequence
          Instructions = reason; Reason = "ChallengeUnblock"
          SpecBlobOid = attempt.SpecBlobOid; SourceBaseCommit = attempt.BaseCommit
          TargetRef = attempt.TargetRef
          SourceRepositoryId = attempt.SourceRepositoryId
          SourceRemote = attempt.SourceRemote
          SourcePushRemote = attempt.SourcePushRemote
          SourceRef = attempt.SourceRef
          PreviousGeneration = attempt.AssignmentGeneration - 1 }

    let private validatedAttempt binding snapshot actor task attemptId
                                 expectedTaskVersion expectedAttemptVersion
                                 expectedAgentVersion generation =
        result {
            let! record, _ = canonical<ReviewTask> binding snapshot (taskPath task)
            if record.TaskId <> task || record.Lifecycle <> "InProgress"
               || record.AttemptId <> attemptId || record.Version <> expectedTaskVersion then
                return! Error(StaleEntityVersion "Task context changed before challenge dependency transition.")
            let! attempt, attemptOid =
                canonical<ReviewAttempt> binding snapshot
                    ($"tasks/{task}/attempts/{attemptId}.json")
            if attempt.TaskId <> task || attempt.AttemptId <> attemptId
               || attempt.OwnerAgentId <> actor
               || attempt.Version <> expectedAttemptVersion
               || attempt.AssignmentGeneration <> generation then
                return! Error(StaleEntityVersion "Attempt owner, generation or version changed.")
            let! agent, agentOid = canonical<ReviewAgent> binding snapshot (agentPath actor)
            if agent.AgentId <> actor || agent.Version <> expectedAgentVersion then
                return! Error(StaleEntityVersion "Agent identity or version changed.")
            return record, attempt, attemptOid, agent, agentOid
        }

    /// An owner may block their assigned attempt on zero (explicit manual),
    /// one, or several existing challenges. All state changes form ONE ledger
    /// CAS transaction; no scheduler, hidden agent activation or separate
    /// notification is introduced.
    let blockAttempt binding operationId actor task attemptId
                     expectedTaskVersion expectedAttemptVersion expectedAgentVersion
                     generation reason (challengeIds: string list) =
        let requested =
            if isNull (box challengeIds) then [] else challengeIds
        // Normalize UUID spelling BEFORE checking uniqueness or storing
        // dependencies; case differences cannot represent distinct challenges.
        let normalized =
            requested
            |> List.choose (fun raw ->
                guidN raw |> Option.map (fun id -> id.ToString("N")))
        if operationId = Guid.Empty || not (agentId actor)
           || String.IsNullOrWhiteSpace task || not (taskId task)
           || (guidN attemptId).IsNone || generation < 1
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || not (text 2048 reason)
           || requested.Length <> normalized.Length
           || normalized.Length > 16
           || normalized.Length <> (normalized |> List.distinct |> List.length) then
            Error(InvalidBinding "Block requires valid actor/task/attempt, exact versions, reason, and <=16 distinct challenge IDs.")
        else
            changed binding operationId actor "WaitOnChallenges"
                {| taskId = task; attemptId = attemptId; reason = reason; challengeIds = normalized
                   expectedTaskVersion = expectedTaskVersion
                   expectedAttemptVersion = expectedAttemptVersion
                   expectedAgentVersion = expectedAgentVersion; generation = generation |}
                (fun snapshot -> result {
                    let! currentTask, attempt, attemptOid, agent, agentOid =
                        validatedAttempt binding snapshot actor task attemptId
                            expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation
                    if not (List.contains attempt.Phase [ "Queued"; "Started" ])
                       || isNull (box agent.CurrentAssignment)
                       || agent.CurrentAssignment.TaskId <> task
                       || agent.CurrentAssignment.AttemptId <> attemptId
                       || (agent.Execution = "Working" && attempt.Phase <> "Started") then
                        return! Error(AgentBusy "Cannot block an attempt without its current matching owner assignment.")
                    let priorWaitPath = waitPath task attemptId
                    let! priorContent, priorOid = LedgerStore.readAt binding snapshot priorWaitPath
                    let! priorWait =
                        match priorContent, priorOid with
                        | None, None -> Ok None
                        | Some _, Some _ ->
                            canonical<AttemptChallengeWait> binding snapshot priorWaitPath
                            |> Result.map (fun (record, _) ->
                                Some record)
                        | _ -> Error(LedgerIntegrityError "Invalid challenge dependency record.")
                    if priorWait |> Option.exists (fun prior -> prior.Status <> "Requeued") then
                        return! Error(StaleEntityVersion "A challenge dependency wait is already open.")
                    // An already-satisfied dependency cannot produce a future
                    // wakeup event. Never strand a task on an empty effective
                    // dependency set; callers may use the explicit manual wait
                    // (no dependencies) instead.
                    let mutable hasOutstandingDependency = List.isEmpty normalized
                    for dependency in normalized do
                        let! linked, _ = readChallenge binding snapshot (Guid.ParseExact(dependency, "N"))
                        if not (accessible actor linked) then
                            return! Error(ReviewAccessDenied
                                "Attempt owner must be the challenge issuer or a condition owner to link it.")
                        if linked.Status = "Cancelled" ||
                           (not (String.IsNullOrEmpty linked.TaskId) && linked.TaskId <> task) then
                            return! Error(StaleEntityVersion "Linked challenge was cancelled or belongs to another task.")
                        if linked.Status <> "Satisfied" then
                            hasOutstandingDependency <- true
                    if not hasOutstandingDependency then
                        return! Error(StaleEntityVersion
                            "All linked challenges are already Satisfied; there is nothing to wait for.")
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let wait : AttemptChallengeWait =
                        { TaskId = task; AttemptId = attemptId; OwnerAgentId = actor
                          Reason = reason; ChallengeIds = normalized
                          Status = "Waiting"
                          Version = priorWait |> Option.map (fun p -> p.Version + 1) |> Option.defaultValue 1
                          BlockedAtUtc = timestamp; RequeuedAtUtc = null }
                    let locator : AttemptChallengeWaitIndex =
                        { TaskId = task; AttemptId = attemptId; OwnerAgentId = actor
                          AssignmentGeneration = generation }
                    let indexWrites =
                        if List.isEmpty normalized then []
                        else
                            let ownerIndex =
                                ReviewWorkflow.waitIndexPath
                                    (ReviewWorkflow.ownerWaitIndexPrefix actor) task attemptId generation
                            [ change ownerIndex None locator ] @
                            [ for dependency in normalized ->
                                  change
                                      (ReviewWorkflow.waitIndexPath
                                          (ReviewWorkflow.challengeWaitIndexPrefix dependency)
                                          task attemptId generation) None locator ]
                    let blockedAttempt =
                        { attempt with Version = attempt.Version + 1; Phase = "Blocked" }
                    let stoppedAgent =
                        { agent with
                            CurrentAssignment = Unchecked.defaultof<AgentAssignment>; Execution = "Stopped"
                            UnassignedDisposition = "NeedsWork"
                            Version = agent.Version + 1
                            StateReason = reason
                            StateSequence = snapshot.Header.NextSequence
                            StateEnteredAtUtc = timestamp; WorkingTurnStartedAtUtc = null
                            ActiveSessionId = null; AssociatedSessionIds = [] }
                    let audit =
                        lifecycle currentTask attempt operationId actor
                            "WaitOnChallenges" reason "Blocked"
                            generation snapshot.Header.NextSequence
                    // Blocking this assignment frees the owner just like an
                    // explicit completion. Reconcile an older satisfied wait
                    // without waiting for unrelated future owner activity.
                    let! nextAgent, wakeups =
                        ReviewWorkflow.reconcileEligibleOnRelease binding snapshot operationId actor stoppedAgent
                    return
                        [ change priorWaitPath priorOid wait
                          change ($"tasks/{task}/attempts/{attemptId}.json") (Some attemptOid) blockedAttempt
                          change (agentPath actor) (Some agentOid) nextAgent
                          change ($"tasks/{task}/lifecycle/{snapshot.Header.NextSequence}.json") None audit ] @ indexWrites @ wakeups
                })

    /// Explicit recovery supports manual waits and legacy Waiting records;
    /// the same pinned snapshot verifies EVERY dependency before transitioning
    /// the attempt to Queued and assigning a new Ready (never Working) slot.
    let requeueAttempt binding operationId actor task attemptId
                       expectedTaskVersion expectedAttemptVersion expectedAgentVersion
                       generation reason =
        if operationId = Guid.Empty || not (agentId actor)
           || String.IsNullOrWhiteSpace task || not (taskId task)
           || (guidN attemptId).IsNone || generation < 1
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || not (text 2048 reason) then
            Error(InvalidBinding "Requeue requires valid actor/task/attempt, exact versions and reason.")
        else
            changed binding operationId actor "RequeueChallengeBlockedAttempt"
                {| taskId = task; attemptId = attemptId; reason = reason
                   expectedTaskVersion = expectedTaskVersion
                   expectedAttemptVersion = expectedAttemptVersion
                   expectedAgentVersion = expectedAgentVersion; generation = generation |}
                (fun snapshot -> result {
                    let! currentTask, attempt, attemptOid, agent, agentOid =
                        validatedAttempt binding snapshot actor task attemptId
                            expectedTaskVersion expectedAttemptVersion expectedAgentVersion generation
                    if attempt.Phase <> "Blocked" then
                        return! Error(StaleEntityVersion "Only Blocked challenge-linked attempts can be requeued.")
                    if not (isNull (box agent.CurrentAssignment)) || agent.Execution <> "Stopped"
                       || agent.UnassignedDisposition <> "NeedsWork" then
                        return! Error(AgentBusy "Cannot requeue while owner is assigned, Working, or explicitly Parked.")
                    let waitingPath = waitPath task attemptId
                    let! wait, waitOid = canonical<AttemptChallengeWait> binding snapshot waitingPath
                    if wait.TaskId <> task || wait.AttemptId <> attemptId ||
                       wait.OwnerAgentId <> actor || wait.Status <> "Waiting" then
                        return! Error(StaleEntityVersion "Attempt's challenge dependency wait is missing or already requeued.")
                    for dependency in wait.ChallengeIds do
                        match guidN dependency with
                        | None -> return! Error(LedgerIntegrityError "Invalid stored challenge dependency ID.")
                        | Some id ->
                            let! linked, _ = readChallenge binding snapshot id
                            if linked.Status <> "Satisfied" then
                                return! Error(StaleEntityVersion
                                    ($"Challenge {dependency} is {linked.Status}; all must be Satisfied before requeue."))
                    let sequence = snapshot.Header.NextSequence
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let nextGen = generation + 1
                    let readyAttempt =
                        { attempt with
                            Phase = "Queued"; Version = attempt.Version + 1
                            AssignmentGeneration = nextGen; IssuedSequence = sequence }
                    let readyAgent =
                        { agent with
                            Version = agent.Version + 1
                            CurrentAssignment = { TaskId = task; AttemptId = attemptId }
                            UnassignedDisposition = null
                            Execution = "Stopped"
                            StateEnteredAtUtc = timestamp
                            StateReason = reason; StateSequence = sequence
                            WorkingTurnStartedAtUtc = null
                            ActiveSessionId = null; AssociatedSessionIds = [] }
                    let requeued =
                        { wait with
                            Status = "Requeued"
                            Version = wait.Version + 1
                            RequeuedAtUtc = timestamp }
                    let audit =
                        lifecycle currentTask attempt operationId actor
                            "RequeueChallengeBlockedAttempt" reason "Queued" nextGen sequence
                    return
                        [ change ($"tasks/{task}/attempts/{attemptId}.json") (Some attemptOid) readyAttempt
                          change (agentPath actor) (Some agentOid) readyAgent
                          change waitingPath (Some waitOid) requeued
                          change ($"tasks/{task}/attempts/{attemptId}/assignments/{nextGen}.json") None
                              (issue currentTask readyAttempt actor reason sequence)
                          change ($"tasks/{task}/lifecycle/{sequence}.json") None audit ]
                })
