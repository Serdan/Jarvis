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
    { OwnerAgentId: string
      Description: string }

[<CLIMutable>]
type ChallengeCondition =
    { ConditionId: string
      OwnerAgentId: string
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
      OnSatisfied: string
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
      Evidence: string
      Reason: string
      Sequence: int64
      OccurredAtUtc: string }

[<CLIMutable>]
type ChallengeDetails =
    { SnapshotCommit: string
      Challenge: AgentChallenge
      History: ChallengeEvent list }

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
    let private notificationPath (operationId: Guid) =
        "notifications/" + operationId.ToString("N") + ".json"
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
        && challenge.OnSatisfied = "Notify"
        && List.contains challenge.Status [ "Pending"; "Blocked"; "Satisfied"; "Cancelled" ]
        && not (List.isEmpty ids)
        && ids.Length <= 32
        && ids.Length = (ids |> List.distinct |> List.length)
        && (challenge.Conditions |> List.forall (fun c ->
            (guidN c.ConditionId).IsSome && agentId c.OwnerAgentId
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

    /// Notifications have a strict 4 KiB ledger limit. Preserve every
    /// condition's result in a compact summary and retain full evidence in
    /// the challenge record and immutable audit events.
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

    let private completionNotice (challenge: AgentChallenge) =
        let header =
            $"Challenge '{compact 120 challenge.Title}' is satisfied ({challenge.ChallengeId})."
        let results =
            challenge.Conditions
            |> List.mapi (fun index condition ->
                $"{index + 1}. {compact 24 condition.OwnerAgentId}: {compact 24 condition.Description}; evidence: {compact 36 condition.Evidence}")
            |> String.concat "\n"
        header + "\n" + results + "\nFull evidence: get_challenge."

    let private blockedNotice (challenge: AgentChallenge) actor evidence reason =
        $"Challenge '{compact 120 challenge.Title}' blocked ({challenge.ChallengeId}); {compact 40 actor}: {compact 512 reason}. Evidence: {compact 512 evidence}. Full history: get_challenge."

    let private emitNotification (id: Guid) issuer taskId message timestamp : LedgerFileChange =
        let notification : AgentNotification =
            { NotificationId = id.ToString("N")
              FromAgentId = issuer
              FromProjectName = ""
              ToAgentId = issuer
              TaskId = taskId
              Message = message
              CreatedAtUtc = timestamp
              AcknowledgedAtUtc = null }
        change (notificationPath id) None notification

    let private changed binding operationId actor kind payload
                        (plan: LedgerSnapshot -> Result<LedgerFileChange list, LedgerFailure>) =
        LedgerStore.transact binding (intent operationId actor kind payload) plan

    let create binding operationId issuer title task
               (definitions: ChallengeConditionSpec list) =
        if operationId = Guid.Empty || not (agentId issuer)
           || not (text 2048 title) || not (taskId task)
           || List.isEmpty definitions || List.length definitions > 32
           || (definitions |> List.exists (fun d ->
               not (agentId d.OwnerAgentId) || not (text 2048 d.Description))) then
            Error(InvalidBinding "Challenge requires valid issuer, title, optional task, and 1-32 owned conditions.")
        else
            let challengeId = operationId
            let conditions =
                definitions |> List.mapi (fun index definition ->
                    { ConditionId = conditionId challengeId index
                      OwnerAgentId = definition.OwnerAgentId
                      Description = definition.Description
                      Status = "Pending"; Evidence = null; Reason = null
                      UpdatedAtUtc = null })
            let created =
                changed binding operationId issuer "CreateChallenge"
                    ({| title = title; taskId = task; conditions = definitions |})
                    (fun snapshot -> result {
                    do! registered binding snapshot issuer
                    for definition in definitions |> List.distinctBy _.OwnerAgentId do
                        do! registered binding snapshot definition.OwnerAgentId
                    do! existingTask binding snapshot task
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let entity =
                        { ChallengeId = challengeId.ToString("N")
                          IssuerAgentId = issuer; Title = title
                          TaskId = if isNull task then "" else task
                          Version = 1; CompletionPolicy = "All"; OnSatisfied = "Notify"
                          Status = "Pending"; Conditions = conditions
                          CreatedAtUtc = timestamp; UpdatedAtUtc = timestamp }
                    let event =
                        { ChallengeId = entity.ChallengeId
                          ConditionId = ""; ActorId = issuer; Action = "Created"
                          BeforeStatus = ""; AfterStatus = "Pending"
                          Evidence = ""; Reason = ""; Sequence = snapshot.Header.NextSequence
                          OccurredAtUtc = timestamp }
                    return [ change (path challengeId) None entity
                             change (eventPath challengeId snapshot.Header.NextSequence) None event ]
                })
            created |> Result.map (fun receipt ->
                { ChallengeId = challengeId.ToString("N")
                  ConditionIds = conditions |> List.map _.ConditionId
                  Receipt = receipt })

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
                        do! registered binding snapshot actor
                        let priorState = challenge.Status
                        let timestamp = DateTimeOffset.UtcNow.ToString("O")
                        let target =
                            challenge.Conditions |> List.tryFind (fun c -> c.ConditionId = conditionId)
                        let! nextConditions, eventCondition, message =
                            match kind with
                            | "CancelChallenge" when actor = challenge.IssuerAgentId
                                && text 4096 reason ->
                                Ok(challenge.Conditions, "", None)
                            | "ReopenChallengeCondition" when actor = challenge.IssuerAgentId
                                                           && text 4096 reason ->
                                match target with
                                | Some condition when condition.Status = "Blocked" ->
                                    Ok(challenge.Conditions |> List.map (fun c ->
                                        if c.ConditionId = conditionId then
                                            { c with Status = "Pending"; UpdatedAtUtc = timestamp }
                                        else c), conditionId, None)
                                | _ -> Error(StaleEntityVersion "Only an existing Blocked condition can be reopened.")
                            | "ResolveChallengeCondition" ->
                                match target with
                                | Some condition when condition.OwnerAgentId = actor
                                                      && condition.Status = "Pending"
                                                      && (text 8192 evidence || text 4096 reason) ->
                                    let blocked = text 4096 reason
                                    let next = challenge.Conditions |> List.map (fun c ->
                                        if c.ConditionId = conditionId then
                                            { c with Status = if blocked then "Blocked" else "Satisfied"
                                                     Evidence = evidence; Reason = reason
                                                     UpdatedAtUtc = timestamp }
                                        else c)
                                    Ok(next, conditionId, None)
                                | Some _ when target.Value.OwnerAgentId <> actor ->
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
                              AfterStatus = nextStatus; Evidence = evidence
                              Reason = reason; Sequence = snapshot.Header.NextSequence
                              OccurredAtUtc = timestamp }
                        let extra =
                            if kind = "ResolveChallengeCondition" && nextStatus = "Satisfied" then
                                [ emitNotification operationId challenge.IssuerAgentId challenge.TaskId
                                    (completionNotice nextRecord) timestamp ]
                            elif kind = "ResolveChallengeCondition" && nextStatus = "Blocked" then
                                [ emitNotification operationId challenge.IssuerAgentId challenge.TaskId
                                    (blockedNotice nextRecord actor evidence reason) timestamp ]
                            else []
                        return [ change (path id) (Some blob) nextRecord
                                 change (eventPath id snapshot.Header.NextSequence) None event ]
                               @ extra
                    })

    let resolve binding operationId actor challengeId conditionId version evidence blockedReason =
        update binding operationId actor challengeId conditionId version
            "ResolveChallengeCondition" evidence blockedReason
    let reopen binding operationId issuer challengeId conditionId version reason =
        update binding operationId issuer challengeId conditionId version
            "ReopenChallengeCondition" "" reason
    let cancel binding operationId issuer challengeId version reason =
        update binding operationId issuer challengeId "" version "CancelChallenge" "" reason

    let private accessible actor (challenge: AgentChallenge) =
        challenge.IssuerAgentId = actor
        || challenge.Conditions |> List.exists (fun c -> c.OwnerAgentId = actor)

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
            let! all = listAt binding snapshot
            let sorted =
                all
                |> List.filter (accessible actor)
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
                        |> List.filter (fun x -> x.OwnerAgentId = actor && x.Status <> "Satisfied")
                        |> List.map _.ConditionId })
            return
                { SnapshotCommit = snapshot.Commit
                  Challenges = summaries
                  NextCursor = if sorted.Length > limit then (page |> List.last).ChallengeId else null }
        }

    let list binding actor = listPage binding actor 50 "" ""

    let get binding actor challengeId =
        match guidN challengeId with
        | None -> Error(InvalidBinding "Invalid challenge ID.")
        | Some id -> result {
            if not (agentId actor) then return! Error(InvalidBinding "Actor identity required.")
            let! snapshot = LedgerStore.fetch binding
            do! registered binding snapshot actor
            let! record, _ = readChallenge binding snapshot id
            if not (accessible actor record) then
                return! Error(ReviewAccessDenied "Only issuer and condition owners can inspect a challenge.")
            let! paths =
                LedgerStore.listAt binding snapshot
                    ("challenges/" + id.ToString("N") + "/events")
            let mutable events = []
            for item in paths do
                let! contents, _ = LedgerStore.readAt binding snapshot item
                match contents with
                | None -> return! Error(LedgerIntegrityError "Missing immutable challenge event.")
                | Some value ->
                    try
                        use doc = JsonDocument.Parse(value)
                        if doc.RootElement.GetProperty("schemaVersion").GetInt32() <> 2 then
                            return! Error(LedgerIntegrityError "Invalid challenge event schema.")
                        let entry = JsonSerializer.Deserialize<ChallengeEvent>(value, options)
                        if isNull (box entry) || entry.ChallengeId <> record.ChallengeId then
                            return! Error(LedgerIntegrityError "Invalid challenge event record.")
                        events <- entry :: events
                    with ex ->
                        return! Error(LedgerIntegrityError ("Invalid challenge event: " + ex.Message))
            return
                { SnapshotCommit = snapshot.Commit; Challenge = record
                  History = events |> List.sortBy _.Sequence }
        }


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
        let normalized =
            if isNull (box challengeIds) then [] else challengeIds
        if operationId = Guid.Empty || not (agentId actor)
           || String.IsNullOrWhiteSpace task || not (taskId task)
           || (guidN attemptId).IsNone || generation < 1
           || expectedTaskVersion < 1 || expectedAttemptVersion < 1
           || expectedAgentVersion < 1 || not (text 2048 reason)
           || normalized.Length > 16
           || normalized.Length <> (normalized |> List.distinct |> List.length)
           || (normalized |> List.exists (guidN >> Option.isNone)) then
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
                    for dependency in normalized do
                        let! linked, _ = readChallenge binding snapshot (Guid.ParseExact(dependency, "N"))
                        if linked.Status = "Cancelled" ||
                           (not (String.IsNullOrEmpty linked.TaskId) && linked.TaskId <> task) then
                            return! Error(StaleEntityVersion "Linked challenge was cancelled or belongs to another task.")
                    let timestamp = DateTimeOffset.UtcNow.ToString("O")
                    let wait : AttemptChallengeWait =
                        { TaskId = task; AttemptId = attemptId; OwnerAgentId = actor
                          Reason = reason; ChallengeIds = normalized
                          Status = "Waiting"
                          Version = priorWait |> Option.map (fun p -> p.Version + 1) |> Option.defaultValue 1
                          BlockedAtUtc = timestamp; RequeuedAtUtc = null }
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
                    return
                        [ change priorWaitPath priorOid wait
                          change ($"tasks/{task}/attempts/{attemptId}.json") (Some attemptOid) blockedAttempt
                          change (agentPath actor) (Some agentOid) stoppedAgent
                          change ($"tasks/{task}/lifecycle/{snapshot.Header.NextSequence}.json") None audit ]
                })

    /// Requeue is explicit even after all challenges are satisfied.
    /// The same pinned snapshot verifies EVERY dependency before transitioning
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
