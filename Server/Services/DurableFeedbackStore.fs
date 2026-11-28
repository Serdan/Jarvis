namespace Server.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Only server-observed, authenticated operation metadata may supply this evidence.
/// The caller's operation ID is not itself evidence.
type FeedbackOperationEvidence =
    { UserId: string
      OperationId: string
      Success: bool option }

[<CLIMutable>]
type DurableFeedbackEntry =
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
      RequestedOperationId: string
      OperationLinkState: string
      Status: string
      ResolvedAt: Nullable<DateTimeOffset>
      ResolutionCommit: string
      ResolutionNotes: string }

[<CLIMutable>]
type DurableFeedbackCreated =
    { Id: string
      CreatedAt: DateTimeOffset
      OperationId: string
      RequestedOperationId: string
      OperationLinkState: string }

module private DurableFeedbackSql =
    let applicationId = 0x4A464442L // JFDB, not the diagnostic or Work database
    let schemaVersion = 1L
    let value = function Some x -> box x | None -> box DBNull.Value
    let text (r: SqliteDataReader) i = if r.IsDBNull i then "" else r.GetString i
    let nullableDate (r: SqliteDataReader) i =
        if r.IsDBNull i then Nullable() else Nullable(DateTimeOffset.Parse(r.GetString i))

    let command (db: SqliteConnection) (tx: SqliteTransaction option) sql parameters =
        let cmd = db.CreateCommand()
        try
            cmd.CommandText <- sql
            tx |> Option.iter (fun transaction -> cmd.Transaction <- transaction)
            for name, argument in parameters do
                cmd.Parameters.AddWithValue(name, argument) |> ignore
            cmd
        with _ ->
            cmd.Dispose()
            reraise()

    let execute db tx sql parameters =
        use cmd = command db tx sql parameters
        cmd.ExecuteNonQuery() |> ignore

    let scalar db tx sql parameters =
        use cmd = command db tx sql parameters
        cmd.ExecuteScalar()

    let number db tx sql parameters = Convert.ToInt64(scalar db tx sql parameters)

    let openDatabase path readOnly =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- if readOnly then SqliteOpenMode.ReadOnly else SqliteOpenMode.ReadWrite
        builder.Cache <- SqliteCacheMode.Private
        builder.Pooling <- false
        builder.DefaultTimeout <- 1
        let db = new SqliteConnection(builder.ToString())
        try
            db.Open()
            db
        with _ ->
            db.Dispose()
            reraise()

    let checkSchema db =
        if number db None "PRAGMA application_id;" [] <> applicationId ||
           number db None "PRAGMA user_version;" [] <> schemaVersion then
            invalidOp "FeedbackSchemaUnavailable: an explicitly initialized or migrated feedback database is required."

    let boundSql (db: SqliteConnection) =
        let started = Stopwatch.GetTimestamp()
        SQLitePCL.raw.sqlite3_progress_handler(
            db.Handle, 1000,
            SQLitePCL.delegate_progress(fun _ ->
                if Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds 2. then 1 else 0), null)

    let setWritePolicy db maxDatabaseBytes =
        execute db None "PRAGMA synchronous=FULL;" []
        let pageSize = number db None "PRAGMA page_size;" []
        number db None $"PRAGMA max_page_count = {maxDatabaseBytes / pageSize};" [] |> ignore
        number db None "PRAGMA wal_autocheckpoint=64;" [] |> ignore
        number db None "PRAGMA journal_size_limit=1048576;" [] |> ignore

    let footprint path =
        [| path; path + "-wal"; path + "-shm"; path + "-journal" |]
        |> Array.sumBy (fun file -> if File.Exists file then FileInfo(file).Length else 0L)

    let schema = """
CREATE TABLE feedback (
    id TEXT NOT NULL, user_id TEXT NOT NULL, created_at TEXT NOT NULL,
    project_name TEXT NULL, tool_name TEXT NULL, category TEXT NOT NULL,
    severity TEXT NOT NULL, summary TEXT NOT NULL, details TEXT NULL,
    workaround TEXT NULL, operation_id TEXT NULL,
    requested_operation_id TEXT NULL, operation_success INTEGER NULL,
    status TEXT NOT NULL DEFAULT 'Open', resolved_at TEXT NULL,
    resolution_commit TEXT NULL, resolution_notes TEXT NULL,
    PRIMARY KEY (user_id,id)
);
CREATE INDEX ix_feedback_user_created ON feedback(user_id,created_at DESC,id);
CREATE TABLE feedback_receipts (
    user_id TEXT NOT NULL, request_id TEXT NOT NULL, request_hash TEXT NOT NULL,
    result_json TEXT NOT NULL, PRIMARY KEY(user_id,request_id)
);
"""

    let entryColumns =
        "id, created_at, project_name, tool_name, category, severity, summary, details, workaround, operation_id, requested_operation_id, status, resolved_at, resolution_commit, resolution_notes"

    let linkState linked requested =
        if not (String.IsNullOrEmpty linked) then "Verified"
        elif not (String.IsNullOrEmpty requested) then "Unverified"
        else "None"

    let entry (r: SqliteDataReader) : DurableFeedbackEntry =
        let linked, requested = text r 9, text r 10
        { Id = r.GetString 0; CreatedAt = DateTimeOffset.Parse(r.GetString 1)
          ProjectName = text r 2; ToolName = text r 3; Category = r.GetString 4
          Severity = r.GetString 5; Summary = r.GetString 6; Details = text r 7
          Workaround = text r 8; OperationId = linked; RequestedOperationId = requested
          OperationLinkState = linkState linked requested; Status = r.GetString 11
          ResolvedAt = nullableDate r 12; ResolutionCommit = text r 13; ResolutionNotes = text r 14 }

    let getEntry db tx user id =
        use cmd = command db tx $"SELECT {entryColumns} FROM feedback WHERE user_id=$user AND id=$id;"
                      [ "$user", box user; "$id", box id ]
        use r = cmd.ExecuteReader()
        if r.Read() then Some(entry r) else None

    let requestHash request =
        JsonSerializer.SerializeToUtf8Bytes(request)
        |> SHA256.HashData
        |> Convert.ToHexString

    let readReceipt<'T> db tx user (requestId: Guid) hash =
        use cmd = command db tx
                      "SELECT request_hash,result_json FROM feedback_receipts WHERE user_id=$user AND request_id=$request;"
                      [ "$user", box user; "$request", box (requestId.ToString("N")) ]
        use r = cmd.ExecuteReader()
        if not (r.Read()) then None
        elif r.GetString(0) <> hash then
            Some(Error "FeedbackRequestConflict: request ID has already been used with different inputs.")
        else Some(Ok(JsonSerializer.Deserialize<'T>(r.GetString 1)))

    let receipt db tx user (requestId: Guid) hash result =
        execute db tx
            "INSERT INTO feedback_receipts(user_id,request_id,request_hash,result_json) VALUES($user,$request,$hash,$result);"
            [ "$user", box user; "$request", box (requestId.ToString("N"))
              "$hash", box hash; "$result", box (JsonSerializer.Serialize(result)) ]

    let fields (db: SqliteConnection) tx table =
        use cmd = command db tx $"PRAGMA table_info({table});" []
        use reader = cmd.ExecuteReader()
        let names = HashSet<string>(StringComparer.Ordinal)
        while reader.Read() do names.Add(reader.GetString 1) |> ignore
        names

    // Offline only. Keep the legacy database read-only and copy a single SQLite
    // snapshot; never treat an unverified/missing operation as a verified link.
    let importLegacy (source: string) destination tx =
        use old = openDatabase source true
        use snapshot = old.BeginTransaction(deferred = true)
        let oldTx = Some snapshot
        let cols = fields old oldTx "feedback"
        for required in ["id";"created_at";"user_id";"project_name";"tool_name";"category";"severity";"summary";"details";"workaround";"operation_id"] do
            if not (cols.Contains required) then invalidOp $"Legacy feedback schema missing {required}."
        let field name fallback = if cols.Contains name then "f." + name else fallback
        let op = fields old oldTx "operations"
        let hasOperations =
            ["id";"user_id";"success"] |> List.forall op.Contains
        let join = if hasOperations then "LEFT JOIN operations o ON o.id=f.operation_id AND o.user_id=f.user_id" else ""
        let verified = if hasOperations then "o.id,o.success" else "NULL,NULL"
        let status = field "status" "'Open'"
        let resolved = field "resolved_at" "NULL"
        let commit = field "resolution_commit" "NULL"
        let notes = field "resolution_notes" "NULL"
        let query =
            $"SELECT f.id,f.user_id,f.created_at,f.project_name,f.tool_name,f.category,f.severity,f.summary,f.details,f.workaround,{verified},f.operation_id,{status},{resolved},{commit},{notes} FROM feedback f {join} ORDER BY f.user_id COLLATE BINARY,f.id COLLATE BINARY;"
        use cmd = command old oldTx query []
        use rows = cmd.ExecuteReader()
        let mutable copied = 0L
        while rows.Read() do
            execute destination tx
                """INSERT INTO feedback(id,user_id,created_at,project_name,tool_name,category,severity,summary,details,workaround,operation_id,operation_success,requested_operation_id,status,resolved_at,resolution_commit,resolution_notes)
                   VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16);"""
                [ for i in 0..16 -> "$" + string i, rows.GetValue i ]
            copied <- copied + 1L
        rows.Close()
        if copied <> number old oldTx "SELECT COUNT(*) FROM feedback;" [] ||
           copied <> number destination tx "SELECT COUNT(*) FROM feedback;" [] then
            invalidOp "Legacy feedback snapshot count mismatch."
        // Independently read the projection back from the target while the same
        // source snapshot is still open. Compare every field and identity in
        // stable tenant/id order; total count alone cannot detect corruption or
        // misattribution. Equality also establishes each tenant's row count.
        use original = command old oldTx query []
        use originals = original.ExecuteReader()
        use imported = command destination tx
                            """SELECT id,user_id,created_at,project_name,tool_name,category,severity,summary,details,workaround,
                                      operation_id,operation_success,requested_operation_id,status,resolved_at,resolution_commit,resolution_notes
                               FROM feedback ORDER BY user_id COLLATE BINARY,id COLLATE BINARY;""" []
        use copies = imported.ExecuteReader()
        let mutable verifiedRows = 0L
        while originals.Read() do
            if not (copies.Read()) ||
               not ([0..16] |> List.forall (fun i -> Object.Equals(originals.GetValue i, copies.GetValue i))) then
                invalidOp "Legacy feedback snapshot identity/content/provenance mismatch."
            verifiedRows <- verifiedRows + 1L
        if copies.Read() || verifiedRows <> copied then
            invalidOp "Legacy feedback verification count mismatch."
        originals.Close()
        copies.Close()
        snapshot.Commit()

    let initialize path maxDatabaseBytes legacy =
        if String.IsNullOrWhiteSpace path then invalidArg "path" "Feedback database path is required."
        let path = Path.GetFullPath path
        if [| path; path + "-wal"; path + "-shm"; path + "-journal" |] |> Array.exists File.Exists then
            invalidOp "Feedback initialization refuses to replace an existing database or sidecar."
        match legacy with
        | Some source when not (File.Exists source) -> invalidArg "legacy" "Legacy database must exist."
        | _ -> ()
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        // Reserve the destination atomically. A failed initialization leaves an
        // unmarked database for inspection, never a silently accepted empty one.
        do
            use _reserved = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            ()
        use db = openDatabase path false
        scalar db None "PRAGMA journal_mode=WAL;" [] |> ignore
        setWritePolicy db maxDatabaseBytes
        use transaction = db.BeginTransaction()
        let tx = Some transaction
        execute db tx schema []
        legacy |> Option.iter (fun source -> importLegacy source db tx)
        if not (Object.Equals(scalar db tx "PRAGMA integrity_check;" [], "ok")) then
            invalidOp "Durable feedback destination failed integrity verification."
        execute db tx $"PRAGMA application_id={applicationId};" []
        execute db tx $"PRAGMA user_version={schemaVersion};" []
        transaction.Commit()

/// Durable feedback storage is a separate transaction and admission domain.
/// Construction is inert. Initialization/import is an explicit offline operation.
/// Runtime uses ReadWrite (never creates an empty replacement) and never prunes.
type DurableFeedbackStore(databasePath: string, ?timeout: TimeSpan, ?maxDiskBytes: int64) =
    let timeout = defaultArg timeout (TimeSpan.FromSeconds 3.)
    let diskBudget = defaultArg maxDiskBytes (128L * 1024L * 1024L)
    let databaseBudget = diskBudget / 2L
    do
        if timeout <= TimeSpan.Zero then invalidArg "timeout" "Must be positive."
        if diskBudget < 256L * 1024L then invalidArg "maxDiskBytes" "Must be at least 256 KiB."
    let writer = new SemaphoreSlim(1)
    let readers = new SemaphoreSlim(2)
    let unavailable = "FeedbackUnavailable: durable feedback storage is unavailable."
    let unknown (requestId: Guid) =
        $"FeedbackOutcomeUnknown: reconcile by repeating the exact request with requestId={requestId:N}; do not generate a new ID."
    let noAdmission requestId =
        match requestId with
        | Some (id: Guid) -> $"FeedbackBusy: no new attempt admitted; reconcile with the same requestId={id:N}."
        | None -> "FeedbackBusy: no read admitted; retry."
    let run (lane: SemaphoreSlim) requestId (action: unit -> Result<'T,string>) =
        task {
            if String.IsNullOrWhiteSpace databasePath then return Error unavailable
            elif not (lane.Wait(0)) then return Error(noAdmission requestId)
            else
                let work () =
                    try
                        try action()
                        with
                        | :? SqliteException as ex ->
                            let detail =
                                if ex.SqliteErrorCode = 13 then " FeedbackQuotaExceeded: SQLite full; preserve records and inspect the durable budget."
                                else $" SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}."
                            match requestId with
                            | Some id -> Error(unknown id + detail)
                            | None -> Error(unavailable + detail)
                        | _ -> Error(requestId |> Option.map unknown |> Option.defaultValue unavailable)
                    finally lane.Release() |> ignore
                let pending =
                    try
                        if ExecutionContext.IsFlowSuppressed() then Task.Run(Func<Result<'T,string>>(work))
                        else
                            use _flow = ExecutionContext.SuppressFlow()
                            Task.Run(Func<Result<'T,string>>(work))
                    with _ ->
                        lane.Release() |> ignore
                        reraise()
                try return! pending.WaitAsync(timeout)
                with :? TimeoutException ->
                    // The lane stays owned until physical completion. A response
                    // timeout does not cancel SQLite or prove non-commit.
                    return Error(requestId |> Option.map unknown |> Option.defaultValue unavailable)
        }
    let useRead operation =
        use db = DurableFeedbackSql.openDatabase databasePath true
        DurableFeedbackSql.boundSql db
        DurableFeedbackSql.checkSchema db
        operation db
    let read operation = run readers None (fun () -> useRead operation |> Ok)
    let mutate user (requestId: Guid) hash (action: SqliteConnection -> SqliteTransaction option -> 'T) =
        if requestId = Guid.Empty then Task.FromResult(Error "FeedbackRequestInvalid: a nonempty request UUID is required.")
        else
            run writer (Some requestId) (fun () ->
                // Replay has independent read admission, even above write quota.
                match useRead (fun db -> DurableFeedbackSql.readReceipt<'T> db None user requestId hash) with
                | Some result -> result
                | None ->
                    use db = DurableFeedbackSql.openDatabase databasePath false
                    DurableFeedbackSql.boundSql db
                    DurableFeedbackSql.checkSchema db
                    DurableFeedbackSql.setWritePolicy db databaseBudget
                    use transaction = db.BeginTransaction()
                    let tx = Some transaction
                    match DurableFeedbackSql.readReceipt<'T> db tx user requestId hash with
                    | Some result -> result
                    | None ->
                        if DurableFeedbackSql.footprint databasePath >= diskBudget ||
                           FileInfo(databasePath).Length >= databaseBudget then
                            Error "FeedbackQuotaExceeded: durable feedback budget reached; preserve records and request operator archival or budget adjustment."
                        else
                            let result = action db tx
                            DurableFeedbackSql.receipt db tx user requestId hash result
                            transaction.Commit()
                            Ok result)

    /// Offline preparation only; requires a new destination. No production caller
    /// should invoke this during startup or while legacy feedback writes continue.
    member _.InitializeOffline(?legacyDatabasePath: string) =
        DurableFeedbackSql.initialize databasePath databaseBudget legacyDatabasePath

    member _.TrySubmit(user: string, requestId: Guid, input: FeedbackInput, evidence: FeedbackOperationEvidence option) =
        let hash =
            DurableFeedbackSql.requestHash
                {| kind = "submit"; project = defaultArg input.ProjectName null
                   tool = defaultArg input.ToolName null; category = input.Category; severity = input.Severity
                   summary = input.Summary; details = defaultArg input.Details null
                   workaround = defaultArg input.Workaround null; requested = defaultArg input.OperationId null |}
        let verified =
            evidence |> Option.filter (fun e ->
                e.UserId = user && not (String.IsNullOrWhiteSpace e.OperationId) &&
                (input.OperationId |> Option.forall ((=) e.OperationId)))
        let linked = verified |> Option.map _.OperationId
        let requested = input.OperationId
        mutate user requestId hash (fun db tx ->
            let id, created = Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow
            let v = DurableFeedbackSql.value
            DurableFeedbackSql.execute db tx
                """INSERT INTO feedback(id,user_id,created_at,project_name,tool_name,category,severity,summary,details,workaround,operation_id,requested_operation_id,operation_success)
                   VALUES($id,$user,$created,$project,$tool,$category,$severity,$summary,$details,$workaround,$linked,$requested,$success);"""
                [ "$id", box id; "$user", box user; "$created", box (created.ToString("O"))
                  "$project", v input.ProjectName; "$tool", v input.ToolName; "$category", box input.Category
                  "$severity", box input.Severity; "$summary", box input.Summary; "$details", v input.Details
                  "$workaround", v input.Workaround; "$linked", v linked; "$requested", v requested
                  "$success", v (verified |> Option.bind _.Success |> Option.map (fun b -> if b then 1 else 0)) ]
            let linked, requested = defaultArg linked "", defaultArg requested ""
            { Id = id; CreatedAt = created; OperationId = linked; RequestedOperationId = requested
              OperationLinkState = DurableFeedbackSql.linkState linked requested })

    member _.TryUpdate(user: string, requestId: Guid, id: string, status: string, commit: string option, notes: string option) =
        let hash =
            DurableFeedbackSql.requestHash
                {| kind = "update"; id = id; status = status; commit = defaultArg commit null; notes = defaultArg notes null |}
        mutate user requestId hash (fun db tx ->
            let closed = FeedbackValidation.isClosed status
            let v = DurableFeedbackSql.value
            DurableFeedbackSql.execute db tx
                """UPDATE feedback SET status=$status,resolved_at=$resolved,resolution_commit=$commit,resolution_notes=$notes
                   WHERE user_id=$user AND id=$id;"""
                [ "$status", box status
                  "$resolved", if closed then box (DateTimeOffset.UtcNow.ToString("O")) else box DBNull.Value
                  "$commit", if closed then v commit else box DBNull.Value
                  "$notes", if closed then v notes else box DBNull.Value
                  "$user", box user; "$id", box id ]
            // A null receipt is a stable not-found result, scoped to this user.
            DurableFeedbackSql.getEntry db tx user id |> Option.defaultValue Unchecked.defaultof<DurableFeedbackEntry>)
        |> fun result -> task {
            let! outcome = result
            return outcome |> Result.map (fun entry -> if isNull (box entry) then None else Some entry)
        }

    member _.TryGet(user: string, id: string) =
        read (fun db -> DurableFeedbackSql.getEntry db None user id)

    member _.TryList(user: string, project: string option, tool: string option, category: string option, severity: string option, limit: int) =
        if limit < 1 || limit > 100 then Task.FromResult(Error "FeedbackLimitInvalid: limit must be between 1 and 100.")
        else
            read (fun db ->
                let filters = [ "project_name", project; "tool_name", tool; "category", category; "severity", severity ]
                let active = filters |> List.choose (fun (name,value) -> value |> Option.map (fun v -> name,v))
                let where = active |> List.map (fun (name,_) -> $" AND {name}=${name}") |> String.concat ""
                let parameters = [ "$user", box user; "$limit", box limit ] @ (active |> List.map (fun (name,v) -> "$"+name,box v))
                use cmd = DurableFeedbackSql.command db None
                              $"SELECT {DurableFeedbackSql.entryColumns} FROM feedback WHERE user_id=$user{where} ORDER BY created_at DESC,id LIMIT $limit;" parameters
                use r = cmd.ExecuteReader()
                [| while r.Read() do yield DurableFeedbackSql.entry r |])

    member _.TrySummary(user: string, project: string option, tool: string option) =
        read (fun db ->
            use transaction = db.BeginTransaction(deferred = true)
            let tx = Some transaction
            let filters = [ "project_name", project; "tool_name", tool ]
            let active = filters |> List.choose (fun (name,value) -> value |> Option.map (fun v -> name,v))
            let where = "user_id=$user" + (active |> List.map (fun (name,_) -> $" AND {name}=${name}") |> String.concat "")
            let parameters = [ "$user", box user ] @ (active |> List.map (fun (name,v) -> "$"+name,box v))
            let count clause =
                DurableFeedbackSql.number db tx $"SELECT COUNT(*) FROM feedback WHERE {where}{clause};" parameters |> int
            let group field =
                use cmd = DurableFeedbackSql.command db tx
                              $"SELECT COALESCE({field},'(none)'),COUNT(*) FROM feedback WHERE {where} GROUP BY {field} ORDER BY COUNT(*) DESC,COALESCE({field},'(none)');" parameters
                use r = cmd.ExecuteReader()
                [| while r.Read() do yield { Name = r.GetString 0; Count = r.GetInt32 1 } |]
            let result =
                { Total = count ""; WithWorkaround = count " AND workaround IS NOT NULL AND TRIM(workaround)<>''"
                  LinkedToFailedOperation = count " AND operation_id IS NOT NULL AND operation_success=0"
                  ByTool = group "tool_name"; ByCategory = group "category"; BySeverity = group "severity" }
            transaction.Commit()
            result)

/// Read-only observation of optional diagnostics; invoke exclusively inside
/// OptionalFeedbackStore.TryRead so its physical single-flight limit applies.
module FeedbackOperationEvidence =
    let read databasePath user explicitId tool project =
        use db = DurableFeedbackSql.openDatabase databasePath true
        DurableFeedbackSql.boundSql db
        let sql, parameters =
            match explicitId with
            | Some id ->
                "SELECT id,success FROM operations WHERE user_id=$user AND id=$id LIMIT 1;",
                [ "$user", box user; "$id", box id ]
            | None ->
                let filters = [ "tool_name",tool; "project_name",project ]
                let active = filters |> List.choose (fun (name,value) -> value |> Option.map (fun v -> name,v))
                let where = active |> List.map (fun (name,_) -> $" AND {name}=${name}") |> String.concat ""
                let args = active |> List.map (fun (name,v) -> "$"+name,box v)
                $"SELECT id,success FROM operations WHERE user_id=$user AND completed_at IS NOT NULL AND created_at >= $since{where} ORDER BY created_at DESC,id LIMIT 1;",
                [ "$user", box user; "$since", box (DateTimeOffset.UtcNow.AddMinutes(-10.).ToString("O")) ] @ args
        use cmd = DurableFeedbackSql.command db None sql parameters
        use r = cmd.ExecuteReader()
        if r.Read() then
            Some { UserId = user; OperationId = r.GetString 0
                   Success = if r.IsDBNull 1 then None else Some(r.GetInt64(1) <> 0L) }
        else None

/// Exactly one feedback authority is selected at service startup.
[<RequireQualifiedAccess>]
type FeedbackStorageMode =
    | Legacy
    | Durable of DurableFeedbackStore
