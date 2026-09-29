namespace Server.Services

open System
open System.Collections.Generic
open System.IO
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
      OperationId: string }

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

type FeedbackStore(databasePath: string) =
    let connectionString =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- databasePath
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Cache <- SqliteCacheMode.Shared
        builder.ToString()

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
"""
        schema.ExecuteNonQuery() |> ignore

    do initialize()

    member _.DatabasePath = databasePath

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
       summary, details, workaround, operation_id
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
                  OperationId = optionText reader 9 |> textOrEmpty }
            )

        results.ToArray()

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
