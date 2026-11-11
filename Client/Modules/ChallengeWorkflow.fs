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
type ChallengeList =
    { SnapshotCommit: string
      Challenges: AgentChallenge list }

[<CLIMutable>]
type ChallengeCreated =
    { ChallengeId: string
      ConditionIds: string list
      Receipt: LedgerWrite }

module ChallengeWorkflow =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)
    let private json value = JsonSerializer.Serialize(value, options)
    let private stored value =
        let body = json value
        if not (body.StartsWith("{", StringComparison.Ordinal)) then invalidArg "value" "Expected record."
        "{\"schemaVersion\":2," + body.Substring(1)

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
                                let resultText =
                                    nextConditions |> List.map (fun c ->
                                        $"{c.OwnerAgentId}: {c.Description} (evidence: {c.Evidence})")
                                    |> String.concat "; "
                                [ emitNotification operationId challenge.IssuerAgentId challenge.TaskId
                                    ($"Challenge '{challenge.Title}' is satisfied ({challengeId}). {resultText}")
                                    timestamp ]
                            elif kind = "ResolveChallengeCondition" && nextStatus = "Blocked" then
                                [ emitNotification operationId challenge.IssuerAgentId challenge.TaskId
                                    ($"Challenge '{challenge.Title}' blocked ({challengeId}); {actor}: {reason}. Evidence: {evidence}")
                                    timestamp ]
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

    let list binding actor = result {
        if not (agentId actor) then return! Error(InvalidBinding "Actor identity required.")
        let! snapshot = LedgerStore.fetch binding
        do! registered binding snapshot actor
        let! all = listAt binding snapshot
        return
            { SnapshotCommit = snapshot.Commit
              Challenges =
                all |> List.filter (accessible actor)
                    |> List.sortBy (fun c -> c.CreatedAtUtc, c.ChallengeId) }
    }

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
