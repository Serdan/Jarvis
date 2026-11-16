module Client.OriginalBrowserJournal

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite
open Jarvis.WorkQueues

// Client-owned WQ03 original browser evidence. No live browser provider or
// scheduler is wired here. The host must explicitly provision this directory;
// a missing or damaged journal never authorizes a replacement send.
type OriginalBrowserJournalStore =
    private
        { Root: string
          Database: string
          Anchor: string
          JournalId: string }

// Match the existing source-effect journal's durable directory enrollment:
// a successfully flushed new file is not sufficient if its directory entry
// can disappear after a host power loss.
module private BrowserJournalDirectory =
    [<DllImport("libc", EntryPoint="open", SetLastError=true)>]
    extern int private openDirectory(string path, int flags)

    [<DllImport("libc", EntryPoint="fsync", SetLastError=true)>]
    extern int private syncDescriptor(int fd)

    [<DllImport("libc", EntryPoint="close", SetLastError=true)>]
    extern int private closeDescriptor(int fd)

    let sync path =
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            let fd = openDirectory(path, 0)
            if fd < 0 then
                raise (IOException("Cannot open original browser journal directory for fsync."))
            try
                if syncDescriptor(fd) <> 0 then
                    raise (IOException("Cannot fsync original browser journal directory."))
            finally
                closeDescriptor(fd) |> ignore
        elif not (OperatingSystem.IsWindows()) then
            raise (PlatformNotSupportedException("Unknown original journal durability platform."))

