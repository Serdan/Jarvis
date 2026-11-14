namespace Server.Services

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Text.Json
open Common
open Microsoft.Data.Sqlite

[<CLIMutable>]
type FeedbackEntry =
    { Id: string
      CreatedAt: DateTimeOffset
      ProjectName: string
      ToolName: string
      Category: string
      Severity: string
      Summary: string
      Details: string
      Workaround: string
      OperationId: string
      Status: string
      ResolvedAt: Nullable<DateTimeOffset>
      ResolutionCommit: string
      ResolutionNotes: string }

[<CLIMutable>]
type FeedbackCreated =
    { Id: string
      CreatedAt: DateTimeOffset
      OperationId: string }

[<CLIMutable>]
type FeedbackCount =
    { Name: string
      Count: int }

[<CLIMutable>]
type FeedbackSummary =
    { Total: int
      WithWorkaround: int
      LinkedToFailedOperation: int
      ByTool: FeedbackCount array
      ByCategory: FeedbackCount array
      BySeverity: FeedbackCount array }

type FeedbackInput =
    { ProjectName: string option
      ToolName: string option
      Category: string
      Severity: string
      Summary: string
      Details: string option
      Workaround: string option
      OperationId: string option }

type OperationStart =
    { Id: string
      StartedAt: DateTimeOffset }

// A typed projection of the schema-checked execution epoch. An empty or
// freshly migrated witness table never constitutes proof of legacy quiescence.
type AgentExecutionReadiness =
    | LegacyDrainUnverified
    | LegacyDrainVerified of operatorId: string * evidence: string * approvedAt: DateTimeOffset

/// An explicit proof boundary for releasing a correction permit. A server
/// timeout or an unreconciled dispatch must never be interpreted as terminal.
type AgentExecutionResolution =
    | NeverDispatched
    | AuthenticatedClientFinished
    | ExecutionUnresolved

/// Read-only operator recovery evidence for a single authenticated user's
/// target agent. This is NOT permission to release the durable exclusion.
type AgentCorrectionPermitEvidence =
    { TargetAgentId: string
      PermitId: string
      OperationId: string
      CreatedAt: DateTimeOffset }

/// Read-only evidence for operator reconciliation. Missing fields remain
/// optional, and a truncated page must never be mistaken for full quiescence.
type UnresolvedAgentExecution =
    { RequestId: string
      ToolName: string option
      ProjectName: string option
      State: string
      DeviceId: string option
      ConnectionId: string option
      DeviceGeneration: int64 option
      CorrelationId: string option
      ParentRequestId: string option
      JobId: string option
      JobStartedAt: DateTimeOffset option
      JobReceiptAt: DateTimeOffset option
      DeferredToolJobId: string option
      DeferredToolJobOperationId: string option
      DeferredToolJobStartedAt: DateTimeOffset option
      DeferredToolJobReceiptAt: DateTimeOffset option
      CreatedAt: DateTimeOffset }

type UnresolvedAgentExecutionPage =
    { Executions: UnresolvedAgentExecution list
      HasMore: bool }

[<CLIMutable>]
type ConnectionEventEntry =
    { CreatedAt: DateTimeOffset
      EventType: string
      ConnectionId: string
      DeviceId: string
      DeviceName: string
      Generation: Nullable<int64>
      ClientVersion: string
      ProtocolVersion: string
      DurationMs: Nullable<int64>
      ErrorType: string
      ErrorMessage: string }

