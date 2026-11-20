namespace Client

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Client.LedgerStore
open Common
open Common.SignalR

module AgentActivityLedger =
    [<CLIMutable>]
    type AgentActivityRecord =
        { SchemaVersion: int
          ProjectId: string
          LedgerId: string
          Epoch: int
          AccountScope: string
          AgentId: string
          LastActivityAtUtc: string
          LastActivityEventId: string
          LastActivitySequence: int64
          LastActivityDeviceId: string
          LastActivityConnectionGeneration: int64
          LastActivitySessionBindingHash: string
          LastActivityCommandName: string
          LastActivityCorrelationId: string
          LastRecordedAtUtc: string
          LastRecordedEventId: string
          LastRecordedSequence: int64
          LastRecordedDeviceId: string
          LastRecordedConnectionGeneration: int64
          LastRecordedSessionBindingHash: string
          LastRecordedCommandName: string
          LastRecordedCorrelationId: string
          SourceKind: string }

    type AgentActivitySessionContext =
        { AccountScope: string
          DeviceId: string
          ConnectionId: string
          ConnectionGeneration: int64 }

    type AgentActivityProof =
        { Activity: AgentActivityRecord
          Agent: LedgerStore.AgentStateProjection
          SnapshotCommit: string
          ProjectId: string
          LedgerId: string
          Epoch: int
          ServerNowUtc: DateTimeOffset
          Age: TimeSpan }

    type AgentActivityGateFailure =
        | ActivityProofUnavailable of string
        | DuplicateActivityEvent

    [<CLIMutable>]
    type private ActivityIntentRequest =
        { SchemaVersion: int
          EventId: string
          ProjectId: string
          LedgerId: string
          Epoch: int
          AccountScope: string
          AgentId: string
          ObservedAtUtc: string
          DeviceId: string
          ConnectionGeneration: int64
          SessionBindingHash: string
          CommandName: string
          CorrelationId: string
          SourceKind: string }

    [<CLIMutable>]
    type private ActivityIntentInput =
        { SchemaVersion: int
          ProjectId: string
          OperationId: string
          Actor: string
          Kind: string
          Request: string }

    let private jsonOptions =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true)

    let private sha256 (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes value)
        |> Convert.ToHexString
        |> fun digest -> digest.ToLowerInvariant()

    let private agentKey (agentId: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes agentId)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private agentPath agentId = $"agents/{agentKey agentId}.json"

    let private activityPath accountScope agentId =
        let key = sha256 (accountScope + "|" + agentId)
        "records/agent-activity/" + key + ".json"

    let private sessionBindingHash (dispatch: AgentActivityDispatch) =
        sha256
            (String.Join(
                "|",
                [| dispatch.DeviceId
                   dispatch.ConnectionId
                   dispatch.ConnectionGeneration.ToString(CultureInfo.InvariantCulture) |]))

    let private utcText (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

    let private tryUtc (value: string) =
        let mutable parsed = DateTimeOffset.MinValue
        if not (String.IsNullOrWhiteSpace value)
           && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed)
           && parsed.Offset = TimeSpan.Zero then
            Some parsed
        else
            None

    let private tryGuid (value: string) =
        let mutable parsed = Guid.Empty
        Guid.TryParseExact(value, "D", &parsed) && parsed <> Guid.Empty

    let private isHexDigest (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length = 64
        && (value |> Seq.forall (fun character ->
            (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f')))

    let private isAccountScope (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.StartsWith("operator:", StringComparison.Ordinal)
        && isHexDigest (value.Substring("operator:".Length))

    let private unavailable reason = "ActivityProofUnavailable: " + reason

    let private failureText = function
        | InvalidBinding _ -> "ledger binding is invalid"
        | RemoteUnavailable _ -> "canonical project ledger is unavailable"
        | LedgerIntegrityError _ -> "canonical project ledger integrity verification failed"
        | LedgerHistoryUnavailable _ -> "canonical project ledger history is unavailable"
        | StaleEntityVersion _ -> "canonical agent/activity state changed during the transaction"
        | OperationIdReused -> "the activity event identifier was reused with different content"
        | OutcomeUnknown _ -> "canonical activity publication outcome is unknown"
        | LedgerContended -> "canonical project ledger remained contended"
        | TrustBindingContended _ -> "the trusted project ledger binding changed concurrently"
        | GitFailure _ -> "the canonical Git ledger operation failed"
        | SourceUnavailable _ -> "the canonical project source is unavailable"
        | AgentBusy _ -> "the canonical agent state is unavailable"
        | ReviewAccessDenied _ -> "the canonical activity record is not accessible"
        | ReviewAlreadyClaimed _ -> "the canonical activity record is already claimed"
        | ReviewVersionConflict _ -> "the canonical activity record has a version conflict"
        | SourceReviewRefUnavailable _ -> "the canonical activity provenance is unavailable"

    let requiresProof (agentId: string) (command: AgentCommand) =
        AgentCommandInfo.isAgentActivity command
        && not (String.IsNullOrWhiteSpace agentId)
        && match AgentCommandInfo.projectName command with
           | Some projectName ->
               not (String.IsNullOrWhiteSpace projectName)
               && not (String.Equals(projectName, "@global", StringComparison.OrdinalIgnoreCase))
           | None -> false

    let tryDecodeEnvelope (commandJson: string) =
        try
            use document = JsonDocument.Parse commandJson
            let root = document.RootElement
            let hasProperty (name: string) =
                let mutable value = Unchecked.defaultof<JsonElement>
                root.ValueKind = JsonValueKind.Object && root.TryGetProperty(name, &value)
            if hasProperty "CommandJson" || hasProperty "commandJson" then
                try
                    let envelope = JsonSerializer.Deserialize<AgentActivityCommandEnvelope>(commandJson, jsonOptions)
                    if isNull (box envelope)
                       || isNull (box envelope.Activity)
                       || envelope.SchemaVersion <> AgentActivityProtocol.schemaVersion
                       || String.IsNullOrWhiteSpace envelope.CommandJson then
                        Error(unavailable "the server activity dispatch envelope is malformed")
                    else
                        Ok(Some envelope)
                with _ ->
                    Error(unavailable "the server activity dispatch envelope could not be decoded")
            else
                Ok None
        with
        | :? JsonException -> Ok None
        | _ -> Error(unavailable "the server activity dispatch envelope is invalid")

    let validateDispatch
        (expected: AgentActivitySessionContext)
        (assertedAgentId: string)
        (command: AgentCommand)
        (dispatch: AgentActivityDispatch) =
        let commandProject = AgentCommandInfo.projectName command
        let expectedCommandName = AgentCommandInfo.protocolName command
        let agentBytes =
            if String.IsNullOrWhiteSpace assertedAgentId then 0
            else Encoding.UTF8.GetByteCount assertedAgentId
        if not (requiresProof assertedAgentId command) then
            Error(unavailable "the command is not an attributable project-scoped agent activity")
        elif dispatch.SchemaVersion <> AgentActivityProtocol.schemaVersion then
            Error(unavailable "the activity dispatch schema version is unsupported")
        elif commandProject <> Some dispatch.ProjectName then
            Error(unavailable "the activity project does not match the dispatched command")
        elif dispatch.AgentId <> assertedAgentId
             || agentBytes > 120
             || String.IsNullOrWhiteSpace dispatch.AgentId then
            Error(unavailable "the activity actor does not match the exact asserted agent")
        elif dispatch.AccountScope <> expected.AccountScope
             || not (isAccountScope dispatch.AccountScope) then
            Error(unavailable "the activity account does not match the authenticated operator")
        elif dispatch.DeviceId <> expected.DeviceId
             || String.IsNullOrWhiteSpace expected.DeviceId then
            Error(unavailable "the activity device does not match the selected local device")
        elif dispatch.ConnectionId <> expected.ConnectionId
             || String.IsNullOrWhiteSpace expected.ConnectionId then
            Error(unavailable "the activity connection does not match the live SignalR session")
        elif dispatch.ConnectionGeneration <> expected.ConnectionGeneration
             || expected.ConnectionGeneration <= 0L then
            Error(unavailable "the activity connection generation is stale or unknown")
        elif not (tryGuid dispatch.EventId) then
            Error(unavailable "the server activity event identifier is missing or invalid")
        elif String.IsNullOrWhiteSpace dispatch.CorrelationId
             || dispatch.CorrelationId.Length > 128 then
            Error(unavailable "the server command correlation identifier is missing or invalid")
        elif String.IsNullOrWhiteSpace dispatch.CommandName
             || dispatch.CommandName <> expectedCommandName then
            Error(unavailable "the activity command name does not match the dispatched command")
        elif dispatch.ObservedAtUtc = DateTimeOffset.MinValue
             || dispatch.ObservedAtUtc.Offset <> TimeSpan.Zero then
            Error(unavailable "the server-observed activity time is missing or is not UTC")
        else
            Ok ()

    let private snapshotMatchesBinding (binding: LedgerStore.LedgerBinding) (snapshot: LedgerStore.LedgerSnapshot) =
        not (String.IsNullOrWhiteSpace snapshot.Commit)
        && snapshot.Header.ProjectId = binding.ProjectId.ToString("D")
        && snapshot.Header.LedgerId = binding.LedgerId.ToString("D")
        && snapshot.Header.Epoch = binding.Epoch
        && snapshot.Header.NextSequence > 0L

    let private readAgentStateAt
        (binding: LedgerStore.LedgerBinding)
        (snapshot: LedgerStore.LedgerSnapshot)
        (agentId: string) =
        match LedgerStore.LedgerStore.readAt binding snapshot (agentPath agentId) with
        | Error failure -> Error(unavailable (failureText failure))
        | Ok (None, None) ->
            Error(unavailable "the exact agent record is missing from the verified project snapshot")
        | Ok (Some content, Some _) ->
            try
                let projection = JsonSerializer.Deserialize<LedgerStore.AgentStateProjection>(content, jsonOptions)
                if isNull (box projection)
                   || projection.AgentId <> agentId
                   || projection.StateSequence < 0L
                   || String.IsNullOrWhiteSpace projection.Execution
                   || (tryUtc projection.StateEnteredAtUtc).IsNone then
                    Error(unavailable "the current agent state in the verified project snapshot is invalid")
                else
                    Ok projection
            with _ ->
                Error(unavailable "the current agent state in the verified project snapshot is malformed")
        | Ok _ ->
            Error(unavailable "the current agent state has an incomplete Git blob proof")

    let private tryCommittedBlobOid (operation: LedgerStore.LedgerOperation) path =
        try
            use document = JsonDocument.Parse operation.OriginalResultJson
            let changes = document.RootElement.GetProperty("changes")
            changes.EnumerateArray()
            |> Seq.tryPick (fun change ->
                let changedPath = change.GetProperty("path").GetString()
                if changedPath = path then
                    let oid = change.GetProperty("committedBlobOid").GetString()
                    if String.IsNullOrWhiteSpace oid then None else Some oid
                else None)
        with _ ->
            None

    let private verifyOperationAt
        (binding: LedgerStore.LedgerBinding)
        (snapshot: LedgerStore.LedgerSnapshot)
        path
        accountScope
        agentId
        eventId
        observedAtUtc
        sequence
        deviceId
        connectionGeneration
        sessionHash
        commandName
        correlationId
        expectedCurrentBlobOid =
        match LedgerStore.LedgerStore.getOperationAt binding snapshot (Guid.ParseExact(eventId, "D")) with
        | Error failure -> Error(unavailable (failureText failure))
        | Ok None -> Error(unavailable "the activity event operation receipt is missing")
        | Ok(Some operation) ->
            let operationMatches =
                operation.OperationId = eventId
                && operation.Type = AgentActivityProtocol.recordKind
                && operation.Actor = agentId
                && operation.Sequence = sequence
                && operation.Path = path
                && not (isNull operation.Paths)
                && (operation.Paths |> Array.contains path)
            if not operationMatches then
                Error(unavailable "the activity event operation receipt has mismatched identity or sequence")
            else
                try
                    let input =
                        JsonSerializer.Deserialize<ActivityIntentInput>(operation.CanonicalRequestJson, jsonOptions)
                    if isNull (box input)
                       || input.SchemaVersion <> 2
                       || input.ProjectId <> binding.ProjectId.ToString("D")
                       || input.OperationId <> eventId
                       || input.Actor <> agentId
                       || input.Kind <> AgentActivityProtocol.recordKind
                       || String.IsNullOrWhiteSpace input.Request then
                        Error(unavailable "the activity event canonical request identity is invalid")
                    else
                        let request = JsonSerializer.Deserialize<ActivityIntentRequest>(input.Request, jsonOptions)
                        let requestMatches =
                            not (isNull (box request))
                            && request.SchemaVersion = AgentActivityProtocol.schemaVersion
                            && request.EventId = eventId
                            && request.ProjectId = binding.ProjectId.ToString("D")
                            && request.LedgerId = binding.LedgerId.ToString("D")
                            && request.Epoch = binding.Epoch
                            && request.AccountScope = accountScope
                            && request.AgentId = agentId
                            && request.ObservedAtUtc = observedAtUtc
                            && request.DeviceId = deviceId
                            && request.ConnectionGeneration = connectionGeneration
                            && request.SessionBindingHash = sessionHash
                            && request.CommandName = commandName
                            && request.CorrelationId = correlationId
                            && request.SourceKind = AgentActivityProtocol.sourceKind
                        if not requestMatches then
                            Error(unavailable "the activity event canonical request does not match its watermark")
                        else
                            match LedgerStore.LedgerStore.getOperationOriginAt binding snapshot operation with
                            | Error failure -> Error(unavailable (failureText failure))
                            | Ok origin when String.IsNullOrWhiteSpace origin ->
                                Error(unavailable "the activity event has no verified originating Git commit")
                            | Ok _ ->
                                match expectedCurrentBlobOid with
                                | None -> Ok ()
                                | Some expectedOid ->
                                    match tryCommittedBlobOid operation path with
                                    | Some committedOid when committedOid = expectedOid -> Ok ()
                                    | _ ->
                                        Error(unavailable "the current activity record was not written by its latest operation receipt")
                with _ ->
                    Error(unavailable "the activity event operation provenance is malformed")

    let private readActivityRecordAt
        (binding: LedgerStore.LedgerBinding)
        (snapshot: LedgerStore.LedgerSnapshot)
        (accountScope: string)
        (agentId: string) =
        let path = activityPath accountScope agentId
        match LedgerStore.LedgerStore.readAt binding snapshot path with
        | Error failure -> Error(unavailable (failureText failure))
        | Ok (None, None) -> Ok None
        | Ok (Some content, Some blobOid) ->
            try
                let record = JsonSerializer.Deserialize<AgentActivityRecord>(content, jsonOptions)
                let activityAt = if isNull (box record) then None else tryUtc record.LastActivityAtUtc
                let recordedAt = if isNull (box record) then None else tryUtc record.LastRecordedAtUtc
                let validRecord =
                    not (isNull (box record))
                    && record.SchemaVersion = AgentActivityProtocol.schemaVersion
                    && record.ProjectId = binding.ProjectId.ToString("D")
                    && record.LedgerId = binding.LedgerId.ToString("D")
                    && record.Epoch = binding.Epoch
                    && record.AccountScope = accountScope
                    && record.AgentId = agentId
                    && record.SourceKind = AgentActivityProtocol.sourceKind
                    && activityAt.IsSome
                    && recordedAt.IsSome
                    && tryGuid record.LastActivityEventId
                    && tryGuid record.LastRecordedEventId
                    && record.LastActivitySequence > 0L
                    && record.LastRecordedSequence >= record.LastActivitySequence
                    && record.LastRecordedSequence < snapshot.Header.NextSequence
                    && record.LastActivitySequence < snapshot.Header.NextSequence
                    && not (String.IsNullOrWhiteSpace record.LastActivityDeviceId)
                    && not (String.IsNullOrWhiteSpace record.LastRecordedDeviceId)
                    && record.LastActivityConnectionGeneration > 0L
                    && record.LastRecordedConnectionGeneration > 0L
                    && isHexDigest record.LastActivitySessionBindingHash
                    && isHexDigest record.LastRecordedSessionBindingHash
                    && not (String.IsNullOrWhiteSpace record.LastActivityCommandName)
                    && not (String.IsNullOrWhiteSpace record.LastRecordedCommandName)
                    && not (String.IsNullOrWhiteSpace record.LastActivityCorrelationId)
                    && not (String.IsNullOrWhiteSpace record.LastRecordedCorrelationId)
                    && (if record.LastActivityEventId = record.LastRecordedEventId then
                            record.LastActivitySequence = record.LastRecordedSequence
                            && record.LastActivityAtUtc = record.LastRecordedAtUtc
                            && record.LastActivityDeviceId = record.LastRecordedDeviceId
                            && record.LastActivityConnectionGeneration = record.LastRecordedConnectionGeneration
                            && record.LastActivitySessionBindingHash = record.LastRecordedSessionBindingHash
                            && record.LastActivityCommandName = record.LastRecordedCommandName
                            && record.LastActivityCorrelationId = record.LastRecordedCorrelationId
                        else
                            record.LastActivitySequence < record.LastRecordedSequence
                            && activityAt.Value > recordedAt.Value)
                if not validRecord then
                    Error(unavailable "the durable activity watermark has invalid identity, time, or sequence fields")
                else
                    let verifyActivity =
                        verifyOperationAt binding snapshot path accountScope agentId
                            record.LastActivityEventId record.LastActivityAtUtc
                            record.LastActivitySequence record.LastActivityDeviceId
                            record.LastActivityConnectionGeneration record.LastActivitySessionBindingHash
                            record.LastActivityCommandName record.LastActivityCorrelationId None
                    match verifyActivity with
                    | Error error -> Error error
                    | Ok () ->
                        match verifyOperationAt binding snapshot path accountScope agentId
                                record.LastRecordedEventId record.LastRecordedAtUtc
                                record.LastRecordedSequence record.LastRecordedDeviceId
                                record.LastRecordedConnectionGeneration record.LastRecordedSessionBindingHash
                                record.LastRecordedCommandName record.LastRecordedCorrelationId (Some blobOid) with
                        | Error error -> Error error
                        | Ok () -> Ok(Some(record, blobOid))
            with _ ->
                Error(unavailable "the durable activity watermark is malformed")
        | Ok _ ->
            Error(unavailable "the durable activity watermark has an incomplete Git blob proof")

    let readAt
        (binding: LedgerStore.LedgerBinding)
        (snapshot: LedgerStore.LedgerSnapshot)
        (accountScope: string)
        (agentId: string)
        (serverNowUtc: DateTimeOffset) =
        if not (snapshotMatchesBinding binding snapshot) then
            Error(unavailable "the supplied ledger snapshot does not match its verified project binding")
        elif not (isAccountScope accountScope) then
            Error(unavailable "the authenticated account scope is missing or invalid")
        elif String.IsNullOrWhiteSpace agentId || Encoding.UTF8.GetByteCount agentId > 120 then
            Error(unavailable "the exact agent identifier is missing or invalid")
        elif serverNowUtc = DateTimeOffset.MinValue || serverNowUtc.Offset <> TimeSpan.Zero then
            Error(unavailable "the server time supplied for proof evaluation is missing or is not UTC")
        else
            match readAgentStateAt binding snapshot agentId with
            | Error error -> Error error
            | Ok agent ->
                match readActivityRecordAt binding snapshot accountScope agentId with
                | Error error -> Error error
                | Ok None ->
                    Error(unavailable "no durable activity watermark exists for this agent/account in the verified project ledger")
                | Ok(Some(activity, _)) ->
                    match tryUtc activity.LastActivityAtUtc, tryUtc activity.LastRecordedAtUtc with
                    | Some lastActivity, Some lastRecorded
                        when serverNowUtc >= lastActivity && serverNowUtc >= lastRecorded ->
                        Ok
                            { Activity = activity
                              Agent = agent
                              SnapshotCommit = snapshot.Commit
                              ProjectId = binding.ProjectId.ToString("D")
                              LedgerId = binding.LedgerId.ToString("D")
                              Epoch = binding.Epoch
                              ServerNowUtc = serverNowUtc
                              Age = serverNowUtc - lastActivity }
                    | _ ->
                        Error(unavailable "server time precedes the recorded watermark; clock or provenance anomaly detected")

    let record
        (binding: LedgerStore.LedgerBinding)
        (verifiedSnapshot: LedgerStore.LedgerSnapshot)
        (expected: AgentActivitySessionContext)
        (assertedAgentId: string)
        (dispatch: AgentActivityDispatch)
        (command: AgentCommand) =
        match validateDispatch expected assertedAgentId command dispatch with
        | Error error -> Error error
        | Ok () when not (snapshotMatchesBinding binding verifiedSnapshot) ->
            Error(unavailable "the supplied verified ledger snapshot does not match its project binding")
        | Ok () ->
            let path = activityPath dispatch.AccountScope dispatch.AgentId
            let sessionHash = sessionBindingHash dispatch
            let observedAt = utcText dispatch.ObservedAtUtc
            let request =
                { SchemaVersion = AgentActivityProtocol.schemaVersion
                  EventId = dispatch.EventId
                  ProjectId = binding.ProjectId.ToString("D")
                  LedgerId = binding.LedgerId.ToString("D")
                  Epoch = binding.Epoch
                  AccountScope = dispatch.AccountScope
                  AgentId = dispatch.AgentId
                  ObservedAtUtc = observedAt
                  DeviceId = dispatch.DeviceId
                  ConnectionGeneration = dispatch.ConnectionGeneration
                  SessionBindingHash = sessionHash
                  CommandName = dispatch.CommandName
                  CorrelationId = dispatch.CorrelationId
                  SourceKind = AgentActivityProtocol.sourceKind }
            let intent =
                { OperationId = Guid.ParseExact(dispatch.EventId, "D")
                  Actor = dispatch.AgentId
                  Kind = AgentActivityProtocol.recordKind
                  RequestJson = JsonSerializer.Serialize(request, jsonOptions) }
            let plan (snapshot: LedgerStore.LedgerSnapshot) =
                if not (snapshotMatchesBinding binding snapshot) then
                    Error(LedgerIntegrityError "The activity transaction snapshot does not match its project binding.")
                else
                    match readAgentStateAt binding snapshot dispatch.AgentId with
                    | Error error ->
                        Error(LedgerIntegrityError error)
                    | Ok _ ->
                        match readActivityRecordAt binding snapshot dispatch.AccountScope dispatch.AgentId with
                        | Error error ->
                            Error(LedgerIntegrityError error)
                        | Ok existing ->
                            let sequence = snapshot.Header.NextSequence
                            let existingActivity = existing |> Option.map fst
                            let incomingWins =
                                match existingActivity with
                                | None -> true
                                | Some previous ->
                                    match tryUtc previous.LastActivityAtUtc with
                                    | Some previousAt -> dispatch.ObservedAtUtc >= previousAt
                                    | None -> false
                            let previous = existingActivity
                            let (activityAt, activityEventId, activitySequence,
                                 activityDeviceId, activityGeneration, activitySessionHash,
                                 activityCommandName, activityCorrelationId) =
                                match previous, incomingWins with
                                | Some value, false ->
                                    value.LastActivityAtUtc, value.LastActivityEventId, value.LastActivitySequence,
                                    value.LastActivityDeviceId, value.LastActivityConnectionGeneration,
                                    value.LastActivitySessionBindingHash, value.LastActivityCommandName,
                                    value.LastActivityCorrelationId
                                | _ ->
                                    observedAt, dispatch.EventId, sequence, dispatch.DeviceId,
                                    dispatch.ConnectionGeneration, sessionHash, dispatch.CommandName,
                                    dispatch.CorrelationId
                            let updated =
                                { SchemaVersion = AgentActivityProtocol.schemaVersion
                                  ProjectId = binding.ProjectId.ToString("D")
                                  LedgerId = binding.LedgerId.ToString("D")
                                  Epoch = binding.Epoch
                                  AccountScope = dispatch.AccountScope
                                  AgentId = dispatch.AgentId
                                  LastActivityAtUtc = activityAt
                                  LastActivityEventId = activityEventId
                                  LastActivitySequence = activitySequence
                                  LastActivityDeviceId = activityDeviceId
                                  LastActivityConnectionGeneration = activityGeneration
                                  LastActivitySessionBindingHash = activitySessionHash
                                  LastActivityCommandName = activityCommandName
                                  LastActivityCorrelationId = activityCorrelationId
                                  LastRecordedAtUtc = observedAt
                                  LastRecordedEventId = dispatch.EventId
                                  LastRecordedSequence = sequence
                                  LastRecordedDeviceId = dispatch.DeviceId
                                  LastRecordedConnectionGeneration = dispatch.ConnectionGeneration
                                  LastRecordedSessionBindingHash = sessionHash
                                  LastRecordedCommandName = dispatch.CommandName
                                  LastRecordedCorrelationId = dispatch.CorrelationId
                                  SourceKind = AgentActivityProtocol.sourceKind }
                            let expectedBlobOid = existing |> Option.map snd
                            Ok
                                [ { Path = path
                                    ExpectedBlobOid = expectedBlobOid
                                    Content = JsonSerializer.Serialize(updated, jsonOptions) } ]
            match LedgerStore.LedgerStore.transact binding intent plan with
            | Ok write -> Ok write
            | Error failure -> Error(unavailable (failureText failure))

    /// The command effect is reachable only after the canonical Git write is
    /// proven. A replayed event is not re-executed because its prior tool outcome
    /// may be unknown after a disconnect or process crash.
    let executeAfterCommit
        (recordActivity: unit -> Result<LedgerStore.LedgerWrite, string>)
        (execute: unit -> Task<'result>) =
        task {
            match recordActivity() with
            | Error reason ->
                return Error(ActivityProofUnavailable reason)
            | Ok write when write.AlreadyApplied ->
                return Error DuplicateActivityEvent
            | Ok write ->
                let! result = execute()
                return Ok(write, result)
        }