module OriginalBrowserJournal =
    let private dbName = "original-browser-v1.sqlite"
    let private anchorName = "original-browser-v1.anchor"

    let private sha (s: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes s)
        |> Convert.ToHexString
        |> fun h -> h.ToLowerInvariant()

    let private bounded (s: string) =
        not (String.IsNullOrWhiteSpace s) && s.Length <= 2048 && not (s.Contains '\000')

    // The inert WQ08 prototype currently uses wq08:dispatch:<sha256> strings,
    // not Guid strings. Preserve that *exact* original ID in the row, while
    // deriving its stable UUID key without creating a new send identity.
    let private stableUuid originalId =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes("wq03-browser-original-v1|" + originalId))
        let key = bytes.[0..15]
        key.[7] <- (key.[7] &&& 0x0fuy) ||| 0x50uy
        key.[8] <- (key.[8] &&& 0x3fuy) ||| 0x80uy
        Guid(key).ToString("D")

    let operationUuid originalId = stableUuid originalId

    // Length-prefixing avoids ambiguities when IDs contain delimiters.
    let private contentHash (intent: OriginalBrowserIntent) =
        let field (s: string) = string s.Length + ":" + s
        let optional = function None -> "absent" | Some value -> "present:" + value
        [ "wq03-original-intent-v1"; intent.OperationId; intent.LaunchId
          intent.ProjectId; intent.WorkId; string intent.WorkGeneration
          intent.RoleId; string intent.RoleVersion; intent.RequestFingerprint
          intent.Provenance.AccountHead; intent.Provenance.ProjectHead
          intent.Provenance.SourceRef |> optional
          intent.Provenance.SourceCommit |> optional ]
        |> List.map field
        |> String.concat ""
        |> sha

    let originalIntentSha256 intent = contentHash intent

    let private validate (intent: OriginalBrowserIntent) =
        not (isNull (box intent))
        && not (isNull (box intent.Provenance))
        && [ intent.OperationId; intent.LaunchId; intent.ProjectId
             intent.WorkId; intent.RoleId; intent.RequestFingerprint
             intent.Provenance.AccountHead; intent.Provenance.ProjectHead ]
           |> List.forall bounded
        && intent.WorkGeneration > 0 && intent.RoleVersion > 0
        && (intent.Provenance.SourceRef |> Option.forall bounded)
        && (intent.Provenance.SourceCommit |> Option.forall bounded)

    let private locations root =
        if String.IsNullOrWhiteSpace root || not (Path.IsPathFullyQualified root) then
            invalidArg "root" "An explicitly provisioned absolute local browser journal root is required."
        let root = Path.GetFullPath root
        if not (Directory.Exists root) || DirectoryInfo(root).LinkTarget <> null then
            invalidOp "Original browser journal root absent or linked."
        let db = Path.Combine(root, dbName)
        let anchor = Path.Combine(root, anchorName)
        if FileInfo(db).LinkTarget <> null || FileInfo(anchor).LinkTarget <> null then
            invalidOp "Original browser journal file is linked."
        root, db, anchor

    let private cmd (connection: SqliteConnection) (tx: SqliteTransaction option) sql =
        let c = connection.CreateCommand()
        c.CommandText <- sql
        tx |> Option.iter (fun transaction -> c.Transaction <- transaction)
        c

    let private add (c: SqliteCommand) name (value: obj) =
        c.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
        |> ignore

    let private protect (work: unit -> 'a) : Result<'a,string> =
        try Ok(work())
        with ex -> Error("RecoveryBlocked: original browser journal: " + ex.Message)

    // File is made durable before the database is created, so incomplete first
    // enrollment cannot silently become a fresh empty one-shot namespace.
    let provisionTrustedHostRoot root : Result<unit,string> =
        protect (fun () ->
            let root, db, anchor = locations root
            if File.Exists anchor || File.Exists db then
                invalidOp "Original journal enrollment already exists or is incomplete."
            let journalId = Guid.NewGuid().ToString("D")
            use marker = new FileStream(anchor, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            let bytes = Encoding.UTF8.GetBytes("original-browser-v1\n" + journalId + "\n" + root + "\n")
            marker.Write(bytes, 0, bytes.Length)
            marker.Flush(true)
            marker.Close()
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(anchor, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            BrowserJournalDirectory.sync root
            let connectionString = SqliteConnectionStringBuilder()
            connectionString.DataSource <- db
            connectionString.Mode <- SqliteOpenMode.ReadWriteCreate
            connectionString.Pooling <- false
            use connection = new SqliteConnection(connectionString.ToString())
            connection.Open()
            use configure = cmd connection None "PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA;"
            configure.ExecuteNonQuery() |> ignore
            use create = cmd connection None ("CREATE TABLE journal_identity (id TEXT PRIMARY KEY CHECK(id='v1'), journal_uuid TEXT NOT NULL, root_path TEXT NOT NULL);" +
                 "CREATE TABLE originals (" +
                 "operation_uuid TEXT PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, launch_id TEXT NOT NULL UNIQUE," +
                 "project_id TEXT NOT NULL, work_id TEXT NOT NULL, work_generation INTEGER NOT NULL," +
                 "role_id TEXT NOT NULL, role_version INTEGER NOT NULL, request_fingerprint TEXT NOT NULL," +
                 "account_head TEXT NOT NULL, project_head TEXT NOT NULL, source_ref TEXT, source_commit TEXT," +
                 "intent_sha TEXT NOT NULL, outcome_kind TEXT, outcome_detail TEXT," +
                 "CHECK ((outcome_kind IS NULL AND outcome_detail IS NULL) OR" +
                 " (outcome_kind IN ('Accepted','DefinitiveNoEffect','Unknown') AND outcome_detail IS NOT NULL)));" +
                 "INSERT INTO journal_identity(id,journal_uuid,root_path) VALUES('v1',$id,$root);")
            add create "$id" (box journalId)
            add create "$root" (box root)
            create.ExecuteNonQuery() |> ignore
            connection.Close()
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                File.SetUnixFileMode(db, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            BrowserJournalDirectory.sync root)

    let private continuity (store: OriginalBrowserJournalStore) =
        let root, db, anchor = locations store.Root
        if root <> store.Root || db <> store.Database || anchor <> store.Anchor
           || not (File.Exists db) || not (File.Exists anchor) then
            invalidOp "Original browser journal or continuity anchor missing."
        let lines = File.ReadAllLines anchor
        if lines.Length < 3 || lines.[0] <> "original-browser-v1"
           || lines.[1] <> store.JournalId || lines.[2] <> store.Root then
            invalidOp "Original browser journal continuity identity changed."

    let private connect readOnly store =
        continuity store
        let cs = SqliteConnectionStringBuilder()
        cs.DataSource <- store.Database
        cs.Mode <- if readOnly then SqliteOpenMode.ReadOnly else SqliteOpenMode.ReadWrite
        cs.Pooling <- false
        let connection = new SqliteConnection(cs.ToString())
        connection.Open()
        try
            use pragmas = cmd connection None (if readOnly then "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;"
                 else "PRAGMA busy_timeout=5000; PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA;")
            pragmas.ExecuteNonQuery() |> ignore
            use identity = cmd connection None "SELECT journal_uuid,root_path FROM journal_identity WHERE id='v1';"
            use reader = identity.ExecuteReader()
            if not (reader.Read()) || reader.GetString(0) <> store.JournalId
               || reader.GetString(1) <> store.Root || reader.Read() then
                invalidOp "Original browser SQLite journal identity diverged from anchor."
            reader.Close()
            connection
        with ex ->
            connection.Dispose()
            raise ex

    // Independent, fsynced UUID inventory catches a removed/rolled-back SQLite
    // row. The anchor marker precedes SQL COMMIT; a torn marker or failed
    // commit blocks recovery rather than ever creating a second original.
    let private verifyInventory store (connection: SqliteConnection) tx =
        let lines = File.ReadAllLines store.Anchor
        if lines.Length < 3 then invalidOp "Original browser continuity anchor truncated."
        let expected =
            lines.[3..]
            |> Array.map (fun line ->
                let parts = line.Split('|')
                if parts.Length <> 3 || parts.[0] <> "op"
                   || parts.[1].Length <> 36 || parts.[2].Length <> 64 then
                    invalidOp "Corrupt original UUID inventory marker."
                let valid, id = Guid.TryParse parts.[1]
                if not valid || id = Guid.Empty then
                    invalidOp "Corrupt original UUID inventory key."
                parts.[1], parts.[2])
        use rows = cmd connection tx "SELECT operation_uuid,intent_sha FROM originals;"
        use reader = rows.ExecuteReader()
        let actual = ResizeArray<string * string>()
        while reader.Read() do actual.Add(reader.GetString 0, reader.GetString 1)
        if (expected |> Array.distinct |> Array.length) <> expected.Length
           || (expected |> Set.ofArray) <> (actual |> Set.ofSeq) then
            invalidOp "Original UUID continuity inventory diverged from SQLite rows."

    let private anchorOriginal store intent =
        let marker = "op|" + stableUuid intent.OperationId + "|" + contentHash intent + "\n"
        use stream = new FileStream(store.Anchor, FileMode.Append, FileAccess.Write, FileShare.Read)
        let bytes = Encoding.UTF8.GetBytes marker
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)

    // Read-only, open-existing by design; no read can initialize or reset state.
    let openExisting root : Result<OriginalBrowserJournalStore,string> =
        protect (fun () ->
            let root, db, anchor = locations root
            if not (File.Exists anchor) || not (File.Exists db) then
                invalidOp "Previously provisioned original browser journal missing."
            let lines = File.ReadAllLines anchor
            if lines.Length < 3 || lines.[0] <> "original-browser-v1" || lines.[2] <> root then
                invalidOp "Original browser journal continuity anchor invalid."
            let parsed, guid = Guid.TryParse lines.[1]
            if not parsed || guid = Guid.Empty then
                invalidOp "Original browser journal UUID corrupt."
            let store = { Root = root; Database = db; Anchor = anchor; JournalId = lines.[1] }
            use connection = connect true store
            use schema = cmd connection None "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('journal_identity','originals');"
            if Convert.ToInt32(schema.ExecuteScalar()) <> 2 then
                invalidOp "Original browser journal schema missing."
            use integrity = cmd connection None "PRAGMA integrity_check;"
            if Convert.ToString(integrity.ExecuteScalar()) <> "ok" then
                invalidOp "Original browser journal integrity check failed."
            verifyInventory store connection None
            store)

    let private selectColumns =
        "operation_uuid,operation_id,launch_id,project_id,work_id,work_generation," +
        "role_id,role_version,request_fingerprint,account_head,project_head," +
        "source_ref,source_commit,intent_sha,outcome_kind,outcome_detail"

    let private fromReader (r: SqliteDataReader) =
        let opt i = if r.IsDBNull i then None else Some(r.GetString i)
        let intent =
            { OperationId = r.GetString 1
              LaunchId = r.GetString 2
              ProjectId = r.GetString 3
              WorkId = r.GetString 4
              WorkGeneration = r.GetInt32 5
              RoleId = r.GetString 6
              RoleVersion = r.GetInt32 7
              RequestFingerprint = r.GetString 8
              Provenance =
                { AccountHead = r.GetString 9
                  ProjectHead = r.GetString 10
                  SourceRef = opt 11
                  SourceCommit = opt 12 } }
        if not (validate intent) || r.GetString(0) <> stableUuid intent.OperationId
           || r.GetString(13) <> contentHash intent then
            invalidOp "Corrupt or foreign original intent SHA/UUID/identity."
        let outcome =
            match opt 14, opt 15 with
            | None, None -> None
            | Some "Accepted", Some receipt when bounded receipt ->
                Some(OriginalBrowserOutcome.Accepted receipt)
            | Some "DefinitiveNoEffect", Some reason when bounded reason ->
                Some(OriginalBrowserOutcome.DefinitiveNoEffect reason)
            | Some "Unknown", Some reason when bounded reason ->
                Some(OriginalBrowserOutcome.Unknown reason)
            | _ -> invalidOp "Corrupt original browser outcome."
        { Intent = intent; Outcome = outcome }

    let private find (connection: SqliteConnection) tx col key =
        use query = cmd connection tx ("SELECT " + selectColumns + " FROM originals WHERE " + col + "=$key;")
        add query "$key" (box key)
        use reader = query.ExecuteReader()
        if not (reader.Read()) then None
        else
            let result = fromReader reader
            if reader.Read() then invalidOp "Duplicate original browser journal key."
            Some result

    let findOriginal store operationId : Result<OriginalBrowserJournalEntry option,string> =
        protect (fun () ->
            if not (bounded operationId) then invalidArg "operationId" "Original ID required."
            use connection = connect true store
            verifyInventory store connection None
            find connection None "operation_id" operationId)

    let findByLaunch store launchId : Result<OriginalBrowserJournalEntry option,string> =
        protect (fun () ->
            if not (bounded launchId) then invalidArg "launchId" "Launch ID required."
            use connection = connect true store
            verifyInventory store connection None
            find connection None "launch_id" launchId)

    let private setIntent (c: SqliteCommand) (intent: OriginalBrowserIntent) =
        add c "$uuid" (box (stableUuid intent.OperationId))
        add c "$op" (box intent.OperationId)
        add c "$launch" (box intent.LaunchId)
        add c "$project" (box intent.ProjectId)
        add c "$work" (box intent.WorkId)
        add c "$generation" (box intent.WorkGeneration)
        add c "$role" (box intent.RoleId)
        add c "$version" (box intent.RoleVersion)
        add c "$fingerprint" (box intent.RequestFingerprint)
        add c "$account" (box intent.Provenance.AccountHead)
        add c "$head" (box intent.Provenance.ProjectHead)
        add c "$ref" (intent.Provenance.SourceRef |> Option.map box |> Option.defaultValue null)
        add c "$commit" (intent.Provenance.SourceCommit |> Option.map box |> Option.defaultValue null)
        add c "$sha" (box (contentHash intent))

    // SQLite serializes competing process writers. A durable COMMIT must finish
    // before Marked is returned to WQ08 and before its provider callback runs.
    let tryMarkOriginal store intent : Result<OriginalMarkResult,string> =
        protect (fun () ->
            if not (validate intent) then invalidArg "intent" "Incomplete original browser identity."
            use connection = connect false store
            use transaction = connection.BeginTransaction()
            verifyInventory store connection (Some transaction)
            use insert = cmd connection (Some transaction) ("INSERT OR IGNORE INTO originals (" +
                 "operation_uuid,operation_id,launch_id,project_id,work_id,work_generation,role_id," +
                 "role_version,request_fingerprint,account_head,project_head,source_ref,source_commit,intent_sha) " +
                 "VALUES ($uuid,$op,$launch,$project,$work,$generation,$role,$version," +
                 "$fingerprint,$account,$head,$ref,$commit,$sha);")
            setIntent insert intent
            let written = insert.ExecuteNonQuery()
            let result =
                if written = 1 then
                    anchorOriginal store intent
                    OriginalMarkResult.Marked
                else
                    match find connection (Some transaction) "operation_id" intent.OperationId with
                    | Some previous when previous.Intent = intent -> OriginalMarkResult.Existing previous
                    | Some _ -> invalidOp "IdentityConflict: original operation ID reused for a different intent."
                    | None -> invalidOp "IdentityConflict: launch ID already belongs to another original operation."
            transaction.Commit()
            result)

    let private outcomeFields outcome =
        match outcome with
        | OriginalBrowserOutcome.Accepted receipt when bounded receipt -> "Accepted", receipt
        | OriginalBrowserOutcome.DefinitiveNoEffect reason when bounded reason -> "DefinitiveNoEffect", reason
        | OriginalBrowserOutcome.Unknown reason when bounded reason -> "Unknown", reason
        | _ -> invalidArg "outcome" "Original outcome needs nonempty verified evidence or uncertainty reason."

    let recordOriginalOutcome store intent outcome : Result<unit,string> =
        protect (fun () ->
            if not (validate intent) then invalidArg "intent" "Incomplete original identity."
            let kind, detail = outcomeFields outcome
            use connection = connect false store
            use transaction = connection.BeginTransaction()
            verifyInventory store connection (Some transaction)
            match find connection (Some transaction) "operation_id" intent.OperationId with
            | None -> invalidOp "Original marker is missing; outcome cannot be invented."
            | Some existing when existing.Intent <> intent ->
                invalidOp "IdentityConflict: original browser intent changed."
            | Some existing ->
                match existing.Outcome with
                | Some previous when previous = outcome -> ()
                | Some _ -> invalidOp "Original outcome is immutable; reconcile conflicting receipt manually."
                | None ->
                    use update = cmd connection (Some transaction) ("UPDATE originals SET outcome_kind=$kind,outcome_detail=$detail " +
                         "WHERE operation_id=$op AND outcome_kind IS NULL;")
                    add update "$kind" (box kind)
                    add update "$detail" (box detail)
                    add update "$op" (box intent.OperationId)
                    if update.ExecuteNonQuery() <> 1 then
                        invalidOp "Original outcome CAS failed; reread before reconciliation."
            transaction.Commit())