module FeedbackValidation =
    let categories =
        set
            [ "ToolFailure"
              "ToolLimitation"
              "Ergonomics"
              "MissingCapability"
              "Documentation"
              "Positive"
              "Other" ]

    let severities =
        set [ "Info"; "Friction"; "Blocking" ]

    let private canonical (allowed: Set<string>) fieldName value =
        let allowedValues = String.Join(", ", allowed)

        match
            allowed
            |> Seq.tryFind (fun candidate -> String.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
        with
        | Some canonicalValue -> Ok canonicalValue
        | None -> Error $"{fieldName} must be one of: {allowedValues}"

    let category value = canonical categories "category" value
    let severity value = canonical severities "severity" value
    let statuses = set [ "Open"; "InProgress"; "Resolved"; "WontFix" ]
    let status value = canonical statuses "status" value
    let isClosed value = value = "Resolved" || value = "WontFix"

type FeedbackStore(databasePath: string, ?telemetryRetention: TimeSpan) =
    let telemetryRetention = defaultArg telemetryRetention (TimeSpan.FromDays 30.0)
    do if telemetryRetention <= TimeSpan.Zero then invalidArg "telemetryRetention" "Must be positive"
    let connectionString =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- databasePath
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Cache <- SqliteCacheMode.Shared
        builder.ToString()

    // Pending handles can be recorded for any known command, including a
    // process runner; recording is NEVER equivalent to terminal proof.
    let knownCommandTypes =
        AgentProtocol.commandDefinitions
        |> Seq.map (fun definition -> definition.Name)
        |> Set.ofSeq
        |> Set.add "NotifyAgent"

    // A transport response cannot certify descendant-process quiescence.
    // Commands capable of launching work that outlives the immediate client
    // task require separate OS-backed terminal evidence. This is a conservative
    // known-command whitelist: future variants never inherit terminal status.
    // NotifyAgent is a synthetic second-stage witness, not an MCP tool name.
    let terminalCommandTypes =
        AgentProtocol.commandDefinitions
        |> Seq.filter (fun definition ->
            not (definition.Permissions |> List.contains ProcessExecution)
            // Git commit hooks may leave detached children; scheduling creates
            // future work after the initiating command has returned.
            && not (List.contains definition.Name
                [ "GitCommit"; "ScheduleBrowserExecution" ]))
        |> Seq.map (fun definition -> definition.Name)
        |> Set.ofSeq
        |> Set.add "NotifyAgent"
        |> Set.remove "StartJob"

    let dbValue = function
        | Some value -> box value
        | None -> box DBNull.Value

    let optionText (reader: SqliteDataReader) ordinal =
        if reader.IsDBNull ordinal then None else Some(reader.GetString ordinal)

    let textOrEmpty = Option.defaultValue ""

    let openConnection () =
        let connection = new SqliteConnection(connectionString)
        connection.Open()

        use foreignKeys = connection.CreateCommand()
        foreignKeys.CommandText <- "PRAGMA foreign_keys = ON;"
        foreignKeys.ExecuteNonQuery() |> ignore

        connection

    let addParameter (command: SqliteCommand) name value =
        command.Parameters.AddWithValue(name, value) |> ignore

    let initialize () =
        let directory = Path.GetDirectoryName(databasePath)

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

        use connection = openConnection()

        use journal = connection.CreateCommand()
        journal.CommandText <- "PRAGMA journal_mode = WAL;"
        journal.ExecuteScalar() |> ignore

        use schema = connection.CreateCommand()
        schema.CommandText <-
            """
CREATE TABLE IF NOT EXISTS operations (
    id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    completed_at TEXT NULL,
    user_id TEXT NOT NULL,
    project_name TEXT NULL,
    tool_name TEXT NOT NULL,
    success INTEGER NULL,
    error_category TEXT NULL,
    duration_ms INTEGER NULL,
    server_version TEXT NOT NULL,
    client_version TEXT NULL,
    protocol_version TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_operations_user_created
    ON operations(user_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_operations_user_tool_created
    ON operations(user_id, tool_name, created_at DESC);

CREATE TABLE IF NOT EXISTS feedback (
    id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    user_id TEXT NOT NULL,
    project_name TEXT NULL,
    tool_name TEXT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL,
    summary TEXT NOT NULL,
    details TEXT NULL,
    workaround TEXT NULL,
    operation_id TEXT NULL,
    FOREIGN KEY(operation_id) REFERENCES operations(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS ix_feedback_user_created
    ON feedback(user_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_feedback_operation
    ON feedback(operation_id);

CREATE TABLE IF NOT EXISTS connection_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL,
    user_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    connection_id TEXT NOT NULL,
    device_id TEXT NULL,
    device_name TEXT NULL,
    generation INTEGER NULL,
    client_version TEXT NULL,
    protocol_version TEXT NULL,
    duration_ms INTEGER NULL,
    error_type TEXT NULL,
    error_message TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_connection_events_user_created
    ON connection_events(user_id, created_at DESC);

-- Independent from 30-day operation telemetry retention: unresolved client
-- executions are never pruned by age. The Git ledger remains task authority.
CREATE TABLE IF NOT EXISTS agent_execution_witness (
    request_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    project_name TEXT NULL,
    tool_name TEXT NULL,
    state TEXT NOT NULL CHECK(state IN ('Admitted','Dispatched','Terminal','NotDispatched')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    device_id TEXT NULL,
    connection_id TEXT NULL,
    device_generation INTEGER NULL,
    correlation_id TEXT UNIQUE NULL,
    terminal_at TEXT NULL,
    parent_request_id TEXT NULL,
    job_id TEXT NULL,
    job_started_at TEXT NULL,
    job_receipt_at TEXT NULL,
    tool_job_id TEXT NULL,
    tool_job_operation_id TEXT NULL,
    tool_job_started_at TEXT NULL,
    tool_job_receipt_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent_execution_witness_pending
    ON agent_execution_witness(user_id, agent_id, state);

-- Before sending GetJobResult, record the exact authenticated poll route
-- against its original still-dispatched pending execution. An arbitrary
-- response-shaped JSON value must never attest some other agent's job.
-- Retain this across timeouts/restarts; no telemetry TTL applies.
CREATE TABLE IF NOT EXISTS agent_tool_job_poll (
    correlation_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    connection_id TEXT NOT NULL,
    device_generation INTEGER NOT NULL,
    agent_id TEXT NOT NULL,
    project_name TEXT NOT NULL,
    job_id TEXT NOT NULL,
    original_request_id TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent_tool_job_poll_original
    ON agent_tool_job_poll(user_id, original_request_id);

-- The correction barrier must survive a server restart or lost HTTP result.
-- It serializes with agent admission under a SQLite writer transaction.
CREATE TABLE IF NOT EXISTS agent_correction_permit (
    user_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    permit_id TEXT NOT NULL UNIQUE,
    operation_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(user_id, agent_id)
);

-- A migration from pre-witness server protocols through 3.64 cannot infer
-- safety from zero rows: an old MCP request may still execute on another device.
-- Explicit authenticated drain proof is required before ANY correction.
CREATE TABLE IF NOT EXISTS agent_execution_epoch (
    epoch_id INTEGER PRIMARY KEY CHECK(epoch_id = 1),
    state TEXT NOT NULL CHECK(state IN ('AwaitingLegacyDrain','Ready')),
    operator_id TEXT NULL,
    approval_operation_id TEXT UNIQUE NULL,
    evidence TEXT NULL,
    approved_at TEXT NULL,
    protocol_version TEXT NULL
);
INSERT OR IGNORE INTO agent_execution_epoch
    (epoch_id, state) VALUES (1, 'AwaitingLegacyDrain');
"""
        schema.ExecuteNonQuery() |> ignore

        // A Ready approval from a prior protocol is not proof of the current
        // execution contract. Add the epoch version without backfilling old
        // approvals: they must be reverified with a fresh drain operation.
        use epochInfo = connection.CreateCommand()
        epochInfo.CommandText <- "PRAGMA table_info(agent_execution_epoch);"
        use epochReader = epochInfo.ExecuteReader()
        let epochColumns = HashSet<string>(StringComparer.Ordinal)
        while epochReader.Read() do
            epochColumns.Add(epochReader.GetString 1) |> ignore
        epochReader.Close()
        if not (epochColumns.Contains("protocol_version")) then
            use epochMigration = connection.CreateCommand()
            epochMigration.CommandText <-
                "ALTER TABLE agent_execution_epoch ADD COLUMN protocol_version TEXT NULL;"
            epochMigration.ExecuteNonQuery() |> ignore

        // Retain compatibility with persisted pre-continuation witness data
        // without reinitializing the audit epoch or discarding unknown rows.
        use witnessInspect = connection.CreateCommand()
        witnessInspect.CommandText <- "PRAGMA table_info(agent_execution_witness);"
        use witnessReader = witnessInspect.ExecuteReader()
        let witnessColumns = HashSet<string>(StringComparer.Ordinal)
        while witnessReader.Read() do
            witnessColumns.Add(witnessReader.GetString 1) |> ignore
        witnessReader.Close()
        for column, definition in
            [ "parent_request_id", "TEXT NULL"
              "tool_name", "TEXT NULL"
              "job_id", "TEXT NULL"
              "job_started_at", "TEXT NULL"
              "job_receipt_at", "TEXT NULL"
              "tool_job_id", "TEXT NULL"
              "tool_job_operation_id", "TEXT NULL"
              "tool_job_started_at", "TEXT NULL"
              "tool_job_receipt_at", "TEXT NULL" ] do
            if not (witnessColumns.Contains(column)) then
                use witnessMigration = connection.CreateCommand()
                witnessMigration.CommandText <-
                    $"ALTER TABLE agent_execution_witness ADD COLUMN {column} {definition};"
                witnessMigration.ExecuteNonQuery() |> ignore

        // Telemetry is short-lived, but the execution classification must
        // outlive it. Existing witnesses are backfilled while their telemetry
        // still exists. A missing historical classification deliberately
        // remains NULL and cannot be certified Terminal without recovery.
        use backfill = connection.CreateCommand()
        backfill.CommandText <-
            """UPDATE agent_execution_witness
               SET tool_name = (SELECT tool_name FROM operations
                                WHERE operations.id = agent_execution_witness.request_id)
               WHERE tool_name IS NULL AND request_id IN (SELECT id FROM operations);
               UPDATE agent_execution_witness
               SET tool_name = 'NotifyAgent'
               WHERE tool_name IS NULL AND parent_request_id IS NOT NULL
                 AND EXISTS (
                     SELECT 1 FROM agent_execution_witness AS parent
                     WHERE parent.request_id = agent_execution_witness.parent_request_id
                       AND parent.user_id = agent_execution_witness.user_id
                       AND parent.tool_name = 'NotifyAgent'
                 );"""
        backfill.ExecuteNonQuery() |> ignore
        use childIndex = connection.CreateCommand()
        childIndex.CommandText <-
            """CREATE UNIQUE INDEX IF NOT EXISTS ix_agent_execution_child_parent
               ON agent_execution_witness(parent_request_id)
               WHERE parent_request_id IS NOT NULL;"""
        childIndex.ExecuteNonQuery() |> ignore
        // Job IDs belong to an authenticated client device, not a project or
        // process-wide ephemeral job registry. Duplicate IDs on that device
        // would make a later reconciliation ambiguous; fail the receipt.
        use jobIndex = connection.CreateCommand()
        jobIndex.CommandText <-
            """CREATE UNIQUE INDEX IF NOT EXISTS ix_agent_execution_job_device
               ON agent_execution_witness(user_id, device_id, job_id)
               WHERE job_id IS NOT NULL;"""
        jobIndex.ExecuteNonQuery() |> ignore
        // Promoted MCP tool jobs share the same client registry as explicit
        // StartJob process jobs. Reusing either ID on a device is ambiguous.
        use deferredJobIndex = connection.CreateCommand()
        deferredJobIndex.CommandText <-
            """CREATE UNIQUE INDEX IF NOT EXISTS ix_agent_execution_any_job_device
               ON agent_execution_witness(
                   user_id, device_id, COALESCE(job_id, tool_job_id))
               WHERE job_id IS NOT NULL OR tool_job_id IS NOT NULL;"""
        deferredJobIndex.ExecuteNonQuery() |> ignore

        use inspect = connection.CreateCommand()
        inspect.CommandText <- "PRAGMA table_info(feedback);"
        use reader = inspect.ExecuteReader()
        let columns = ResizeArray<string>()
        while reader.Read() do columns.Add(reader.GetString(1))
        reader.Close()
        for column, definition in
            [ "status", "TEXT NOT NULL DEFAULT 'Open'"
              "resolved_at", "TEXT NULL"
              "resolution_commit", "TEXT NULL"
              "resolution_notes", "TEXT NULL" ] do
            if not (columns.Contains(column)) then
                use migration = connection.CreateCommand()
                migration.CommandText <- $"ALTER TABLE feedback ADD COLUMN {column} {definition};"
                migration.ExecuteNonQuery() |> ignore

    let pruneTelemetry (now: DateTimeOffset) =
        use connection = openConnection()
        use transaction = connection.BeginTransaction()
        let cutoff = now - telemetryRetention
        for table in [ "connection_events"; "operations" ] do
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- $"DELETE FROM {table} WHERE created_at < $cutoff;"
            addParameter command "$cutoff" (cutoff.ToString("O"))
            command.ExecuteNonQuery() |> ignore
        transaction.Commit()

    do initialize()
    do pruneTelemetry DateTimeOffset.UtcNow
    let pruneTimer =
        new Timer(TimerCallback(fun _ ->
            try pruneTelemetry DateTimeOffset.UtcNow
            with _ -> ()), null, TimeSpan.FromHours 1.0, TimeSpan.FromHours 1.0)

    member _.PruneTelemetry(now: DateTimeOffset) = pruneTelemetry now

    interface IDisposable with
        member _.Dispose() = pruneTimer.Dispose()

    member _.DatabasePath = databasePath

    member _.AddConnectionEvent(
        userId: string,
        eventType: string,
        connectionId: string,
        deviceId: string option,
        deviceName: string option,
        generation: int64 option,
        clientVersion: string option,
        protocolVersion: string option,
        durationMs: int64 option,
        errorType: string option,
        errorMessage: string option
    ) =
        let createdAt = DateTimeOffset.UtcNow
        let bounded maxLength (value: string option) =
            value
            |> Option.map (fun text ->
                if text.Length <= maxLength then text
                else text.Substring(0, maxLength))

        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
INSERT INTO connection_events (
    created_at, user_id, event_type, connection_id,
    device_id, device_name, generation, client_version, protocol_version,
    duration_ms, error_type, error_message
) VALUES (
    $createdAt, $userId, $eventType, $connectionId,
    $deviceId, $deviceName, $generation, $clientVersion, $protocolVersion,
    $durationMs, $errorType, $errorMessage
);
"""

        addParameter command "$createdAt" (createdAt.ToString("O"))
        addParameter command "$userId" userId
        addParameter command "$eventType" eventType
        addParameter command "$connectionId" connectionId
        addParameter command "$deviceId" (dbValue (bounded 256 deviceId))
        addParameter command "$deviceName" (dbValue (bounded 256 deviceName))
        addParameter command "$generation" (dbValue generation)
        addParameter command "$clientVersion" (dbValue (bounded 128 clientVersion))
        addParameter command "$protocolVersion" (dbValue (bounded 128 protocolVersion))
        addParameter command "$durationMs" (dbValue durationMs)
        addParameter command "$errorType" (dbValue (bounded 512 errorType))
        addParameter command "$errorMessage" (dbValue (bounded 4000 errorMessage))
        command.ExecuteNonQuery() |> ignore

    member _.ListConnectionEvents(userId: string, limit: int) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
SELECT created_at, event_type, connection_id, device_id, device_name,
       generation, client_version, protocol_version, duration_ms,
       error_type, error_message
FROM connection_events
WHERE user_id = $userId
ORDER BY created_at DESC
LIMIT $limit;
"""
        addParameter command "$userId" userId
        addParameter command "$limit" limit

        use reader = command.ExecuteReader()
        let results = ResizeArray<ConnectionEventEntry>()

        while reader.Read() do
            results.Add(
                { CreatedAt = DateTimeOffset.Parse(reader.GetString 0)
                  EventType = reader.GetString 1
                  ConnectionId = reader.GetString 2
                  DeviceId = optionText reader 3 |> textOrEmpty
                  DeviceName = optionText reader 4 |> textOrEmpty
                  Generation = if reader.IsDBNull 5 then Nullable() else Nullable(reader.GetInt64 5)
                  ClientVersion = optionText reader 6 |> textOrEmpty
                  ProtocolVersion = optionText reader 7 |> textOrEmpty
                  DurationMs = if reader.IsDBNull 8 then Nullable() else Nullable(reader.GetInt64 8)
                  ErrorType = optionText reader 9 |> textOrEmpty
                  ErrorMessage = optionText reader 10 |> textOrEmpty }
            )

        results.ToArray()

    member _.BeginOperation(
        userId: string,
        toolName: string,
        projectName: string option,
        serverVersion: string,
        clientVersion: string option,
        protocolVersion: string option
    ) =
        let startedAt = DateTimeOffset.UtcNow
        let operationId = Guid.NewGuid().ToString("N")

        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
INSERT INTO operations (
    id, created_at, user_id, project_name, tool_name,
    server_version, client_version, protocol_version
) VALUES (
    $id, $createdAt, $userId, $projectName, $toolName,
    $serverVersion, $clientVersion, $protocolVersion
);
"""
        addParameter command "$id" operationId
        addParameter command "$createdAt" (startedAt.ToString("O"))
        addParameter command "$userId" userId
        addParameter command "$projectName" (dbValue projectName)
        addParameter command "$toolName" toolName
        addParameter command "$serverVersion" serverVersion
        addParameter command "$clientVersion" (dbValue clientVersion)
        addParameter command "$protocolVersion" (dbValue protocolVersion)
        command.ExecuteNonQuery() |> ignore

        { Id = operationId; StartedAt = startedAt }

    // A freshly deployed witness schema has no knowledge of MCP calls
    // dispatched by the previous server. An empty witness table is therefore
    // never sufficient evidence to permit correction.
    // Both observation and exclusive correction admission use the SAME
    // verified epoch predicate. A partially migrated/corrupt Ready marker
    // must not authorize a state change.
    member private _.ReadVerifiedExecutionEpoch(
        connection: SqliteConnection, transaction: SqliteTransaction option
    ) : Result<AgentExecutionReadiness, string> =
        use command = connection.CreateCommand()
        transaction |> Option.iter (fun current -> command.Transaction <- current)
        command.CommandText <-
            """SELECT state, operator_id, approval_operation_id, evidence,
                      approved_at, protocol_version
               FROM agent_execution_epoch WHERE epoch_id = 1;"""
        use reader = command.ExecuteReader()
        if not (reader.Read()) then Error "Execution epoch record is missing."
        else
            match reader.GetString 0 with
            | "AwaitingLegacyDrain" -> Ok LegacyDrainUnverified
            | "Ready" when reader.IsDBNull 5
                           || reader.GetString 5 <> AgentProtocol.version ->
                // An old or missing protocol approval is not reusable.
                Ok LegacyDrainUnverified
            | "Ready" when [1; 2; 3; 4] |> List.exists reader.IsDBNull ->
                Error "Execution epoch approval evidence is incomplete."
            | "Ready" ->
                let operatorId = reader.GetString 1
                let approvalId = reader.GetString 2
                let evidence = reader.GetString 3
                let approvedAt = reader.GetString 4
                if String.IsNullOrWhiteSpace operatorId
                   || String.IsNullOrWhiteSpace approvalId
                   || String.IsNullOrWhiteSpace evidence
                   || evidence.Trim().Length < 20 then
                    Error "Execution epoch approval evidence is invalid."
                else
                    match DateTimeOffset.TryParse approvedAt with
                    | true, at -> Ok (LegacyDrainVerified(operatorId, evidence, at))
                    | _ -> Error "Execution epoch approval timestamp is corrupt."
            | _ -> Error "Execution epoch is not a verified protocol state."

    member this.GetAgentExecutionReadiness() : Result<AgentExecutionReadiness, string> =
        try
            use connection = openConnection()
            this.ReadVerifiedExecutionEpoch(connection, None)
        with ex -> Error $"Cannot read execution protocol readiness: {ex.Message}"

    // Caller must be the authenticated operator path after proving that ALL
    // pre-witness requests and detached child jobs have exited across devices.
    // This is intentionally not exposed through MCP until real device/process
    // reconciliation can supply that proof. Never auto-ready on startup.
    member this.TryAuthorizeLegacyDrain(
        operatorId: string, operationId: string, evidence: string,
        verifyLegacyQuiescence: unit -> Result<unit, string>
    ) : Result<unit, string> =
        if String.IsNullOrWhiteSpace operatorId
           || String.IsNullOrWhiteSpace operationId
           || String.IsNullOrWhiteSpace evidence
           || evidence.Trim().Length < 20 then
            Error "Protocol readiness requires an operator, stable operation ID and substantive drain evidence."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use query = connection.CreateCommand()
                query.Transaction <- transaction
                query.CommandText <-
                    """SELECT state, approval_operation_id, protocol_version
                       FROM agent_execution_epoch WHERE epoch_id = 1;"""
                use reader = query.ExecuteReader()
                let epoch =
                    if reader.Read() then
                        Some(reader.GetString 0,
                             (if reader.IsDBNull 1 then "" else reader.GetString 1),
                             (if reader.IsDBNull 2 then None else Some(reader.GetString 2)))
                    else None
                reader.Close()
                match epoch with
                | Some("Ready", original, Some version)
                    when version = AgentProtocol.version && original = operationId ->
                    match this.ReadVerifiedExecutionEpoch(connection, Some transaction) with
                    | Ok (LegacyDrainVerified(originalOperator, originalEvidence, _))
                        when originalOperator = operatorId.Trim()
                             && originalEvidence = evidence.Trim() ->
                        Ok () // same verified command; no new approval or mutation
                    | Ok (LegacyDrainVerified _) ->
                        Error "Legacy drain approval operation ID was reused with different operator or evidence."
                    | _ -> Error "Persisted legacy-drain approval is incomplete or corrupt."
                | Some("Ready", _, Some version) when version = AgentProtocol.version ->
                    Error "Protocol epoch was already approved by a different operation."
                | Some("Ready", _, _) ->
                    // No silent reuse or destructive overwrite of a prior
                    // protocol's approval history. A new audited epoch is
                    // required before the upgraded server can correct agents.
                    Error "A prior protocol's legacy drain cannot authorize the current protocol epoch."
                | Some("AwaitingLegacyDrain", _, _) ->
                    // This SQLite writer transaction serializes the *final*
                    // legacy-process verification with all new admissions.
                    // No pre-witness client may dispatch on this server.
                    let verified =
                        try verifyLegacyQuiescence()
                        with ex -> Error ex.Message
                    match verified with
                    | Error reason ->
                        Error $"Legacy activity was not proven quiescent: {reason}"
                    | Ok () ->
                        use unknown = connection.CreateCommand()
                        unknown.Transaction <- transaction
                        unknown.CommandText <-
                            """SELECT 1 FROM agent_execution_witness
                               WHERE state NOT IN ('Terminal','NotDispatched') LIMIT 1;"""
                        if not (isNull (unknown.ExecuteScalar())) then
                            Error "Current tracked activity remains unresolved; epoch cannot become ready."
                        else
                            use update = connection.CreateCommand()
                            update.Transaction <- transaction
                            update.CommandText <-
                                """UPDATE agent_execution_epoch SET
                                       state = 'Ready', operator_id = $operator,
                                       approval_operation_id = $operation,
                                       evidence = $evidence, approved_at = $at,
                                       protocol_version = $protocol
                                   WHERE epoch_id = 1 AND state = 'AwaitingLegacyDrain';"""
                            addParameter update "$operator" (operatorId.Trim())
                            addParameter update "$operation" (operationId.Trim())
                            addParameter update "$evidence" (evidence.Trim())
                            addParameter update "$at" (DateTimeOffset.UtcNow.ToString("O"))
                            addParameter update "$protocol" AgentProtocol.version
                            if update.ExecuteNonQuery() <> 1 then
                                Error "Protocol readiness changed during verification."
                            else
                                transaction.Commit()
                                Ok ()
                | _ -> Error "Execution epoch is missing or corrupt."
            with ex -> Error $"Cannot audit protocol drain approval: {ex.Message}"

    // This is separate from best-effort telemetry BeginOperation: a genuine
    // self-agent tool MUST have a durable witness before it can be dispatched.
    // One SQLite writer transaction contains the telemetry row and activity
    // admission. The rare correction permit checks the same SQLite database.
    member _.TryBeginAgentOperation(
        userId: string, agentId: string, toolName: string,
        projectName: string option, serverVersion: string,
        clientVersion: string option, protocolVersion: string option
    ) : Result<OperationStart, string> =
        if String.IsNullOrWhiteSpace userId || String.IsNullOrWhiteSpace agentId
           || agentId.Length > 256 then
            Error "The authenticated user and bounded initiating agent are required."
        else
            try
                let normalized = agentId.ToUpperInvariant()
                let startedAt = DateTimeOffset.UtcNow
                let requestId = Guid.NewGuid().ToString("N")
                use connection = openConnection()
                // SQLite's default non-deferred transaction takes the writer
                // reservation before reading the correction gate: no check/
                // insertion race with an operator acquisition.
                use transaction = connection.BeginTransaction()
                use existing = connection.CreateCommand()
                existing.Transaction <- transaction
                existing.CommandText <-
                    """SELECT 1 FROM agent_correction_permit
                       WHERE user_id = $userId AND agent_id = $agentId LIMIT 1;"""
                addParameter existing "$userId" userId
                addParameter existing "$agentId" normalized
                if not (isNull (existing.ExecuteScalar())) then
                    Error "Administrative correction is currently excluding this agent's commands."
                else
                    use telemetry = connection.CreateCommand()
                    telemetry.Transaction <- transaction
                    telemetry.CommandText <-
                        """INSERT INTO operations (
                               id, created_at, user_id, project_name, tool_name,
                               server_version, client_version, protocol_version
                           ) VALUES (
                               $id, $createdAt, $userId, $projectName, $toolName,
                               $serverVersion, $clientVersion, $protocolVersion
                           );"""
                    addParameter telemetry "$id" requestId
                    addParameter telemetry "$createdAt" (startedAt.ToString("O"))
                    addParameter telemetry "$userId" userId
                    addParameter telemetry "$projectName" (dbValue projectName)
                    addParameter telemetry "$toolName" toolName
                    addParameter telemetry "$serverVersion" serverVersion
                    addParameter telemetry "$clientVersion" (dbValue clientVersion)
                    addParameter telemetry "$protocolVersion" (dbValue protocolVersion)
                    telemetry.ExecuteNonQuery() |> ignore
                    use witness = connection.CreateCommand()
                    witness.Transaction <- transaction
                    witness.CommandText <-
                        """INSERT INTO agent_execution_witness (
                               request_id, user_id, agent_id, project_name,
                               tool_name, state, created_at, updated_at
                           ) VALUES (
                               $id, $userId, $agentId, $projectName,
                               $toolName, 'Admitted', $createdAt, $createdAt
                           );"""
                    addParameter witness "$id" requestId
                    addParameter witness "$userId" userId
                    addParameter witness "$agentId" normalized
                    addParameter witness "$projectName" (dbValue projectName)
                    addParameter witness "$toolName" toolName
                    addParameter witness "$createdAt" (startedAt.ToString("O"))
                    witness.ExecuteNonQuery() |> ignore
                    transaction.Commit()
                    Ok { Id = requestId; StartedAt = startedAt }
            with ex ->
                Error $"Cannot record durable agent admission: {ex.Message}"

    // For a two-stage cross-project notification, the source AgentInbox
    // validation is a genuine agent operation. Its exact client-terminal
    // receipt must be persisted *before* a separate destination dispatch.
    // Create one child witness without duplicating parent MCP telemetry.
    // The same writer transaction excludes a concurrent correction of the
    // source agent between the two independently correlated dispatches.
    member _.TryBeginAgentContinuation(
        userId: string, parentRequestId: string, destinationProject: string
    ) : Result<string, string> =
        if String.IsNullOrWhiteSpace userId
           || String.IsNullOrWhiteSpace parentRequestId
           || String.IsNullOrWhiteSpace destinationProject then
            Error "An authenticated terminal parent and destination are required."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use parent = connection.CreateCommand()
                parent.Transaction <- transaction
                parent.CommandText <-
                    """SELECT agent_id, state, parent_request_id
                       FROM agent_execution_witness
                       WHERE user_id = $userId AND request_id = $parent LIMIT 1;"""
                addParameter parent "$userId" userId
                addParameter parent "$parent" parentRequestId
                use parentReader = parent.ExecuteReader()
                let root =
                    if parentReader.Read() then
                        Some(
                            parentReader.GetString 0,
                            parentReader.GetString 1,
                            parentReader.IsDBNull 2)
                    else None
                parentReader.Close()
                match root with
                | Some(agent, "Terminal", true) ->
                    use correction = connection.CreateCommand()
                    correction.Transaction <- transaction
                    correction.CommandText <-
                        """SELECT 1 FROM agent_correction_permit
                           WHERE user_id = $userId AND agent_id = $agent LIMIT 1;"""
                    addParameter correction "$userId" userId
                    addParameter correction "$agent" agent
                    if not (isNull (correction.ExecuteScalar())) then
                        Error "Correction permit excludes the source agent's continuation."
                    else
                        let childId = Guid.NewGuid().ToString("N")
                        let now = DateTimeOffset.UtcNow.ToString("O")
                        use insert = connection.CreateCommand()
                        insert.Transaction <- transaction
                        insert.CommandText <-
                            """INSERT INTO agent_execution_witness (
                                   request_id, user_id, agent_id, project_name,
                                   tool_name, state, created_at, updated_at, parent_request_id
                               ) VALUES (
                                   $childId, $userId, $agent, $project,
                                   'NotifyAgent', 'Admitted', $now, $now, $parent
                               );"""
                        addParameter insert "$childId" childId
                        addParameter insert "$userId" userId
                        addParameter insert "$agent" agent
                        addParameter insert "$project" destinationProject
                        addParameter insert "$now" now
                        addParameter insert "$parent" parentRequestId
                        insert.ExecuteNonQuery() |> ignore
                        transaction.Commit()
                        Ok childId
                | Some (_, "Admitted", _) | Some (_, "Dispatched", _) ->
                    Error "Source validation is still running or its terminal receipt is unavailable."
                | Some (_, "NotDispatched", _) ->
                    Error "Source validation was not dispatched; continuation is forbidden."
                | Some (_, "Terminal", false) ->
                    Error "Cannot create a continuation from an already nested request."
                | _ ->
                    Error "Parent client terminal proof was not found."
            with ex ->
                Error $"Cannot durably admit cross-project continuation: {ex.Message}"

    // Claim the exclusive operator correction barrier in the same SQLite
    // writer-serialization domain as TryBeginAgentOperation. A pending or
    // dispatched request vetoes it, including after server restart.
    member this.TryAcquireAgentCorrection(
        userId: string, targetAgentId: string, operationId: string
    ) : Result<string, string> =
        if String.IsNullOrWhiteSpace userId
           || String.IsNullOrWhiteSpace targetAgentId
           || targetAgentId.Length > 256
           || String.IsNullOrWhiteSpace operationId then
            Error "Correction requires an authenticated user, target agent and stable operation ID."
        else
            try
                let agent = targetAgentId.ToUpperInvariant()
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use current = connection.CreateCommand()
                current.Transaction <- transaction
                current.CommandText <-
                    """SELECT permit_id, operation_id FROM agent_correction_permit
                       WHERE user_id = $userId AND agent_id = $agentId LIMIT 1;"""
                addParameter current "$userId" userId
                addParameter current "$agentId" agent
                use existing = current.ExecuteReader()
                let prior =
                    if existing.Read() then Some(existing.GetString 0, existing.GetString 1)
                    else None
                existing.Close()
                let epochReady =
                    match this.ReadVerifiedExecutionEpoch(connection, Some transaction) with
                    | Ok (LegacyDrainVerified _) -> true
                    | _ -> false
                match prior with
                | _ when not epochReady ->
                    Error "Protocol epoch has not proven pre-witness legacy activity quiescent."
                | Some (permit, previousOperation) when previousOperation = operationId ->
                    Ok permit // idempotent recovery of the same unresolved permit
                | Some _ -> Error "Another administrative correction is unresolved."
                | None ->
                    use busy = connection.CreateCommand()
                    busy.Transaction <- transaction
                    busy.CommandText <-
                        """SELECT 1 FROM agent_execution_witness
                           WHERE user_id = $userId AND agent_id = $agentId
                           AND state NOT IN ('Terminal', 'NotDispatched')
                           LIMIT 1;"""
                    addParameter busy "$userId" userId
                    addParameter busy "$agentId" agent
                    if not (isNull (busy.ExecuteScalar())) then
                        Error "Cannot prove the agent is inactive: unresolved execution witness exists."
                    else
                        let permit = Guid.NewGuid().ToString("N")
                        use insert = connection.CreateCommand()
                        insert.Transaction <- transaction
                        insert.CommandText <-
                            """INSERT INTO agent_correction_permit
                               (user_id, agent_id, permit_id, operation_id, created_at)
                               VALUES ($userId, $agentId, $permit, $operationId, $now);"""
                        addParameter insert "$userId" userId
                        addParameter insert "$agentId" agent
                        addParameter insert "$permit" permit
                        addParameter insert "$operationId" operationId
                        addParameter insert "$now" (DateTimeOffset.UtcNow.ToString("O"))
                        insert.ExecuteNonQuery() |> ignore
                        transaction.Commit()
                        Ok permit
            with ex ->
                Error $"Cannot acquire durable correction barrier: {ex.Message}"

    // A correction gate does not disappear on timeout, network failure or
    // server restart. The caller must first establish a definitive canonical
    // Git operation result (or independently prove the operation did not run).
    member _.TryReleaseAgentCorrection(
        userId: string, targetAgentId: string, permitId: string,
        canonicalOutcomeResolved: bool
    ) : Result<unit, string> =
        if not canonicalOutcomeResolved then
            Error "Ambiguous canonical correction; exclusive gate remains held."
        else
            try
                use connection = openConnection()
                use command = connection.CreateCommand()
                command.CommandText <-
                    """DELETE FROM agent_correction_permit
                       WHERE user_id = $userId AND agent_id = $agentId
                       AND permit_id = $permit;"""
                addParameter command "$userId" userId
                addParameter command "$agentId" (targetAgentId.ToUpperInvariant())
                addParameter command "$permit" permitId
                if command.ExecuteNonQuery() = 1 then Ok ()
                else Error "Unknown or stale correction permit."
            with ex -> Error $"Cannot release correction barrier: {ex.Message}"

    /// Inspect exactly one unresolved correction permit without admitting work,
    /// changing its owner or clearing the gate. A missing permit is not proof
    /// that an agent is inactive: also check execution witnesses and epoch.
    member _.InspectAgentCorrectionPermit(
        userId: string, targetAgentId: string
    ) : Result<AgentCorrectionPermitEvidence option, string> =
        if String.IsNullOrWhiteSpace userId
           || String.IsNullOrWhiteSpace targetAgentId
           || targetAgentId.Length > 256 then
            Error "Authenticated user and bounded target agent are required."
        else
            try
                use connection = openConnection()
                use command = connection.CreateCommand()
                command.CommandText <-
                    """SELECT agent_id, permit_id, operation_id, created_at
                       FROM agent_correction_permit
                       WHERE user_id = $userId AND agent_id = $agentId LIMIT 1;"""
                addParameter command "$userId" userId
                addParameter command "$agentId" (targetAgentId.ToUpperInvariant())
                use reader = command.ExecuteReader()
                if not (reader.Read()) then Ok None
                elif [0; 1; 2; 3] |> List.exists reader.IsDBNull then
                    Error "Durable correction permit is incomplete."
                else
                    let agentId = reader.GetString 0
                    let permitId = reader.GetString 1
                    let operationId = reader.GetString 2
                    let timestamp = reader.GetString 3
                    if String.IsNullOrWhiteSpace agentId
                       || String.IsNullOrWhiteSpace permitId
                       || String.IsNullOrWhiteSpace operationId then
                        Error "Durable correction permit identity is malformed."
                    else
                        match DateTimeOffset.TryParse timestamp with
                        | true, at ->
                            Ok(Some
                                { TargetAgentId = agentId; PermitId = permitId
                                  OperationId = operationId; CreatedAt = at })
                        | _ -> Error "Durable correction permit timestamp is malformed."
            with ex -> Error $"Cannot inspect correction permit evidence: {ex.Message}"

    // Bind the exact routed connection before SignalR dispatch is attempted.
    // Once bound, an HTTP cancellation or server timeout leaves the witness
    // Dispatched until authenticated client-terminal evidence arrives.
    member _.TryMarkAgentDispatched(
        userId: string, requestId: string,
        deviceId: string, connectionId: string, generation: int64,
        correlationId: string
    ) : Result<unit, string> =
        if [ userId; requestId; deviceId; connectionId; correlationId ]
           |> List.exists String.IsNullOrWhiteSpace
           || generation < 1L then
            Error "Dispatch requires authenticated user, request, device, connection, generation and correlation."
        else
            try
                use connection = openConnection()
                use command = connection.CreateCommand()
                command.CommandText <-
                    """UPDATE agent_execution_witness
                       SET state = 'Dispatched', device_id = $deviceId,
                           connection_id = $connectionId,
                           device_generation = $generation, correlation_id = $correlationId,
                           updated_at = $now
                       WHERE user_id = $userId AND request_id = $requestId
                           AND state = 'Admitted';"""
                addParameter command "$userId" userId
                addParameter command "$requestId" requestId
                addParameter command "$deviceId" deviceId
                addParameter command "$connectionId" connectionId
                addParameter command "$generation" generation
                addParameter command "$correlationId" correlationId
                addParameter command "$now" (DateTimeOffset.UtcNow.ToString("O"))
                if command.ExecuteNonQuery() = 1 then Ok ()
                else Error "Dispatch request is missing or already left the admitted state."
            with ex -> Error $"Cannot persist exact agent dispatch: {ex.Message}"

    // Only call when the request is *provably* not sent to the client.
    // A SignalR timeout or exception after calling ReceiveCommand cannot
    // prove this and must leave the witness unresolved.
    member _.TryMarkAgentNotDispatched(userId: string, requestId: string) : Result<unit, string> =
        try
            use connection = openConnection()
            use command = connection.CreateCommand()
            command.CommandText <-
                """UPDATE agent_execution_witness
                   SET state = 'NotDispatched', updated_at = $now, terminal_at = $now
                   WHERE user_id = $userId AND request_id = $requestId
                       AND state = 'Admitted';"""
            addParameter command "$userId" userId
            addParameter command "$requestId" requestId
            addParameter command "$now" (DateTimeOffset.UtcNow.ToString("O"))
            if command.ExecuteNonQuery() = 1 then Ok ()
            else Error "Cannot certify non-dispatch for this request."
        with ex -> Error $"Cannot record proven non-dispatch: {ex.Message}"

    // This query is part of the correction release proof. It may be called
    // only after the client response is known, but does NOT itself certify
    // that a Git operation committed. Missing/corrupt rows fail closed.
    member _.GetAgentExecutionResolution(userId: string, requestId: string)
        : Result<AgentExecutionResolution, string> =
        if String.IsNullOrWhiteSpace userId || String.IsNullOrWhiteSpace requestId then
            Error "Authenticated operation identity is required."
        else
            try
                use connection = openConnection()
                use command = connection.CreateCommand()
                command.CommandText <-
                    """SELECT state FROM agent_execution_witness
                       WHERE user_id = $userId AND request_id = $requestId LIMIT 1;"""
                addParameter command "$userId" userId
                addParameter command "$requestId" requestId
                match command.ExecuteScalar() with
                | :? string as status ->
                    match status with
                    | "Terminal" -> Ok AuthenticatedClientFinished
                    | "NotDispatched" -> Ok NeverDispatched
                    | "Admitted" | "Dispatched" -> Ok ExecutionUnresolved
                    | _ -> Error "Execution witness status is invalid."
                | _ -> Error "Execution witness was not found for authenticated request."
            with ex -> Error $"Cannot read exact execution witness: {ex.Message}"

    /// Persist the exact server-issued GetJobResult route before dispatch.
    /// The client reply cannot authoritatively finish any other job or actor.
    /// Missing original evidence is normal for unrelated job polling and
    /// cannot be treated as proof of completion.
    member _.TryRecordAgentToolJobPollDispatch(
        userId: string, deviceId: string, connectionId: string,
        generation: int64, correlationId: string, agentId: string,
        projectName: string, jobId: string
    ) : Result<string option, string> =
        let mutable parsed = Guid.Empty
        if [userId; deviceId; connectionId; correlationId; agentId; projectName; jobId]
           |> List.exists (fun value -> String.IsNullOrWhiteSpace value || value.Length > 512)
           || generation < 1L
           || not (Guid.TryParseExact(jobId, "N", &parsed))
           || parsed = Guid.Empty
           || jobId <> parsed.ToString("N") then
            Error "Authenticated bounded GetJobResult dispatch route and canonical job identity are required."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use lookup = connection.CreateCommand()
                lookup.Transaction <- transaction
                lookup.CommandText <-
                    """SELECT request_id FROM agent_execution_witness
                       WHERE user_id = $userId AND agent_id = $agentId
                         AND device_id = $deviceId
                         AND connection_id = $connectionId
                         AND device_generation = $generation
                         AND project_name = $projectName
                         AND tool_job_id = $jobId
                         AND state = 'Dispatched'
                         AND tool_job_receipt_at IS NOT NULL
                       LIMIT 1;"""
                addParameter lookup "$userId" userId
                addParameter lookup "$agentId" (agentId.ToUpperInvariant())
                addParameter lookup "$deviceId" deviceId
                addParameter lookup "$connectionId" connectionId
                addParameter lookup "$generation" generation
                addParameter lookup "$projectName" projectName
                addParameter lookup "$jobId" jobId
                match lookup.ExecuteScalar() with
                | :? string as requestId ->
                    use record = connection.CreateCommand()
                    record.Transaction <- transaction
                    record.CommandText <-
                        """INSERT INTO agent_tool_job_poll
                           (correlation_id, user_id, device_id, connection_id,
                            device_generation, agent_id, project_name, job_id,
                            original_request_id, created_at)
                           VALUES ($correlation, $userId, $deviceId, $connectionId,
                                   $generation, $agentId, $projectName, $jobId,
                                   $requestId, $now);"""
                    addParameter record "$correlation" correlationId
                    addParameter record "$userId" userId
                    addParameter record "$deviceId" deviceId
                    addParameter record "$connectionId" connectionId
                    addParameter record "$generation" generation
                    addParameter record "$agentId" (agentId.ToUpperInvariant())
                    addParameter record "$projectName" projectName
                    addParameter record "$jobId" jobId
                    addParameter record "$requestId" requestId
                    addParameter record "$now" (DateTimeOffset.UtcNow.ToString("O"))
                    record.ExecuteNonQuery() |> ignore
                    transaction.Commit()
                    Ok(Some requestId)
                | _ -> Ok None
            with ex ->
                Error $"Cannot durably bind an authenticated tool-job poll: {ex.Message}"

    // A promoted MCP ToolOperation has returned a handle while the original
    // command is STILL RUNNING. Persist the handle on the exact routed witness,
    // never advance its state to Terminal, and never infer completion from a
    // subsequent polling response until a separate authenticated end-to-end
    // completion protocol is available.
    member _.TryRecordAgentDeferredToolJob(
        userId: string, connectionId: string, generation: int64,
        correlationId: string, actorId: string, commandName: string,
        jobId: string, startedAt: DateTimeOffset,
        ?operationId: string, ?projectName: string
    ) : Result<string option, string> =
        let mutable parsedId = Guid.Empty
        if [userId; connectionId; correlationId; actorId; commandName; jobId]
            |> List.exists String.IsNullOrWhiteSpace
           || generation < 1L
           || not (Guid.TryParseExact(jobId, "N", &parsedId))
           || parsedId = Guid.Empty
           || not (String.Equals(jobId, parsedId.ToString("N"), StringComparison.Ordinal))
           || startedAt = DateTimeOffset.MinValue
           || (operationId |> Option.exists (fun id ->
                String.IsNullOrWhiteSpace id || id.Length > 256))
           || (projectName |> Option.exists (fun project ->
                String.IsNullOrWhiteSpace project || project.Length > 256)) then
            Error "Deferred tool-job receipt requires authenticated bounded provenance and canonical GUID-N job identity."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use lookup = connection.CreateCommand()
                lookup.Transaction <- transaction
                lookup.CommandText <-
                    """SELECT request_id, agent_id, tool_name, state,
                              project_name, parent_request_id
                       FROM agent_execution_witness
                       WHERE user_id = $userId AND connection_id = $connectionId
                         AND device_generation = $generation AND correlation_id = $correlationId
                       LIMIT 1;"""
                addParameter lookup "$userId" userId
                addParameter lookup "$connectionId" connectionId
                addParameter lookup "$generation" generation
                addParameter lookup "$correlationId" correlationId
                use reader = lookup.ExecuteReader()
                let found =
                    if reader.Read() then
                        Some (reader.GetString 0, reader.GetString 1,
                              optionText reader 2, reader.GetString 3,
                              optionText reader 4, optionText reader 5)
                    else None
                reader.Close()
                match found with
                | None -> Ok None
                | Some (requestId, actor, Some tool, "Dispatched", witnessProject, parent)
                    when actor = actorId.ToUpperInvariant()
                         && knownCommandTypes.Contains tool
                         && (projectName.IsNone || projectName = witnessProject)
                         && (tool = commandName && tool <> "StartJob"
                             // NotifyAgent is a logical two-stage command:
                             // source is a TaskRead, and destination is a
                             // TaskWrite. Both retain the original actor and
                             // must match an exact authenticated route.
                             || tool = "NotifyAgent"
                                && (commandName = "TaskWrite"
                                    || parent.IsNone && commandName = "TaskRead")) ->
                    use record = connection.CreateCommand()
                    record.Transaction <- transaction
                    record.CommandText <-
                        """UPDATE agent_execution_witness
                           SET tool_job_id = $jobId,
                               tool_job_operation_id = $operationId,
                               tool_job_started_at = $startedAt,
                               tool_job_receipt_at = COALESCE(tool_job_receipt_at, $now),
                               updated_at = $now
                           WHERE request_id = $requestId
                             AND state = 'Dispatched' AND tool_name = $tool
                             AND job_id IS NULL
                             AND (tool_job_id IS NULL
                                  OR (tool_job_id = $jobId
                                      AND tool_job_started_at = $startedAt
                                      AND tool_job_operation_id IS $operationId));"""
                    addParameter record "$requestId" requestId
                    addParameter record "$tool" tool
                    addParameter record "$jobId" jobId
                    addParameter record "$operationId" (dbValue operationId)
                    addParameter record "$startedAt" (startedAt.ToString("O"))
                    addParameter record "$now" (DateTimeOffset.UtcNow.ToString("O"))
                    if record.ExecuteNonQuery() <> 1 then
                        Error "Conflicting deferred tool-job receipt; execution remains unresolved."
                    else
                        transaction.Commit()
                        Ok (Some requestId)
                | Some _ ->
                    Error "Deferred job does not match the admitted agent, command or unresolved route."
            with ex ->
                Error $"Cannot durably record deferred tool-job identity: {ex.Message}"

    /// A tool-job result can close the original witness only when its
    /// GetJobResult dispatch was durably bound to the exact authenticated
    /// client route before the poll. A returned JSON body alone is not proof.
    member _.TryRecordAgentToolJobPollTerminal(
        userId: string, connectionId: string, generation: int64,
        correlationId: string, clientResponse: AgentCommandResponse
    ) : Result<string option, string> =
        if [userId; connectionId; correlationId] |> List.exists String.IsNullOrWhiteSpace
           || generation < 1L then
            Error "Authenticated GetJobResult route is required."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use lookup = connection.CreateCommand()
                lookup.Transaction <- transaction
                lookup.CommandText <-
                    """SELECT original.request_id, original.state,
                              original.tool_name, original.tool_job_id,
                              original.tool_job_operation_id
                       FROM agent_tool_job_poll AS poll
                       JOIN agent_execution_witness AS original
                         ON original.request_id = poll.original_request_id
                         AND original.user_id = poll.user_id
                         AND original.agent_id = poll.agent_id
                         AND original.project_name = poll.project_name
                         AND original.device_id = poll.device_id
                         AND original.connection_id = poll.connection_id
                         AND original.device_generation = poll.device_generation
                         AND original.tool_job_id = poll.job_id
                       WHERE poll.user_id = $userId
                         AND poll.connection_id = $connectionId
                         AND poll.device_generation = $generation
                         AND poll.correlation_id = $correlationId
                       LIMIT 1;"""
                addParameter lookup "$userId" userId
                addParameter lookup "$connectionId" connectionId
                addParameter lookup "$generation" generation
                addParameter lookup "$correlationId" correlationId
                use reader = lookup.ExecuteReader()
                let found =
                    if reader.Read() then
                        Some(reader.GetString 0, reader.GetString 1,
                             optionText reader 2, reader.GetString 3,
                             optionText reader 4)
                    else None
                reader.Close()
                match found with
                | None -> Ok None
                | Some (requestId, "Terminal", _, _, _) ->
                    Ok(Some requestId) // authenticated idempotent replay
                | Some (_, "Dispatched", Some tool, _, _)
                    when not (terminalCommandTypes.Contains tool) ->
                    Error $"Tool {tool} still requires independent liveness evidence."
                | Some (requestId, "Dispatched", Some _, jobId, operationId) ->
                    let verified =
                        match clientResponse.Result, clientResponse.Error,
                              clientResponse.PendingJob with
                        | Some payload, None, None ->
                            try
                                let result = JsonSerializer.Deserialize<JobResult>(payload)
                                if isNull (box result) || result.JobId <> jobId
                                   || result.OperationId <> operationId then
                                    Error "Job or original operation identity differs from authenticated poll."
                                else
                                    match result.Status, result.ToolResponse with
                                    | ToolCompleted, Some final
                                        when final.PendingJob.IsNone && final.Error.IsNone -> Ok true
                                    | ToolFailed _, Some final
                                        when final.PendingJob.IsNone && final.Error.IsSome -> Ok true
                                    | Running, None -> Ok false // normal polling, not an error
                                    | _ -> Error "Job result does not prove completed tool execution."
                            with :? JsonException ->
                                Error "Malformed typed job-result response."
                        | _ -> Error "GetJobResult has no successful non-pending response."
                    match verified with
                    | Error reason -> Error reason
                    | Ok false -> Ok None // no terminal change while still running
                    | Ok true ->
                        use update = connection.CreateCommand()
                        update.Transaction <- transaction
                        update.CommandText <-
                            """UPDATE agent_execution_witness
                               SET state = 'Terminal', updated_at = $now,
                                   terminal_at = $now
                               WHERE request_id = $requestId AND state = 'Dispatched'
                                 AND tool_job_id = $jobId
                                 AND tool_job_operation_id IS $operationId;"""
                        addParameter update "$requestId" requestId
                        addParameter update "$jobId" jobId
                        addParameter update "$operationId" (dbValue operationId)
                        addParameter update "$now" (DateTimeOffset.UtcNow.ToString("O"))
                        if update.ExecuteNonQuery() <> 1 then
                            Error "Tool-job terminal proof raced conflicting execution evidence."
                        else
                            transaction.Commit()
                            Ok(Some requestId)
                | Some _ -> Error "Poll does not match unresolved dispatched execution."
            with ex ->
                Error $"Cannot attest authenticated tool-job termination: {ex.Message}"

    // The transport server must verify the authenticated sender's active
    // device registration before passing its connection/generation here.
    // Match the persisted route exactly and accept a late response even if
    // ClientResponseTracker already timed out and forgot its correlation.
    member _.TryRecordAgentClientTerminal(
        userId: string, connectionId: string, generation: int64, correlationId: string,
        ?clientResponse: AgentCommandResponse
    ) : Result<string option, string> =
        if [ userId; connectionId; correlationId ] |> List.exists String.IsNullOrWhiteSpace
           || generation < 1L then
            Error "An authenticated routed client receipt is required."
        else
            try
                use connection = openConnection()
                use transaction = connection.BeginTransaction()
                use command = connection.CreateCommand()
                command.Transaction <- transaction
                command.CommandText <-
                    """SELECT witness.request_id, witness.state, witness.tool_name
                       FROM agent_execution_witness AS witness
                       WHERE witness.user_id = $userId AND witness.connection_id = $connectionId
                         AND witness.device_generation = $generation AND witness.correlation_id = $correlationId
                       LIMIT 1;"""
                addParameter command "$userId" userId
                addParameter command "$connectionId" connectionId
                addParameter command "$generation" generation
                addParameter command "$correlationId" correlationId
                use reader = command.ExecuteReader()
                let found =
                    if reader.Read() then
                        Some(
                            reader.GetString 0,
                            reader.GetString 1,
                            if reader.IsDBNull 2 then None else Some(reader.GetString 2))
                    else None
                reader.Close()
                match found with
                | None -> Ok None // Untracked MCP replies do not carry an execution witness.
                | Some (requestId, "Terminal", _) -> Ok(Some requestId) // idempotent late duplicate
                | Some (_, "Dispatched", None) ->
                    // Historical telemetry may already have been pruned before
                    // migration. Without a durable command classification we
                    // cannot rule out a detached job; fail closed.
                    Error "The dispatched tool classification is unavailable; reconcile legacy execution before terminal proof."
                | Some (requestId, "Dispatched", Some "StartJob") ->
                    // The client reply is only a job handle. Persist its exact
                    // identity under the authenticated route for later process
                    // tree reconciliation, but NEVER mark this witness Terminal
                    // or release a correction permit on a mere StartJob reply.
                    let launch =
                        match clientResponse with
                        | Some { Result = Some payload; Error = None } ->
                            try
                                let result = JsonSerializer.Deserialize<StartJobResult>(payload)
                                let mutable parsedJobId = Guid.Empty
                                if isNull (box result)
                                   || String.IsNullOrWhiteSpace result.JobId
                                   || not (Guid.TryParseExact(result.JobId, "N", &parsedJobId))
                                   || parsedJobId = Guid.Empty
                                   || not (String.Equals(result.JobId, parsedJobId.ToString("N"), StringComparison.Ordinal))
                                   || result.StartedAt = DateTimeOffset.MinValue then
                                    // JobManager generates exactly Guid.NewGuid().ToString("N").
                                    // A free-form string is not a reconcilable client identity.
                                    Error "StartJob requires a canonical nonempty GUID-N job identity and start timestamp."
                                else Ok result
                            with :? JsonException ->
                                Error "StartJob returned an invalid job identity payload."
                        | _ ->
                            Error "StartJob has no authenticated successful launch receipt; process liveness is unresolved."
                    match launch with
                    | Error failure -> Error failure
                    | Ok job ->
                        let launchedAt = job.StartedAt.ToString("O")
                        use launchReceipt = connection.CreateCommand()
                        launchReceipt.Transaction <- transaction
                        launchReceipt.CommandText <-
                            """UPDATE agent_execution_witness
                               SET job_id = $jobId,
                                   job_started_at = $jobStartedAt,
                                   job_receipt_at = COALESCE(job_receipt_at, $now),
                                   updated_at = $now
                               WHERE request_id = $requestId
                                 AND state = 'Dispatched' AND tool_name = 'StartJob'
                                 AND (job_id IS NULL OR job_id = $jobId)
                                 AND (job_started_at IS NULL OR job_started_at = $jobStartedAt);"""
                        addParameter launchReceipt "$requestId" requestId
                        addParameter launchReceipt "$jobId" job.JobId
                        addParameter launchReceipt "$jobStartedAt" launchedAt
                        addParameter launchReceipt "$now" (DateTimeOffset.UtcNow.ToString("O"))
                        if launchReceipt.ExecuteNonQuery() <> 1 then
                            Error "Conflicting StartJob launch receipts for one authenticated request."
                        else
                            transaction.Commit()
                            Error "StartJob handle recorded; detached process-tree termination remains unverified."
                | Some (_, "Dispatched", Some unknown)
                    when not (terminalCommandTypes.Contains unknown) ->
                    // New or unsupported command kinds may spawn work that
                    // outlives their response. Never certify them by default.
                    Error $"Unknown tool classification {unknown}; execution requires independent liveness reconciliation."
                | Some (requestId, "Dispatched", Some _) ->
                    use update = connection.CreateCommand()
                    update.Transaction <- transaction
                    update.CommandText <-
                        """UPDATE agent_execution_witness
                           SET state = 'Terminal', updated_at = $now, terminal_at = $now
                           WHERE request_id = $requestId AND state = 'Dispatched'
                             AND tool_job_id IS NULL;"""
                    addParameter update "$now" (DateTimeOffset.UtcNow.ToString("O"))
                    addParameter update "$requestId" requestId
                    if update.ExecuteNonQuery() <> 1 then
                        Error "Client terminal receipt raced another update."
                    else
                        transaction.Commit()
                        Ok(Some requestId)
                | Some _ -> Error "Receipt is inconsistent with non-dispatched activity."
            with ex -> Error $"Cannot persist authenticated client terminal receipt: {ex.Message}"

    member _.CountUnresolvedAgentExecutions(userId: string, agentId: string) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """SELECT COUNT(*) FROM agent_execution_witness
               WHERE user_id = $userId AND agent_id = $agentId
               AND state NOT IN ('Terminal', 'NotDispatched');"""
        addParameter command "$userId" userId
        addParameter command "$agentId" (agentId.ToUpperInvariant())
        Convert.ToInt32(command.ExecuteScalar())

    /// Bounded, read-only audit projection. It cannot certify quiescence:
    /// HasMore means additional unresolved evidence exists, and even an
    /// empty page cannot replace the verified legacy epoch or child-job gate.
    member _.ListUnresolvedAgentExecutions(
        userId: string, agentId: string, limit: int
    ) : Result<UnresolvedAgentExecutionPage, string> =
        if String.IsNullOrWhiteSpace userId
           || String.IsNullOrWhiteSpace agentId
           || agentId.Length > 256 || limit < 1 || limit > 500 then
            Error "Authenticated user, bounded agent and page size 1..500 are required."
        else
            try
                use connection = openConnection()
                use command = connection.CreateCommand()
                command.CommandText <-
                    """SELECT request_id, tool_name, project_name, state,
                              device_id, connection_id, device_generation,
                              correlation_id, parent_request_id, created_at,
                              job_id, job_started_at, job_receipt_at,
                              tool_job_id, tool_job_started_at, tool_job_receipt_at,
                              tool_job_operation_id
                       FROM agent_execution_witness
                       WHERE user_id = $userId AND agent_id = $agentId
                         AND state NOT IN ('Terminal', 'NotDispatched')
                       ORDER BY created_at ASC, request_id ASC
                       LIMIT $maxRows;"""
                addParameter command "$userId" userId
                addParameter command "$agentId" (agentId.ToUpperInvariant())
                addParameter command "$maxRows" (limit + 1)
                use reader = command.ExecuteReader()
                let results = ResizeArray<UnresolvedAgentExecution>()
                while reader.Read() do
                    results.Add
                        { RequestId = reader.GetString 0
                          ToolName = optionText reader 1
                          ProjectName = optionText reader 2
                          State = reader.GetString 3
                          DeviceId = optionText reader 4
                          ConnectionId = optionText reader 5
                          DeviceGeneration =
                              if reader.IsDBNull 6 then None else Some(reader.GetInt64 6)
                          CorrelationId = optionText reader 7
                          ParentRequestId = optionText reader 8
                          CreatedAt = DateTimeOffset.Parse(reader.GetString 9)
                          JobId = optionText reader 10
                          JobStartedAt =
                              optionText reader 11 |> Option.map DateTimeOffset.Parse
                          JobReceiptAt =
                              optionText reader 12 |> Option.map DateTimeOffset.Parse
                          DeferredToolJobId = optionText reader 13
                          DeferredToolJobOperationId = optionText reader 16
                          DeferredToolJobStartedAt =
                              optionText reader 14 |> Option.map DateTimeOffset.Parse
                          DeferredToolJobReceiptAt =
                              optionText reader 15 |> Option.map DateTimeOffset.Parse }
                Ok { Executions = results |> Seq.truncate limit |> Seq.toList
                     HasMore = results.Count > limit }
            with ex -> Error $"Cannot inspect unresolved agent execution evidence: {ex.Message}"

    member _.CompleteOperation(operationId: string, success: bool, errorCategory: string option, durationMs: int64) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
UPDATE operations
SET completed_at = $completedAt,
    success = $success,
    error_category = $errorCategory,
    duration_ms = $durationMs
WHERE id = $id;
"""
        addParameter command "$completedAt" (DateTimeOffset.UtcNow.ToString("O"))
        addParameter command "$success" (if success then 1 else 0)
        addParameter command "$errorCategory" (dbValue errorCategory)
        addParameter command "$durationMs" durationMs
        addParameter command "$id" operationId
        command.ExecuteNonQuery() |> ignore

    member private _.OperationBelongsToUser(userId: string, operationId: string) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT 1 FROM operations WHERE id = $id AND user_id = $userId LIMIT 1;"
        addParameter command "$id" operationId
        addParameter command "$userId" userId
        not (isNull (command.ExecuteScalar()))

    member private _.FindRecentOperation(
        userId: string,
        toolName: string option,
        projectName: string option,
        now: DateTimeOffset
    ) =
        use connection = openConnection()
        use command = connection.CreateCommand()

        let clauses = ResizeArray<string>()
        clauses.Add("user_id = $userId")
        clauses.Add("completed_at IS NOT NULL")
        clauses.Add("created_at >= $since")

        addParameter command "$userId" userId
        addParameter command "$since" (now.AddMinutes(-10.0).ToString("O"))

        match toolName with
        | Some tool ->
            clauses.Add("tool_name = $toolName")
            addParameter command "$toolName" tool
        | None -> ()

        match projectName with
        | Some project ->
            clauses.Add("project_name = $projectName")
            addParameter command "$projectName" project
        | None -> ()

        let where = String.Join(" AND ", clauses)
        command.CommandText <-
            $"SELECT id FROM operations WHERE {where} ORDER BY created_at DESC LIMIT 1;"

        match command.ExecuteScalar() with
        | null -> None
        | value -> Some(string value)

    member this.ResolveOperation(
        userId: string,
        explicitOperationId: string option,
        toolName: string option,
        projectName: string option
    ) =
        match explicitOperationId with
        | Some operationId ->
            if this.OperationBelongsToUser(userId, operationId) then
                Ok(Some operationId)
            else
                Error "operationId does not identify an operation belonging to the authenticated user."
        | None ->
            Ok(this.FindRecentOperation(userId, toolName, projectName, DateTimeOffset.UtcNow))

    member _.AddFeedback(userId: string, input: FeedbackInput, linkedOperationId: string option) =
        let createdAt = DateTimeOffset.UtcNow
        let feedbackId = Guid.NewGuid().ToString("N")

        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """
INSERT INTO feedback (
    id, created_at, user_id, project_name, tool_name,
    category, severity, summary, details, workaround, operation_id
) VALUES (
    $id, $createdAt, $userId, $projectName, $toolName,
    $category, $severity, $summary, $details, $workaround, $operationId
);
"""
        addParameter command "$id" feedbackId
        addParameter command "$createdAt" (createdAt.ToString("O"))
        addParameter command "$userId" userId
        addParameter command "$projectName" (dbValue input.ProjectName)
        addParameter command "$toolName" (dbValue input.ToolName)
        addParameter command "$category" input.Category
        addParameter command "$severity" input.Severity
        addParameter command "$summary" input.Summary
        addParameter command "$details" (dbValue input.Details)
        addParameter command "$workaround" (dbValue input.Workaround)
        addParameter command "$operationId" (dbValue linkedOperationId)
        command.ExecuteNonQuery() |> ignore

        { Id = feedbackId
          CreatedAt = createdAt
          OperationId = linkedOperationId |> textOrEmpty }

    member _.ListFeedback(
        userId: string,
        projectName: string option,
        toolName: string option,
        category: string option,
        severity: string option,
        limit: int
    ) =
        use connection = openConnection()
        use command = connection.CreateCommand()

        let clauses = ResizeArray<string>()
        clauses.Add("user_id = $userId")
        addParameter command "$userId" userId

        let addOptional field parameter value =
            match value with
            | Some item ->
                clauses.Add($"{field} = {parameter}")
                addParameter command parameter item
            | None -> ()

        addOptional "project_name" "$projectName" projectName
        addOptional "tool_name" "$toolName" toolName
        addOptional "category" "$category" category
        addOptional "severity" "$severity" severity
        addParameter command "$limit" limit

        let where = String.Join(" AND ", clauses)
        command.CommandText <-
            $"""
SELECT id, created_at, project_name, tool_name, category, severity,
       summary, details, workaround, operation_id,
       status, resolved_at, resolution_commit, resolution_notes
FROM feedback
WHERE {where}
ORDER BY created_at DESC
LIMIT $limit;
"""

        use reader = command.ExecuteReader()
        let results = ResizeArray<FeedbackEntry>()

        while reader.Read() do
            results.Add(
                { Id = reader.GetString 0
                  CreatedAt = DateTimeOffset.Parse(reader.GetString 1)
                  ProjectName = optionText reader 2 |> textOrEmpty
                  ToolName = optionText reader 3 |> textOrEmpty
                  Category = reader.GetString 4
                  Severity = reader.GetString 5
                  Summary = reader.GetString 6
                  Details = optionText reader 7 |> textOrEmpty
                  Workaround = optionText reader 8 |> textOrEmpty
                  OperationId = optionText reader 9 |> textOrEmpty
                  Status = reader.GetString 10
                  ResolvedAt = if reader.IsDBNull 11 then Nullable() else Nullable(DateTimeOffset.Parse(reader.GetString 11))
                  ResolutionCommit = optionText reader 12 |> textOrEmpty
                  ResolutionNotes = optionText reader 13 |> textOrEmpty }
            )

        results.ToArray()

    member _.GetFeedback(userId: string, feedbackId: string) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """SELECT id, created_at, project_name, tool_name, category, severity,
                      summary, details, workaround, operation_id, status,
                      resolved_at, resolution_commit, resolution_notes
               FROM feedback WHERE user_id = $userId AND id = $id LIMIT 1;"""
        addParameter command "$userId" userId
        addParameter command "$id" feedbackId
        use reader = command.ExecuteReader()
        if reader.Read() then
            Some
                { Id = reader.GetString 0
                  CreatedAt = DateTimeOffset.Parse(reader.GetString 1)
                  ProjectName = optionText reader 2 |> textOrEmpty
                  ToolName = optionText reader 3 |> textOrEmpty
                  Category = reader.GetString 4
                  Severity = reader.GetString 5
                  Summary = reader.GetString 6
                  Details = optionText reader 7 |> textOrEmpty
                  Workaround = optionText reader 8 |> textOrEmpty
                  OperationId = optionText reader 9 |> textOrEmpty
                  Status = reader.GetString 10
                  ResolvedAt = if reader.IsDBNull 11 then Nullable() else Nullable(DateTimeOffset.Parse(reader.GetString 11))
                  ResolutionCommit = optionText reader 12 |> textOrEmpty
                  ResolutionNotes = optionText reader 13 |> textOrEmpty }
        else None

    member this.UpdateFeedback(userId: string, feedbackId: string, status: string, commit: string option, notes: string option) =
        use connection = openConnection()
        use command = connection.CreateCommand()
        command.CommandText <-
            """UPDATE feedback SET status = $status,
                   resolved_at = $resolvedAt, resolution_commit = $commit, resolution_notes = $notes
               WHERE id = $id AND user_id = $userId;"""
        addParameter command "$status" status
        addParameter command "$resolvedAt" (if FeedbackValidation.isClosed status then box (DateTimeOffset.UtcNow.ToString("O")) else box DBNull.Value)
        addParameter command "$commit" (if FeedbackValidation.isClosed status then dbValue commit else box DBNull.Value)
        addParameter command "$notes" (if FeedbackValidation.isClosed status then dbValue notes else box DBNull.Value)
        addParameter command "$id" feedbackId
        addParameter command "$userId" userId
        if command.ExecuteNonQuery() = 0 then None
        else this.GetFeedback(userId, feedbackId)

    member _.GetSummary(userId: string, projectName: string option, toolName: string option) =
        use connection = openConnection()

        let baseWhere (command: SqliteCommand) =
            let clauses = ResizeArray<string>()
            clauses.Add("f.user_id = $userId")
            addParameter command "$userId" userId

            match projectName with
            | Some project ->
                clauses.Add("f.project_name = $projectName")
                addParameter command "$projectName" project
            | None -> ()

            match toolName with
            | Some tool ->
                clauses.Add("f.tool_name = $toolName")
                addParameter command "$toolName" tool
            | None -> ()

            String.Join(" AND ", clauses)

        let scalarInt (sql: string) =
            use command = connection.CreateCommand()
            let where = baseWhere command
            command.CommandText <- sql.Replace("$WHERE", where)
            Convert.ToInt32(command.ExecuteScalar())

        let grouped field =
            use command = connection.CreateCommand()
            let where = baseWhere command
            command.CommandText <-
                $"""
SELECT COALESCE({field}, '(none)'), COUNT(*)
FROM feedback f
WHERE {where}
GROUP BY {field}
ORDER BY COUNT(*) DESC, COALESCE({field}, '(none)');
"""
            use reader = command.ExecuteReader()
            let values = ResizeArray<FeedbackCount>()

            while reader.Read() do
                values.Add({ Name = reader.GetString 0; Count = reader.GetInt32 1 })

            values.ToArray()

        let total = scalarInt "SELECT COUNT(*) FROM feedback f WHERE $WHERE;"
        let withWorkaround =
            scalarInt
                "SELECT COUNT(*) FROM feedback f WHERE $WHERE AND workaround IS NOT NULL AND TRIM(workaround) <> '';"
        let linkedToFailedOperation =
            scalarInt
                """
SELECT COUNT(*)
FROM feedback f
JOIN operations o ON o.id = f.operation_id
WHERE $WHERE AND o.success = 0;
"""

        { Total = total
          WithWorkaround = withWorkaround
          LinkedToFailedOperation = linkedToFailedOperation
          ByTool = grouped "tool_name"
          ByCategory = grouped "category"
          BySeverity = grouped "severity" }
