namespace Server

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite
open Jarvis.WorkSnapshots

// F1: isolated, default-off physical backup of the SQLite B authority.
// The generic B BodyJson field is NOT a trusted credential boundary. Export
// deliberately accepts only an explicitly typed, closed, secret-free subset.
// Other bodies must be mapped by a reviewed domain projection before backup.
type private BackupRow =
    { Project: string; Kind: string; Id: string; Version: int64; Body: string
      Graph: int64; Stage: int64; Claim: int64; Stop: int64; Role: int64
      Source: string option; Review: string option; Integration: string option; Effect: string }

type private BackupCapacity = { Account: string; Version: int64; Maximum: int; Reserved: int }
type private BackupReceipt = { Operation: string; Hash: string; Sequence: int64 }
type private BackupAudit = { Operation: string; Hash: string; Sequence: int64; CommittedAt: string }
type private BackupState =
    { Sequence: int64; Rows: BackupRow list; Capacities: BackupCapacity list
      Receipts: BackupReceipt list; Audit: BackupAudit list }

/// A restored history is never proof that newer external launches, reviews,
/// integration outcomes, or authorizations did not occur.
type WorkSqliteRestorationReceipt =
    { CommittedSequence: int64
      RequiresRecovery: bool }

[<RequireQualifiedAccess>]
module WorkSqliteBackup =
    [<Literal>]
    let FormatVersion = 2
    [<Literal>]
    let DatabaseSchema = 1
    [<Literal>]
    let private ApplicationId = 0x4A57524B

    let private utf8 = UTF8Encoding(false, true)
    let private error message = invalidOp message
    let private ensure condition message = if not condition then error message
    let private hash (bytes: byte[]) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()
    let private hex length (value: string) =
        not (isNull value) && value.Length = length &&
        (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
    let private sha (value: string option) =
        Option.forall (fun s -> hex 40 s || hex 64 s) value
    let private safeId (value: string) =
        not (String.IsNullOrEmpty value) && value.Length <= 128 &&
        Char.IsAsciiLetterOrDigit value.[0] &&
        (value |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')) &&
        not (value.Contains("..", StringComparison.Ordinal)) &&
        not (value.EndsWith(".", StringComparison.Ordinal))
    let private safeEntity (value: string) = safeId value
    let private kindNames =
        set [ "project"; "work"; "stage"; "launch"; "role"; "review"; "integration"; "external-effect" ]
    let private effectNames =
        set [ "none"; "prepared"; "unknown"; "definitive-no-effect"; "observed" ]
    let private stateNames =
        set [ "Draft"; "Proposed"; "Open"; "Available"; "Blocked"; "Claimed";
              "InProgress"; "Pending"; "Prepared"; "Unknown"; "Completed"; "Accepted";
              "Cancelled"; "Closed"; "Released"; "Integrated"; "Observed";
              "DefinitiveNoEffect"; "Ready"; "Running"; "Stopped" ]

    // No generic recursive traversal, no free-form string values and no
    // "unknown field => copy it" fallback. Whitelist growth requires reviewed
    // domain-to-physical mapping and negative credential tests.
    let private safeBody (body: string) =
        try
            use document = JsonDocument.Parse(body)
            if document.RootElement.ValueKind <> JsonValueKind.Object then false
            else
                let properties = document.RootElement.EnumerateObject() |> Seq.toList
                properties.Length > 0 &&
                (properties |> List.map _.Name |> Set.ofList |> Set.count) = properties.Length &&
                (properties |> List.forall (fun p ->
                    match p.Name, p.Value.ValueKind with
                    | ("state" | "status"), JsonValueKind.String ->
                        stateNames.Contains(p.Value.GetString())
                    | "credentialVerifierSha256", JsonValueKind.String ->
                        hex 64 (p.Value.GetString())
                    | _ -> false))
        with :? JsonException -> false

    let private validate (state: BackupState) =
        ensure (state.Sequence >= 0L) "Negative committed sequence"
        let rowKeys = state.Rows |> List.map (fun r -> r.Project, r.Kind, r.Id)
        ensure (rowKeys.Length = (rowKeys |> Set.ofList |> Set.count)) "Duplicate record key"
        for row in state.Rows do
            ensure (safeId row.Project && safeEntity row.Id && kindNames.Contains row.Kind)
                "Unsafe or unsupported record identity"
            ensure (row.Version > 0L && [ row.Graph; row.Stage; row.Claim; row.Stop; row.Role ]
                    |> List.forall (fun value -> value >= 0L)) "Invalid durable generation"
            ensure (sha row.Source && sha row.Review && sha row.Integration) "Invalid Git SHA"
            ensure (effectNames.Contains row.Effect) "Invalid external effect fence"
            ensure (safeBody row.Body) "No trusted, credential-free projection for SQLite body"
        let accountIds = state.Capacities |> List.map _.Account
        ensure (accountIds.Length = (accountIds |> Set.ofList |> Set.count)) "Duplicate capacity account"
        for capacity in state.Capacities do
            ensure (safeId capacity.Account && capacity.Version > 0L &&
                    capacity.Maximum >= 0 && capacity.Reserved >= 0 &&
                    capacity.Reserved <= capacity.Maximum) "Invalid account capacity"
        let operations = state.Receipts |> List.map _.Operation
        let auditOperations = state.Audit |> List.map _.Operation
        ensure (operations.Length = (operations |> Set.ofList |> Set.count)) "Duplicate receipt"
        ensure (auditOperations.Length = (auditOperations |> Set.ofList |> Set.count)) "Duplicate audit"
        ensure (int64 state.Receipts.Length = state.Sequence &&
                int64 state.Audit.Length = state.Sequence)
            "Committed sequence lacks complete receipt and audit coverage"
        let receiptBySequence = state.Receipts |> List.map (fun r -> r.Sequence, (r.Operation, r.Hash)) |> Map.ofList
        let auditBySequence = state.Audit |> List.map (fun r -> r.Sequence, (r.Operation, r.Hash)) |> Map.ofList
        ensure (receiptBySequence.Count = state.Receipts.Length &&
                auditBySequence.Count = state.Audit.Length) "Duplicate committed sequence"
        for sequence in 1L .. state.Sequence do
            let receipt = receiptBySequence.TryFind sequence
            ensure (receipt.IsSome && auditBySequence.TryFind sequence = receipt)
                "Missing or inconsistent committed receipt and audit"
        for receipt in state.Receipts do
            let mutable guid = Guid.Empty
            ensure (Guid.TryParseExact(receipt.Operation, "D", &guid) &&
                    guid <> Guid.Empty && guid.ToString("D") = receipt.Operation &&
                    hex 64 receipt.Hash) "Invalid idempotency receipt"
        for audit in state.Audit do
            let mutable stamp = DateTimeOffset.MinValue
            ensure (DateTimeOffset.TryParseExact(audit.CommittedAt, "O",
                      CultureInfo.InvariantCulture, DateTimeStyles.None, &stamp))
                "Invalid audit timestamp"

    let private encode write =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true))
        write writer
        writer.Flush()
        Array.append (buffer.ToArray()) [| byte '\n' |]

    let private optional (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some s -> writer.WriteString(name, s)
        | None -> writer.WriteNull(name)

    let private bytes (state: BackupState) =
        validate state
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", FormatVersion)
            writer.WriteNumber("databaseSchemaVersion", DatabaseSchema)
            writer.WriteNumber("committedSequence", state.Sequence)
            writer.WriteStartArray("records")
            for row in state.Rows |> List.sortBy (fun r -> r.Project, r.Kind, r.Id) do
                writer.WriteStartObject()
                writer.WriteString("projectId", row.Project)
                writer.WriteString("kind", row.Kind)
                writer.WriteString("id", row.Id)
                writer.WriteNumber("version", row.Version)
                writer.WriteString("bodyJson", row.Body)
                writer.WriteNumber("graphVersion", row.Graph)
                writer.WriteNumber("stageGeneration", row.Stage)
                writer.WriteNumber("claimGeneration", row.Claim)
                writer.WriteNumber("stopGeneration", row.Stop)
                writer.WriteNumber("roleVersion", row.Role)
                optional writer "sourceSha" row.Source
                optional writer "reviewSha" row.Review
                optional writer "integrationSha" row.Integration
                writer.WriteString("externalEffect", row.Effect)
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteStartArray("capacities")
            for item in state.Capacities |> List.sortBy _.Account do
                writer.WriteStartObject()
                writer.WriteString("accountId", item.Account)
                writer.WriteNumber("version", item.Version)
                writer.WriteNumber("maximum", item.Maximum)
                writer.WriteNumber("reserved", item.Reserved)
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteStartArray("receipts")
            for item in state.Receipts |> List.sortBy _.Sequence do
                writer.WriteStartObject()
                writer.WriteString("operationId", item.Operation)
                writer.WriteString("commandHash", item.Hash)
                writer.WriteNumber("sequence", item.Sequence)
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteStartArray("audit")
            for item in state.Audit |> List.sortBy _.Sequence do
                writer.WriteStartObject()
                writer.WriteNumber("sequence", item.Sequence)
                writer.WriteString("operationId", item.Operation)
                writer.WriteString("commandHash", item.Hash)
                writer.WriteString("committedAtUtc", item.CommittedAt)
                writer.WriteEndObject()
            writer.WriteEndArray()
            writer.WriteEndObject())

    let private manifest (sequence: int64) (payload: byte[]) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", FormatVersion)
            writer.WriteNumber("databaseSchemaVersion", DatabaseSchema)
            writer.WriteNumber("committedSequence", sequence)
            writer.WriteString("file", "authority.json")
            writer.WriteString("sha256", hash payload)
            writer.WriteEndObject())

    let private package state =
        let payload = bytes state
        { Files = Map.ofList [ "authority.json", payload
                               "manifest.json", manifest state.Sequence payload ] }

    let private select (connection: SqliteConnection) (tx: SqliteTransaction) query read =
        use cmd = connection.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- query
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do yield read reader ]

    let private scalar (connection: SqliteConnection) (tx: SqliteTransaction) query =
        use cmd = connection.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- query
        cmd.ExecuteScalar() |> Convert.ToInt64

    let private capture path =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadOnly
        // Avoid a pooled reader keeping the sealed restore file open on Windows.
        builder.Pooling <- false
        use conn = new SqliteConnection(builder.ToString())
        conn.Open()
        use tx = conn.BeginTransaction()
        ensure (scalar conn tx "PRAGMA application_id;" = int64 ApplicationId) "Foreign SQLite application"
        ensure (scalar conn tx "PRAGMA user_version;" = int64 DatabaseSchema) "Unsupported SQLite schema"
        let sequence = scalar conn tx "SELECT last_sequence FROM work_meta WHERE id=1;"
        let readOption (reader: SqliteDataReader) index =
            if reader.IsDBNull index then None else Some(reader.GetString index)
        let rows =
            select conn tx
                "SELECT project_id,record_kind,entity_id,version,body_json,graph_version,stage_generation,claim_generation,stop_generation,role_version,source_sha,review_sha,integration_sha,external_effect FROM work_records ORDER BY project_id,record_kind,entity_id;"
                (fun r ->
                    { Project = r.GetString 0; Kind = r.GetString 1; Id = r.GetString 2
                      Version = r.GetInt64 3; Body = r.GetString 4; Graph = r.GetInt64 5
                      Stage = r.GetInt64 6; Claim = r.GetInt64 7; Stop = r.GetInt64 8
                      Role = r.GetInt64 9; Source = readOption r 10; Review = readOption r 11
                      Integration = readOption r 12; Effect = r.GetString 13 })
        let capacities =
            select conn tx
                "SELECT account_id,version,maximum,reserved FROM work_capacity ORDER BY account_id;"
                (fun r -> { Account = r.GetString 0; Version = r.GetInt64 1
                            Maximum = r.GetInt32 2; Reserved = r.GetInt32 3 })
        let receipts =
            select conn tx
                "SELECT operation_id,command_hash,sequence FROM work_receipts ORDER BY sequence;"
                (fun r -> { Operation = r.GetString 0; Hash = r.GetString 1; Sequence = r.GetInt64 2 })
        let audit =
            select conn tx
                "SELECT operation_id,command_hash,sequence,committed_at_utc FROM work_audit ORDER BY sequence;"
                (fun r -> { Operation = r.GetString 0; Hash = r.GetString 1
                            Sequence = r.GetInt64 2; CommittedAt = r.GetString 3 })
        tx.Commit()
        { Sequence = sequence; Rows = rows; Capacities = capacities; Receipts = receipts; Audit = audit }

    let private parse (bundle: WorkSnapshotBundle) =
        ensure (bundle.Files.Count = 2 &&
                bundle.Files.ContainsKey "authority.json" &&
                bundle.Files.ContainsKey "manifest.json") "Incomplete or unexpected backup file set"
        let authority = bundle.Files.["authority.json"]
        use meta = JsonDocument.Parse(utf8.GetString(bundle.Files.["manifest.json"]))
        let propertyNames (node: JsonElement) =
            node.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        let exact (names: string list) (node: JsonElement) =
            ensure (node.ValueKind = JsonValueKind.Object &&
                    (propertyNames node |> List.sort) = (names |> List.sort)) "Unexpected JSON properties"
        let memberOf (node: JsonElement) (name: string) = node.GetProperty(name)
        let str (node: JsonElement) (name: string) = (memberOf node name).GetString()
        let number (node: JsonElement) (name: string) = (memberOf node name).GetInt64()
        let optionStr (node: JsonElement) (name: string) =
            let value = memberOf node name
            if value.ValueKind = JsonValueKind.Null then None else Some(value.GetString())
        exact [ "formatVersion"; "databaseSchemaVersion"; "committedSequence"; "file"; "sha256" ] meta.RootElement
        ensure (number meta.RootElement "formatVersion" = int64 FormatVersion &&
                number meta.RootElement "databaseSchemaVersion" = int64 DatabaseSchema &&
                str meta.RootElement "file" = "authority.json" &&
                str meta.RootElement "sha256" = hash authority) "Unsupported or corrupt backup manifest"
        use doc = JsonDocument.Parse(utf8.GetString(authority))
        let root = doc.RootElement
        exact [ "formatVersion"; "databaseSchemaVersion"; "committedSequence"; "records"; "capacities"; "receipts"; "audit" ] root
        ensure (number root "formatVersion" = int64 FormatVersion &&
                number root "databaseSchemaVersion" = int64 DatabaseSchema &&
                number root "committedSequence" = number meta.RootElement "committedSequence")
            "Backup version or committed sequence mismatch"
        let items (name: string) = (memberOf root name).EnumerateArray() |> Seq.toList
        let rows =
            items "records" |> List.map (fun node ->
                exact [ "projectId"; "kind"; "id"; "version"; "bodyJson"; "graphVersion"
                        "stageGeneration"; "claimGeneration"; "stopGeneration"; "roleVersion"
                        "sourceSha"; "reviewSha"; "integrationSha"; "externalEffect" ] node
                { Project = str node "projectId"; Kind = str node "kind"; Id = str node "id"
                  Version = number node "version"; Body = str node "bodyJson"
                  Graph = number node "graphVersion"; Stage = number node "stageGeneration"
                  Claim = number node "claimGeneration"; Stop = number node "stopGeneration"
                  Role = number node "roleVersion"; Source = optionStr node "sourceSha"
                  Review = optionStr node "reviewSha"; Integration = optionStr node "integrationSha"
                  Effect = str node "externalEffect" })
        let capacities =
            items "capacities" |> List.map (fun node ->
                exact [ "accountId"; "version"; "maximum"; "reserved" ] node
                { Account = str node "accountId"; Version = number node "version"
                  Maximum = (memberOf node "maximum").GetInt32()
                  Reserved = (memberOf node "reserved").GetInt32() })
        let receipts =
            items "receipts" |> List.map (fun node ->
                exact [ "operationId"; "commandHash"; "sequence" ] node
                { Operation = str node "operationId"; Hash = str node "commandHash"
                  Sequence = number node "sequence" })
        let audit =
            items "audit" |> List.map (fun node ->
                exact [ "operationId"; "commandHash"; "sequence"; "committedAtUtc" ] node
                { Operation = str node "operationId"; Hash = str node "commandHash"
                  Sequence = number node "sequence"; CommittedAt = str node "committedAtUtc" })
        let state =
            { Sequence = number root "committedSequence"; Rows = rows; Capacities = capacities
              Receipts = receipts; Audit = audit }
        validate state
        ensure (authority.AsSpan().SequenceEqual(bytes state)) "Noncanonical authority JSON"
        ensure (bundle.Files.["manifest.json"].AsSpan().SequenceEqual(manifest state.Sequence authority))
            "Noncanonical backup manifest"
        state

    /// Verify all schema, sequence, body, file-hash, and canonical byte
    /// constraints without touching a database or publishing to Git.
    let validateSnapshot (bundle: WorkSnapshotBundle) : Result<int64, string> =
        try parse bundle |> fun state -> Ok state.Sequence
        with ex -> Error ex.Message

    let exportSnapshot (databasePath: string) : Result<WorkSnapshotBundle, string> =
        try
            ensure (File.Exists databasePath) "SQLite authority does not exist"
            capture databasePath |> package |> Ok
        with ex -> Error ex.Message

    // Disposable restore only: no existing target file and no host wiring.
    // Restore into a private sibling, verify all checksums and full DB
    // re-export, and publish by non-overwriting atomic move.
    let restoreEmpty (bundle: WorkSnapshotBundle) (destinationPath: string) : Result<WorkSqliteRestorationReceipt, string> =
        let mutable scratch = ""
        let mutable sealedPath = ""
        let outcome =
            try
                let state = parse bundle
                ensure (not (String.IsNullOrWhiteSpace destinationPath)) "Empty restore destination"
                ensure (not (File.Exists destinationPath) &&
                        not (File.Exists(destinationPath + "-wal")) &&
                        not (File.Exists(destinationPath + "-shm"))) "Restore target already exists"
                let folder = Path.GetDirectoryName(Path.GetFullPath destinationPath)
                ensure (Directory.Exists folder) "Restore parent directory does not exist"
                scratch <- destinationPath + "." + Guid.NewGuid().ToString("N") + ".restore"
                let store = WorkSqliteAuthority(scratch)
                match store.Initialize() with
                | Ok _ -> ()
                | Error failure -> error $"Cannot initialize isolated restore: {failure}"
                let builder = SqliteConnectionStringBuilder()
                builder.DataSource <- scratch
                builder.Mode <- SqliteOpenMode.ReadWrite
                builder.Pooling <- false
                use conn = new SqliteConnection(builder.ToString())
                conn.Open()
                use tx = conn.BeginTransaction()
                let insert sql values =
                    use cmd = conn.CreateCommand()
                    cmd.Transaction <- tx
                    cmd.CommandText <- sql
                    for name, value in values do
                        cmd.Parameters.AddWithValue(name, value) |> ignore
                    ensure (cmd.ExecuteNonQuery() = 1) "Restore insert did not affect one row"
                let nullable value = match value with Some s -> box s | None -> box DBNull.Value
                for row in state.Rows do
                    insert
                        "INSERT INTO work_records(project_id,record_kind,entity_id,version,body_json,graph_version,stage_generation,claim_generation,stop_generation,role_version,source_sha,review_sha,integration_sha,external_effect) VALUES($p,$k,$id,$v,$b,$g,$st,$c,$sp,$ro,$src,$rev,$int,$ef);"
                        [ "$p", box row.Project; "$k", box row.Kind; "$id", box row.Id
                          "$v", box row.Version; "$b", box row.Body; "$g", box row.Graph
                          "$st", box row.Stage; "$c", box row.Claim; "$sp", box row.Stop
                          "$ro", box row.Role; "$src", nullable row.Source
                          "$rev", nullable row.Review; "$int", nullable row.Integration
                          "$ef", box row.Effect ]
                for item in state.Capacities do
                    insert "INSERT INTO work_capacity(account_id,version,maximum,reserved) VALUES($id,$v,$m,$r);"
                        [ "$id", box item.Account; "$v", box item.Version
                          "$m", box item.Maximum; "$r", box item.Reserved ]
                for item in state.Receipts do
                    insert "INSERT INTO work_receipts(operation_id,command_hash,sequence) VALUES($id,$h,$s);"
                        [ "$id", box item.Operation; "$h", box item.Hash; "$s", box item.Sequence ]
                for item in state.Audit do
                    insert "INSERT INTO work_audit(sequence,operation_id,command_hash,committed_at_utc) VALUES($s,$id,$h,$t);"
                        [ "$s", box item.Sequence; "$id", box item.Operation
                          "$h", box item.Hash; "$t", box item.CommittedAt ]
                use update = conn.CreateCommand()
                update.Transaction <- tx
                update.CommandText <- "UPDATE work_meta SET last_sequence=$s WHERE id=1;"
                update.Parameters.AddWithValue("$s", state.Sequence) |> ignore
                ensure (update.ExecuteNonQuery() = 1) "Restore sequence metadata absent"
                tx.Commit()
                conn.Close()
                match exportSnapshot scratch with
                | Error reason -> error $"Restore verification failed: {reason}"
                | Ok replay ->
                    ensure (replay.Files = bundle.Files) "Restored database differs from verified input"
                // SQLite initialized the scratch DB in WAL mode. Moving only the
                // main file discards committed WAL frames and corrupts a restore.
                // BackupDatabase copies a coherent snapshot into an independent
                // rollback-journal file; only that sealed file may be published.
                sealedPath <- scratch + ".sealed"
                let sealedBuilder = SqliteConnectionStringBuilder()
                sealedBuilder.DataSource <- sealedPath
                sealedBuilder.Mode <- SqliteOpenMode.ReadWriteCreate
                sealedBuilder.Pooling <- false
                let sourceBuilder = SqliteConnectionStringBuilder()
                sourceBuilder.DataSource <- scratch
                sourceBuilder.Mode <- SqliteOpenMode.ReadOnly
                sourceBuilder.Pooling <- false
                do
                    use source = new SqliteConnection(sourceBuilder.ToString())
                    use target = new SqliteConnection(sealedBuilder.ToString())
                    source.Open()
                    target.Open()
                    source.BackupDatabase(target)
                match exportSnapshot sealedPath with
                | Error reason -> error $"Sealed restore verification failed: {reason}"
                | Ok replay ->
                    ensure (replay.Files = bundle.Files) "Sealed restore differs from verified input"
                File.Move(sealedPath, destinationPath)
                Ok { CommittedSequence = state.Sequence; RequiresRecovery = true }
            with ex ->
                Error ex.Message
        for prefix in [ scratch; sealedPath ] do
            if not (String.IsNullOrEmpty prefix) then
                for suffix in [ ""; "-wal"; "-shm" ] do
                    let path = prefix + suffix
                    if File.Exists path then
                        try File.Delete path with _ -> ()
        outcome
