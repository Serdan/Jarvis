namespace Server

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite

// Default-off SQLite authority substrate. No production host registration or
// scheduler entry point lives here. Domain reducers must authenticate/authorize
// commands before calling this internal transaction boundary.
[<RequireQualifiedAccess>]
type WorkSqliteRecordKind =
    | Project
    | Work
    | Stage
    | Launch
    | Role
    | Review
    | Integration
    | ExternalEffect

type WorkSqliteRecordKey =
    { ProjectId: string
      Kind: WorkSqliteRecordKind
      EntityId: string }

[<RequireQualifiedAccess>]
type WorkSqliteEffect =
    | NoneRecorded
    | Prepared
    | Unknown
    | DefinitiveNoEffect
    | Observed

// Immutable Git evidence is referenced, not copied from a checkout. An
// "Unknown" external effect is first-class durable state, not an absence.
type WorkSqliteRecordValue =
    { BodyJson: string
      GraphVersion: int64
      StageGeneration: int64
      ClaimGeneration: int64
      StopGeneration: int64
      RoleVersion: int64
      SourceSha: string option
      ReviewSha: string option
      IntegrationSha: string option
      ExternalEffect: WorkSqliteEffect }

type WorkSqliteRecordMutation =
    { Key: WorkSqliteRecordKey
      ExpectedVersion: int64 option // None means create-only, never upsert.
      Value: WorkSqliteRecordValue }

// Read-only CAS witnesses for related graph, role, source and STOP state.
// A stage mutation can guard a different project/role/graph record in the
// SAME SQLite transaction instead of assuming its own version covers them.
type WorkSqliteRecordGuard =
    { Key: WorkSqliteRecordKey
      ExpectedVersion: int64
      GraphVersion: int64 option
      StageGeneration: int64 option
      ClaimGeneration: int64 option
      StopGeneration: int64 option
      RoleVersion: int64 option
      SourceSha: string option }

type WorkSqliteCapacityMutation =
    { AccountId: string
      ExpectedVersion: int64 option // None creates the account capacity.
      Maximum: int option // Required at creation; optional on update.
      ReservedDelta: int }

type WorkSqliteTransactionCommand =
    { OperationId: Guid
      Guards: WorkSqliteRecordGuard list
      Records: WorkSqliteRecordMutation list
      Capacities: WorkSqliteCapacityMutation list }

type WorkSqliteRecord =
    { Key: WorkSqliteRecordKey
      Version: int64
      Value: WorkSqliteRecordValue }

type WorkSqliteCapacity =
    { AccountId: string
      Version: int64
      Maximum: int
      Reserved: int }

type WorkSqliteReceipt =
    { OperationId: Guid
      Sequence: int64
      Replayed: bool }

// Original-source repository identity cannot be reconstructed from SourceSha
// or the separate Git backup location. A v2 writer must supply verified
// original-source evidence for each source-bearing record.
type WorkSqliteSourceProvenance =
    { RepositoryId: string
      SourceRef: string
      SourceCommit: string
      VerificationReceiptId: Guid
      EvidenceSha256: string }

type WorkSqliteProvenancedMutation =
    { Mutation: WorkSqliteRecordMutation
      Source: WorkSqliteSourceProvenance option }

[<RequireQualifiedAccess>]
type WorkSqliteFailure =
    | InvalidCommand of string
    | ForeignDatabase
    | UnsupportedSchemaVersion of int
    | MissingEntity of string
    | EntityAlreadyExists of string
    | StaleVersion of entity: string * expected: int64 * actual: int64
    | StaleEvidence of entity: string * field: string
    | CapacityExceeded of accountId: string
    | OperationIdReused of Guid
    | StorageUnavailable of string

module private WorkSqliteInternals =
    [<Literal>]
    let SchemaVersion = 1

    [<Literal>]
    let TrustedSchemaVersion = 2

    // "JWRK". Distinct from the legacy FeedbackStore's database.
    [<Literal>]
    let ApplicationId = 0x4A57524B

    let kindText = function
        | WorkSqliteRecordKind.Project -> "project"
        | WorkSqliteRecordKind.Work -> "work"
        | WorkSqliteRecordKind.Stage -> "stage"
        | WorkSqliteRecordKind.Launch -> "launch"
        | WorkSqliteRecordKind.Role -> "role"
        | WorkSqliteRecordKind.Review -> "review"
        | WorkSqliteRecordKind.Integration -> "integration"
        | WorkSqliteRecordKind.ExternalEffect -> "external-effect"

    let parseKind = function
        | "project" -> WorkSqliteRecordKind.Project
        | "work" -> WorkSqliteRecordKind.Work
        | "stage" -> WorkSqliteRecordKind.Stage
        | "launch" -> WorkSqliteRecordKind.Launch
        | "role" -> WorkSqliteRecordKind.Role
        | "review" -> WorkSqliteRecordKind.Review
        | "integration" -> WorkSqliteRecordKind.Integration
        | "external-effect" -> WorkSqliteRecordKind.ExternalEffect
        | name -> invalidOp $"Unknown durable Work record kind: {name}"

    let effectText = function
        | WorkSqliteEffect.NoneRecorded -> "none"
        | WorkSqliteEffect.Prepared -> "prepared"
        | WorkSqliteEffect.Unknown -> "unknown"
        | WorkSqliteEffect.DefinitiveNoEffect -> "definitive-no-effect"
        | WorkSqliteEffect.Observed -> "observed"

    let parseEffect = function
        | "none" -> WorkSqliteEffect.NoneRecorded
        | "prepared" -> WorkSqliteEffect.Prepared
        | "unknown" -> WorkSqliteEffect.Unknown
        | "definitive-no-effect" -> WorkSqliteEffect.DefinitiveNoEffect
        | "observed" -> WorkSqliteEffect.Observed
        | state -> invalidOp $"Unknown durable external-effect state: {state}"

    let keyText (key: WorkSqliteRecordKey) =
        $"{key.ProjectId}/{kindText key.Kind}/{key.EntityId}"

    let boundedId name (id: string) =
        if String.IsNullOrWhiteSpace id || id.Length > 256 || id <> id.Trim() ||
           id |> Seq.exists Char.IsControl then
            Error(WorkSqliteFailure.InvalidCommand $"{name} must be a bounded nonblank identifier.")
        else Ok ()

    let validateSha name (value: string option) =
        match value with
        | None -> Ok ()
        | Some sha when (sha.Length = 40 || sha.Length = 64) &&
                        (sha |> Seq.forall Uri.IsHexDigit) -> Ok ()
        | _ -> Error(WorkSqliteFailure.InvalidCommand $"{name} must be an exact Git SHA (40 or 64 hex characters).")

    let validateValue (value: WorkSqliteRecordValue) =
        if value.GraphVersion < 0L || value.StageGeneration < 0L ||
           value.ClaimGeneration < 0L || value.StopGeneration < 0L ||
           value.RoleVersion < 0L then
            Error(WorkSqliteFailure.InvalidCommand "Durable generation and version fields cannot be negative.")
        elif String.IsNullOrWhiteSpace value.BodyJson then
            Error(WorkSqliteFailure.InvalidCommand "Work record JSON body is required.")
        else
            try
                use document = JsonDocument.Parse value.BodyJson
                if document.RootElement.ValueKind <> JsonValueKind.Object then
                    Error(WorkSqliteFailure.InvalidCommand "Work record body must be a JSON object.")
                else
                    [ validateSha "SourceSha" value.SourceSha
                      validateSha "ReviewSha" value.ReviewSha
                      validateSha "IntegrationSha" value.IntegrationSha ]
                    |> List.tryFind Result.isError
                    |> Option.defaultValue (Ok ())
            with :? JsonException ->
                Error(WorkSqliteFailure.InvalidCommand "Work record body is not valid JSON.")

    // This is the physical v2 baseline only; it does not substitute for the
    // complete closed per-kind DTO parser or trusted producer boundary.
    let validateNoDuplicateBodyProperties (bodyJson: string) =
        let rec duplicate (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                element.EnumerateObject()
                |> Seq.exists (fun property ->
                    not (seen.Add property.Name) || duplicate property.Value)
            | JsonValueKind.Array ->
                element.EnumerateArray() |> Seq.exists duplicate
            | _ -> false
        try
            use doc = JsonDocument.Parse bodyJson
            if duplicate doc.RootElement then
                Error(WorkSqliteFailure.InvalidCommand "Duplicate JSON properties are forbidden in v2 Work-domain bodies.")
            else Ok ()
        with :? JsonException ->
            Error(WorkSqliteFailure.InvalidCommand "Invalid v2 Work-domain JSON body.")

    let validateProvenance (item: WorkSqliteProvenancedMutation) =
        let source = item.Source
        let value = item.Mutation.Value
        match value.SourceSha, source with
        | None, None -> Ok ()
        | Some _, None ->
            Error(WorkSqliteFailure.InvalidCommand "Original repository/ref evidence is required for source-bearing v2 records.")
        | None, Some _ ->
            Error(WorkSqliteFailure.InvalidCommand "Unbound original-source evidence cannot accompany an absent SourceSha.")
        | Some sha, Some proof ->
            let refText = proof.SourceRef
            let safeRef =
                not (String.IsNullOrWhiteSpace refText) && refText.Length <= 512 &&
                refText.StartsWith("refs/", StringComparison.Ordinal) &&
                not (refText.Contains("..", StringComparison.Ordinal)) &&
                not (refText.Contains("@{", StringComparison.Ordinal)) &&
                not (refText.EndsWith(".lock", StringComparison.Ordinal)) &&
                not (refText |> Seq.exists (fun c ->
                    Char.IsControl c || Char.IsWhiteSpace c ||
                    "~^:?*[\\".Contains(c)))
            let digest = proof.EvidenceSha256
            if String.IsNullOrWhiteSpace proof.RepositoryId || proof.RepositoryId.Length > 256 ||
               proof.RepositoryId <> proof.RepositoryId.Trim() ||
               proof.RepositoryId |> Seq.exists Char.IsControl ||
               not safeRef || proof.SourceCommit <> sha ||
               proof.VerificationReceiptId = Guid.Empty ||
               isNull digest || digest.Length <> 64 ||
               (digest |> Seq.exists (fun c -> not (Char.IsAsciiHexDigitLower c))) then
                Error(WorkSqliteFailure.InvalidCommand "Invalid authenticated original repository/ref/commit/evidence binding.")
            else Ok ()

    let validate (command: WorkSqliteTransactionCommand) =
        let duplicateRecords =
            command.Records
            |> List.map (fun x -> x.Key)
            |> fun keys -> keys.Length <> (keys |> List.distinct |> List.length)
        let duplicateGuards =
            command.Guards
            |> List.map (fun x -> x.Key)
            |> fun keys -> keys.Length <> (keys |> List.distinct |> List.length)
        let duplicateAccounts =
            command.Capacities
            |> List.map (fun x -> x.AccountId)
            |> fun ids -> ids.Length <> (ids |> List.distinct |> List.length)

        if command.OperationId = Guid.Empty then
            Error(WorkSqliteFailure.InvalidCommand "OperationId must be a nonempty UUID.")
        elif command.Records.IsEmpty && command.Capacities.IsEmpty then
            Error(WorkSqliteFailure.InvalidCommand "An empty transaction cannot consume a sequence.")
        elif duplicateRecords || duplicateGuards || duplicateAccounts then
            Error(WorkSqliteFailure.InvalidCommand "A transaction cannot mutate one key twice.")
        else
            let errors =
                [ for item in command.Records do
                      yield boundedId "ProjectId" item.Key.ProjectId
                      yield boundedId "EntityId" item.Key.EntityId
                      yield validateValue item.Value
                      match item.ExpectedVersion with
                      | Some version when version <= 0L ->
                          yield Error(WorkSqliteFailure.InvalidCommand "Expected record version must be positive.")
                      | _ -> ()
                  for guard in command.Guards do
                      yield boundedId "ProjectId" guard.Key.ProjectId
                      yield boundedId "EntityId" guard.Key.EntityId
                      yield validateSha "ExpectedSourceSha" guard.SourceSha
                      if guard.ExpectedVersion <= 0L ||
                         ([ guard.GraphVersion; guard.StageGeneration; guard.ClaimGeneration
                            guard.StopGeneration; guard.RoleVersion ]
                          |> List.exists (Option.exists (fun x -> x < 0L))) then
                          yield Error(WorkSqliteFailure.InvalidCommand "Invalid guard version or generation.")
                  for item in command.Capacities do
                      yield boundedId "AccountId" item.AccountId
                      match item.ExpectedVersion, item.Maximum with
                      | None, None ->
                          yield Error(WorkSqliteFailure.InvalidCommand "Capacity creation requires a limit.")
                      | _ -> ()
                      match item.ExpectedVersion with
                      | Some version when version <= 0L ->
                          yield Error(WorkSqliteFailure.InvalidCommand "Expected capacity version must be positive.")
                      | _ -> ()
                      match item.Maximum with
                      | Some maximum when maximum < 0 ->
                          yield Error(WorkSqliteFailure.InvalidCommand "Capacity maximum must be nonnegative.")
                      | _ -> () ]
            errors |> List.tryFind Result.isError |> Option.defaultValue (Ok ())

    let fingerprint (command: WorkSqliteTransactionCommand) =
        let builder = StringBuilder()
        let append (value: string) =
            builder.Append(value.Length).Append(':').Append(value) |> ignore
        let appendOption (value: string option) =
            match value with
            | None -> append "null"
            | Some v -> append "some"; append v
        let appendIntOption (value: int64 option) =
            appendOption (value |> Option.map string)
        append "WorkSqliteTransaction/v2"
        append (string command.Guards.Length)
        for guard in command.Guards do
            append guard.Key.ProjectId
            append (kindText guard.Key.Kind)
            append guard.Key.EntityId
            append (string guard.ExpectedVersion)
            appendIntOption guard.GraphVersion
            appendIntOption guard.StageGeneration
            appendIntOption guard.ClaimGeneration
            appendIntOption guard.StopGeneration
            appendIntOption guard.RoleVersion
            appendOption guard.SourceSha
        append (string command.Records.Length)
        for item in command.Records do
            append item.Key.ProjectId
            append (kindText item.Key.Kind)
            append item.Key.EntityId
            appendIntOption item.ExpectedVersion
            let value = item.Value
            append value.BodyJson
            append (string value.GraphVersion)
            append (string value.StageGeneration)
            append (string value.ClaimGeneration)
            append (string value.StopGeneration)
            append (string value.RoleVersion)
            appendOption value.SourceSha
            appendOption value.ReviewSha
            appendOption value.IntegrationSha
            append (effectText value.ExternalEffect)
        append (string command.Capacities.Length)
        for item in command.Capacities do
            append item.AccountId
            appendIntOption item.ExpectedVersion
            appendOption (item.Maximum |> Option.map string)
            append (string item.ReservedDelta)
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))
        |> Convert.ToHexString

    let fingerprintProvenanced (command: WorkSqliteTransactionCommand)
                                (records: WorkSqliteProvenancedMutation list) =
        let builder = StringBuilder("WorkSqliteAuthenticatedTransaction/v1|")
        builder.Append(fingerprint command) |> ignore
        for record in records do
            builder.Append('|').Append(keyText record.Mutation.Key) |> ignore
            match record.Source with
            | None -> builder.Append("|no-source") |> ignore
            | Some source ->
                for value in [ source.RepositoryId; source.SourceRef; source.SourceCommit
                               source.VerificationReceiptId.ToString("D"); source.EvidenceSha256 ] do
                    builder.Append('|').Append(value.Length).Append(':').Append(value) |> ignore
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))
        |> Convert.ToHexString

    let sql (connection: SqliteConnection) (transaction: SqliteTransaction option)
            (statement: string) (parameters: (string * obj) list) =
        let cmd = connection.CreateCommand()
        cmd.CommandText <- statement
        transaction |> Option.iter (fun tx -> cmd.Transaction <- tx)
        for (name, value) in parameters do
            cmd.Parameters.AddWithValue(name, value) |> ignore
        cmd

    let scalar connection transaction statement parameters =
        use cmd = sql connection transaction statement parameters
        cmd.ExecuteScalar()

    let execute connection transaction statement parameters =
        use cmd = sql connection transaction statement parameters
        cmd.ExecuteNonQuery()

    let version connection =
        scalar connection None "PRAGMA user_version;" [] |> Convert.ToInt32

    let applicationId connection =
        scalar connection None "PRAGMA application_id;" [] |> Convert.ToInt32

    let requireSchema connection =
        let schema = version connection
        let app = applicationId connection
        if (schema = SchemaVersion || schema = TrustedSchemaVersion) && app = ApplicationId then Ok ()
        elif schema = 0 && app = 0 then Error WorkSqliteFailure.ForeignDatabase
        elif schema <> SchemaVersion && schema <> TrustedSchemaVersion then
            Error(WorkSqliteFailure.UnsupportedSchemaVersion schema)
        else Error WorkSqliteFailure.ForeignDatabase

    let openConnection (path: string) (mode: SqliteOpenMode) =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- mode
        builder.DefaultTimeout <- 15
        let connection = new SqliteConnection(builder.ToString())
        try
            connection.Open()
            execute connection None "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;" [] |> ignore
            connection
        with _ ->
            connection.Dispose()
            reraise()

    let optionalString (value: string option) : obj =
        match value with Some text -> box text | None -> box DBNull.Value

    let readOptionalString (reader: SqliteDataReader) index =
        if reader.IsDBNull index then None else Some(reader.GetString index)


    // The create/upgrade scripts are the only canonical source for physical
    // schema identity. Compare every sqlite_master object against schemas
    // executed by the same SQLite engine, so comments, literals, dropped
    // constraints/indexes and unrelated objects cannot spoof the authority.
    let initialSchemaSql = """
CREATE TABLE work_meta (
    id INTEGER PRIMARY KEY CHECK(id=1),
    schema_version INTEGER NOT NULL,
    last_sequence INTEGER NOT NULL CHECK(last_sequence>=0)
);
INSERT INTO work_meta(id,schema_version,last_sequence) VALUES(1,1,0);
CREATE TABLE work_records (
    project_id TEXT NOT NULL,
    record_kind TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK(version>=1),
    body_json TEXT NOT NULL CHECK(json_valid(body_json)),
    graph_version INTEGER NOT NULL CHECK(graph_version>=0),
    stage_generation INTEGER NOT NULL CHECK(stage_generation>=0),
    claim_generation INTEGER NOT NULL CHECK(claim_generation>=0),
    stop_generation INTEGER NOT NULL CHECK(stop_generation>=0),
    role_version INTEGER NOT NULL CHECK(role_version>=0),
    source_sha TEXT,
    review_sha TEXT,
    integration_sha TEXT,
    external_effect TEXT NOT NULL CHECK(external_effect IN
        ('none','prepared','unknown','definitive-no-effect','observed')),
    PRIMARY KEY(project_id,record_kind,entity_id)
);
CREATE INDEX work_records_project_kind ON work_records(project_id,record_kind);
CREATE INDEX work_records_stage_claim ON work_records(record_kind,project_id,claim_generation);
CREATE TABLE work_capacity (
    account_id TEXT PRIMARY KEY,
    version INTEGER NOT NULL CHECK(version>=1),
    maximum INTEGER NOT NULL CHECK(maximum>=0),
    reserved INTEGER NOT NULL CHECK(reserved>=0 AND reserved<=maximum)
);
CREATE TABLE work_receipts (
    operation_id TEXT PRIMARY KEY,
    command_hash TEXT NOT NULL,
    sequence INTEGER NOT NULL UNIQUE CHECK(sequence>=1)
);
CREATE TABLE work_audit (
    sequence INTEGER PRIMARY KEY,
    operation_id TEXT NOT NULL UNIQUE REFERENCES work_receipts(operation_id),
    command_hash TEXT NOT NULL,
    committed_at_utc TEXT NOT NULL
);
PRAGMA application_id = 1247236683;
PRAGMA user_version = 1;
"""

    let provenanceV2UpgradeSql = """
ALTER TABLE work_records ADD COLUMN source_repository_id TEXT;
ALTER TABLE work_records ADD COLUMN source_ref TEXT;
ALTER TABLE work_records ADD COLUMN source_verification_receipt_id TEXT;
ALTER TABLE work_records ADD COLUMN source_evidence_sha256 TEXT;
UPDATE work_meta SET schema_version=2 WHERE id=1 AND schema_version=1;
PRAGMA user_version=2;
"""

    // sqlite_master.sql must match the canonical SQLite-generated DDL exactly.
    // Folding case or whitespace changes literals and quoted identifiers,
    // letting an altered CHECK expression impersonate the trusted schema.

    let private schemaCatalog (connection: SqliteConnection) =
        use command = sql connection None
                        """SELECT type, name, tbl_name, sql
                           FROM sqlite_master ORDER BY type, name, tbl_name;""" []
        use reader = command.ExecuteReader()
        [ while reader.Read() do
            let definition =
                if reader.IsDBNull 3 then None
                else Some(reader.GetString 3)
            yield reader.GetString 0, reader.GetString 1,
                  reader.GetString 2, definition ]

    // Lazy creation avoids per-initialization DDL replay and keeps version 2
    // ALTER TABLE formatting identical to SQLite's real migration behavior.
    let private expectedCatalogs = lazy (
        use canonical = openConnection ":memory:" SqliteOpenMode.ReadWriteCreate
        execute canonical None initialSchemaSql [] |> ignore
        let v1 = schemaCatalog canonical
        execute canonical None provenanceV2UpgradeSql [] |> ignore
        let v2 = schemaCatalog canonical
        Map.ofList [ SchemaVersion, v1; TrustedSchemaVersion, v2 ])

    // Compare physical names, affinity declarations, NOT NULL and primary-key
    // ordinals, not just the number of columns. Foreign/corrupt same-count
    // schemas must never be mistaken for an initialized Work authority.
    let physicalSchemaMatches (connection: SqliteConnection) schema =
        let column name typeName required primaryKey = name, typeName, required, primaryKey
        let expected =
            [ "work_meta",
                [ column "id" "INTEGER" false 1
                  column "schema_version" "INTEGER" true 0
                  column "last_sequence" "INTEGER" true 0 ]
              "work_records",
                [ column "project_id" "TEXT" true 1
                  column "record_kind" "TEXT" true 2
                  column "entity_id" "TEXT" true 3
                  column "version" "INTEGER" true 0
                  column "body_json" "TEXT" true 0
                  column "graph_version" "INTEGER" true 0
                  column "stage_generation" "INTEGER" true 0
                  column "claim_generation" "INTEGER" true 0
                  column "stop_generation" "INTEGER" true 0
                  column "role_version" "INTEGER" true 0
                  column "source_sha" "TEXT" false 0
                  column "review_sha" "TEXT" false 0
                  column "integration_sha" "TEXT" false 0
                  column "external_effect" "TEXT" true 0 ]
              "work_capacity",
                [ column "account_id" "TEXT" false 1
                  column "version" "INTEGER" true 0
                  column "maximum" "INTEGER" true 0
                  column "reserved" "INTEGER" true 0 ]
              "work_receipts",
                [ column "operation_id" "TEXT" false 1
                  column "command_hash" "TEXT" true 0
                  column "sequence" "INTEGER" true 0 ]
              "work_audit",
                [ column "sequence" "INTEGER" false 1
                  column "operation_id" "TEXT" true 0
                  column "command_hash" "TEXT" true 0
                  column "committed_at_utc" "TEXT" true 0 ] ]
            |> List.map (fun (table, columns) ->
                if table = "work_records" && schema = TrustedSchemaVersion then
                    table, columns @
                        [ column "source_repository_id" "TEXT" false 0
                          column "source_ref" "TEXT" false 0
                          column "source_verification_receipt_id" "TEXT" false 0
                          column "source_evidence_sha256" "TEXT" false 0 ]
                else table, columns)
        let actualColumns table =
            use cmd = sql connection None
                          """SELECT name, type, "notnull", pk
                             FROM pragma_table_info($table) ORDER BY cid;"""
                          [ "$table", box table ]
            use reader = cmd.ExecuteReader()
            [ while reader.Read() do
                yield reader.GetString 0, reader.GetString 1,
                      reader.GetInt32 2 <> 0, reader.GetInt32 3 ]
        let columnsMatch =
            expected |> List.forall (fun (table, columns) -> actualColumns table = columns)
        if not columnsMatch then false
        else
            let catalogMatches =
                match Map.tryFind schema expectedCatalogs.Value with
                | Some canonical -> schemaCatalog connection = canonical
                | None -> false
            let metaRows =
                scalar connection None "SELECT COUNT(*) FROM work_meta;" []
                |> Convert.ToInt32
            let metaSchema =
                scalar connection None "SELECT schema_version FROM work_meta WHERE id=1;" []
                |> function null -> -1 | value -> Convert.ToInt32 value
            catalogMatches && metaRows = 1 && metaSchema = schema

    let currentRecordVersion connection transaction key =
        scalar connection transaction
            "SELECT version FROM work_records WHERE project_id=$p AND record_kind=$k AND entity_id=$id;"
            [ "$p", box key.ProjectId; "$k", box (kindText key.Kind); "$id", box key.EntityId ]
        |> function null -> None | value -> Some(Convert.ToInt64 value)

    let checkGuard connection transaction (guard: WorkSqliteRecordGuard) =
        let id = keyText guard.Key
        use cmd = sql connection (Some transaction)
                    """SELECT version, graph_version, stage_generation, claim_generation,
                              stop_generation, role_version, source_sha
                       FROM work_records
                       WHERE project_id=$p AND record_kind=$kind AND entity_id=$id;"""
                    [ "$p", box guard.Key.ProjectId; "$kind", box (kindText guard.Key.Kind)
                      "$id", box guard.Key.EntityId ]
        use reader = cmd.ExecuteReader()
        if not (reader.Read()) then Error(WorkSqliteFailure.MissingEntity id)
        else
            let actualVersion = reader.GetInt64 0
            if actualVersion <> guard.ExpectedVersion then
                Error(WorkSqliteFailure.StaleVersion(id, guard.ExpectedVersion, actualVersion))
            else
                let mismatches =
                    [ "graph", guard.GraphVersion, reader.GetInt64 1
                      "stage-generation", guard.StageGeneration, reader.GetInt64 2
                      "claim-generation", guard.ClaimGeneration, reader.GetInt64 3
                      "stop-generation", guard.StopGeneration, reader.GetInt64 4
                      "role-version", guard.RoleVersion, reader.GetInt64 5 ]
                    |> List.tryPick (fun (field, required, actual) ->
                        match required with
                        | Some expected when expected <> actual -> Some field
                        | _ -> None)
                match mismatches with
                | Some field -> Error(WorkSqliteFailure.StaleEvidence(id, field))
                | None ->
                    match guard.SourceSha with
                    | Some required when readOptionalString reader 6 <> Some required ->
                        Error(WorkSqliteFailure.StaleEvidence(id, "source-sha"))
                    | _ -> Ok ()

    let currentCapacity connection transaction account =
        use cmd = sql connection transaction
                    "SELECT version, maximum, reserved FROM work_capacity WHERE account_id=$account;"
                    [ "$account", box account ]
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some { AccountId = account
                   Version = reader.GetInt64 0
                   Maximum = reader.GetInt32 1
                   Reserved = reader.GetInt32 2 }
        else None

    let applyRecord connection transaction (item: WorkSqliteRecordMutation)
                    (source: WorkSqliteSourceProvenance option) (trustedSchema: bool) =
        let key = item.Key
        let identity = keyText key
        let current = currentRecordVersion connection (Some transaction) key
        match item.ExpectedVersion, current with
        | None, Some _ -> Error(WorkSqliteFailure.EntityAlreadyExists identity)
        | Some _, None -> Error(WorkSqliteFailure.MissingEntity identity)
        | Some expected, Some actual when expected <> actual ->
            Error(WorkSqliteFailure.StaleVersion(identity, expected, actual))
        | _ ->
            let value = item.Value
            let parameters =
                [ "$p", box key.ProjectId; "$k", box (kindText key.Kind)
                  "$id", box key.EntityId; "$json", box value.BodyJson
                  "$graph", box value.GraphVersion; "$stage", box value.StageGeneration
                  "$claim", box value.ClaimGeneration; "$stop", box value.StopGeneration
                  "$role", box value.RoleVersion; "$source", optionalString value.SourceSha
                  "$review", optionalString value.ReviewSha
                  "$integration", optionalString value.IntegrationSha
                  "$effect", box (effectText value.ExternalEffect) ]
            let sourceParameters =
                match source with
                | None -> []
                | Some proof ->
                    [ "$repo", box proof.RepositoryId; "$ref", box proof.SourceRef
                      "$evidenceReceipt", box (proof.VerificationReceiptId.ToString("D"))
                      "$evidenceSha", box proof.EvidenceSha256 ]
            let parameters = parameters @ sourceParameters
            let statement, parameters =
                match current, source with
                | None, Some _ ->
                    """INSERT INTO work_records
                        (project_id, record_kind, entity_id, version, body_json,
                         graph_version, stage_generation, claim_generation, stop_generation,
                         role_version, source_sha, review_sha, integration_sha, external_effect,
                         source_repository_id, source_ref, source_verification_receipt_id,
                         source_evidence_sha256)
                       VALUES ($p,$k,$id,1,$json,$graph,$stage,$claim,$stop,$role,$source,
                               $review,$integration,$effect,$repo,$ref,$evidenceReceipt,$evidenceSha);""",
                    parameters
                | Some expected, Some _ ->
                    """UPDATE work_records SET version=version+1, body_json=$json,
                         graph_version=$graph, stage_generation=$stage,
                         claim_generation=$claim, stop_generation=$stop,
                         role_version=$role, source_sha=$source, review_sha=$review,
                         integration_sha=$integration, external_effect=$effect,
                         source_repository_id=$repo, source_ref=$ref,
                         source_verification_receipt_id=$evidenceReceipt,
                         source_evidence_sha256=$evidenceSha
                       WHERE project_id=$p AND record_kind=$k AND entity_id=$id
                         AND version=$expected;""",
                    ("$expected", box expected) :: parameters
                | None, None ->
                    """INSERT INTO work_records
                        (project_id, record_kind, entity_id, version, body_json,
                         graph_version, stage_generation, claim_generation, stop_generation,
                         role_version, source_sha, review_sha, integration_sha, external_effect)
                       VALUES ($p,$k,$id,1,$json,$graph,$stage,$claim,$stop,$role,$source,$review,$integration,$effect);""",
                    parameters
                | Some expected, None when trustedSchema ->
                    """UPDATE work_records SET version=version+1, body_json=$json,
                         graph_version=$graph, stage_generation=$stage,
                         claim_generation=$claim, stop_generation=$stop,
                         role_version=$role, source_sha=$source, review_sha=$review,
                         integration_sha=$integration, external_effect=$effect,
                         source_repository_id=NULL, source_ref=NULL,
                         source_verification_receipt_id=NULL,
                         source_evidence_sha256=NULL
                       WHERE project_id=$p AND record_kind=$k AND entity_id=$id
                         AND version=$expected;""",
                    ("$expected", box expected) :: parameters
                | Some expected, None ->
                    """UPDATE work_records SET version=version+1, body_json=$json,
                         graph_version=$graph, stage_generation=$stage,
                         claim_generation=$claim, stop_generation=$stop,
                         role_version=$role, source_sha=$source, review_sha=$review,
                         integration_sha=$integration, external_effect=$effect
                       WHERE project_id=$p AND record_kind=$k AND entity_id=$id
                         AND version=$expected;""",
                    ("$expected", box expected) :: parameters
            if execute connection (Some transaction) statement parameters = 1 then Ok ()
            else Error(WorkSqliteFailure.StaleVersion(identity, defaultArg item.ExpectedVersion 0L, defaultArg current 0L))

    let applyCapacity connection transaction (item: WorkSqliteCapacityMutation) =
        let current = currentCapacity connection (Some transaction) item.AccountId
        match item.ExpectedVersion, current with
        | None, Some _ -> Error(WorkSqliteFailure.EntityAlreadyExists $"capacity/{item.AccountId}")
        | Some _, None -> Error(WorkSqliteFailure.MissingEntity $"capacity/{item.AccountId}")
        | Some expected, Some existing when expected <> existing.Version ->
            Error(WorkSqliteFailure.StaleVersion($"capacity/{item.AccountId}", expected, existing.Version))
        | _ ->
            let maximum = match item.Maximum, current with
                          | Some limit, _ -> limit
                          | None, Some existing -> existing.Maximum
                          | None, None -> 0
            let reserved = int64 (current |> Option.map (fun c -> c.Reserved) |> Option.defaultValue 0)
                           + int64 item.ReservedDelta
            if reserved < 0L || reserved > int64 maximum then
                Error(WorkSqliteFailure.CapacityExceeded item.AccountId)
            else
                let parameters =
                    [ "$account", box item.AccountId
                      "$max", box maximum; "$reserved", box (int reserved) ]
                let statement, parameters =
                    match current with
                    | None ->
                        "INSERT INTO work_capacity(account_id,version,maximum,reserved) VALUES($account,1,$max,$reserved);",
                        parameters
                    | Some existing ->
                        "UPDATE work_capacity SET version=version+1, maximum=$max, reserved=$reserved WHERE account_id=$account AND version=$expected;",
                        ("$expected", box existing.Version) :: parameters
                if execute connection (Some transaction) statement parameters = 1 then Ok ()
                else Error(WorkSqliteFailure.StaleVersion($"capacity/{item.AccountId}", defaultArg item.ExpectedVersion 0L, current |> Option.map (fun c -> c.Version) |> Option.defaultValue 0L))

    let readRecord connection (key: WorkSqliteRecordKey) =
        use cmd = sql connection None
                    """SELECT version, body_json, graph_version, stage_generation,
                              claim_generation, stop_generation, role_version,
                              source_sha, review_sha, integration_sha, external_effect
                       FROM work_records WHERE project_id=$p AND record_kind=$kind AND entity_id=$id;"""
                    [ "$p", box key.ProjectId; "$kind", box (kindText key.Kind); "$id", box key.EntityId ]
        use reader = cmd.ExecuteReader()
        if reader.Read() then
            Some { Key = key
                   Version = reader.GetInt64 0
                   Value =
                       { BodyJson = reader.GetString 1
                         GraphVersion = reader.GetInt64 2
                         StageGeneration = reader.GetInt64 3
                         ClaimGeneration = reader.GetInt64 4
                         StopGeneration = reader.GetInt64 5
                         RoleVersion = reader.GetInt64 6
                         SourceSha = readOptionalString reader 7
                         ReviewSha = readOptionalString reader 8
                         IntegrationSha = readOptionalString reader 9
                         ExternalEffect = parseEffect (reader.GetString 10) } }
        else None

/// Explicitly initialized by a future gated host adapter. Constructing this
/// service never opens a live DB, starts Work, or touches the feedback store.
type WorkSqliteAuthority(databasePath: string) =
    let path = databasePath

    let storageError (e: exn) =
        WorkSqliteFailure.StorageUnavailable($"{e.GetType().Name}: {e.Message}")

    let guarded f =
        try f()
        with
        | :? SqliteException as ex -> Error(storageError ex)
        | :? IOException as ex -> Error(storageError ex)
        | :? UnauthorizedAccessException as ex -> Error(storageError ex)
        | :? InvalidOperationException as ex -> Error(storageError ex)

    member _.DatabasePath = path

    // Additive schema upgrade entrypoint. Unknown/foreign databases fail
    // closed; a legacy FeedbackStore SQLite file cannot become Work authority.
    member _.Initialize() : Result<int, WorkSqliteFailure> =
        guarded (fun () ->
            if String.IsNullOrWhiteSpace path then
                Error(WorkSqliteFailure.InvalidCommand "Database path is required.")
            else
                use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWriteCreate
                let schema = WorkSqliteInternals.version connection
                let app = WorkSqliteInternals.applicationId connection
                if (schema = WorkSqliteInternals.SchemaVersion
                    || schema = WorkSqliteInternals.TrustedSchemaVersion)
                   && app = WorkSqliteInternals.ApplicationId then
                    if WorkSqliteInternals.physicalSchemaMatches connection schema then
                        Ok schema
                    else Error WorkSqliteFailure.ForeignDatabase
                elif schema <> 0 then
                    Error(WorkSqliteFailure.UnsupportedSchemaVersion schema)
                elif app <> 0 then
                    Error WorkSqliteFailure.ForeignDatabase
                else
                    let tables =
                        WorkSqliteInternals.scalar connection None
                            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';" []
                        |> Convert.ToInt32
                    if tables <> 0 then Error WorkSqliteFailure.ForeignDatabase
                    else
                        WorkSqliteInternals.execute connection None "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" [] |> ignore
                        use transaction = connection.BeginTransaction()
                        let create = WorkSqliteInternals.initialSchemaSql
                        WorkSqliteInternals.execute connection (Some transaction) create [] |> ignore
                        transaction.Commit()
                        Ok WorkSqliteInternals.SchemaVersion)

    // Explicit opt-in migration. Never infer original RepositoryId/SourceRef
    // from an existing SHA; pre-v2 rows remain untrusted until reimported by
    // independently verified original source authorities.
    member _.InitializeProvenanceV2() : Result<int, WorkSqliteFailure> =
        guarded (fun () ->
            use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
            let schema = WorkSqliteInternals.version connection
            let app = WorkSqliteInternals.applicationId connection
            if app <> WorkSqliteInternals.ApplicationId then
                Error WorkSqliteFailure.ForeignDatabase
            elif schema <> WorkSqliteInternals.SchemaVersion &&
                 schema <> WorkSqliteInternals.TrustedSchemaVersion then
                Error(WorkSqliteFailure.UnsupportedSchemaVersion schema)
            else
                if not (WorkSqliteInternals.physicalSchemaMatches connection schema) then
                    Error WorkSqliteFailure.ForeignDatabase
                elif schema = WorkSqliteInternals.TrustedSchemaVersion then
                    Ok schema
                else
                    use tx = connection.BeginTransaction()
                    let upgrade = WorkSqliteInternals.provenanceV2UpgradeSql
                    WorkSqliteInternals.execute connection (Some tx) upgrade [] |> ignore
                    tx.Commit()
                    Ok WorkSqliteInternals.TrustedSchemaVersion)

    member private _.CommitCore(command: WorkSqliteTransactionCommand,
                                provenance: WorkSqliteProvenancedMutation list option)
                                : Result<WorkSqliteReceipt, WorkSqliteFailure> =
        let validation =
            WorkSqliteInternals.validate command
            |> Result.bind (fun () ->
                match provenance with
                | None -> Ok ()
                | Some records ->
                    if records.Length <> command.Records.Length ||
                       (List.map (fun r -> r.Mutation) records) <> command.Records then
                        Error(WorkSqliteFailure.InvalidCommand "Typed v2 mutations must exactly match their transaction.")
                    else
                        records
                        |> List.map (fun record ->
                            WorkSqliteInternals.validateProvenance record
                            |> Result.bind (fun () ->
                                WorkSqliteInternals.validateNoDuplicateBodyProperties record.Mutation.Value.BodyJson))
                        |> List.tryFind Result.isError |> Option.defaultValue (Ok ()))
        match validation with
        | Error failure -> Error failure
        | Ok () ->
            guarded (fun () ->
                use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
                match WorkSqliteInternals.requireSchema connection with
                | Error failure -> Error failure
                | Ok () when WorkSqliteInternals.version connection = WorkSqliteInternals.SchemaVersion
                             && provenance.IsSome ->
                    Error(WorkSqliteFailure.UnsupportedSchemaVersion WorkSqliteInternals.SchemaVersion)
                | Ok () when WorkSqliteInternals.version connection = WorkSqliteInternals.TrustedSchemaVersion
                             && provenance.IsNone ->
                    Error(WorkSqliteFailure.InvalidCommand "Legacy untyped Commit cannot write a trusted v2 database.")
                | Ok () ->
                    use transaction = connection.BeginTransaction()
                    let operationId = command.OperationId.ToString("D")
                    let hash =
                        match provenance with
                        | Some records -> WorkSqliteInternals.fingerprintProvenanced command records
                        | None -> WorkSqliteInternals.fingerprint command
                    use receipt = WorkSqliteInternals.sql connection (Some transaction)
                                    "SELECT command_hash, sequence FROM work_receipts WHERE operation_id=$id;"
                                    [ "$id", box operationId ]
                    use reader = receipt.ExecuteReader()
                    let previous =
                        if reader.Read() then Some(reader.GetString 0, reader.GetInt64 1)
                        else None
                    reader.Close()
                    match previous with
                    | Some(priorHash, sequence) when priorHash = hash ->
                        Ok { OperationId = command.OperationId; Sequence = sequence; Replayed = true }
                    | Some _ -> Error(WorkSqliteFailure.OperationIdReused command.OperationId)
                    | None ->
                        let rec applyRecordChanges (pending: WorkSqliteRecordMutation list) =
                            match pending with
                            | [] -> Ok ()
                            | mutation :: rest ->
                                let source =
                                    provenance
                                    |> Option.bind (List.tryFind (fun (x: WorkSqliteProvenancedMutation) -> x.Mutation.Key = mutation.Key))
                                    |> Option.bind (fun x -> x.Source)
                                match WorkSqliteInternals.applyRecord connection transaction mutation source provenance.IsSome with
                                | Ok () -> applyRecordChanges rest
                                | Error error -> Error error
                        let rec applyCapacityChanges = function
                            | [] -> Ok ()
                            | mutation :: rest ->
                                match WorkSqliteInternals.applyCapacity connection transaction mutation with
                                | Ok () -> applyCapacityChanges rest
                                | Error error -> Error error
                        let guardedRecords =
                            command.Guards
                            |> List.fold (fun state guard ->
                                match state with
                                | Error _ -> state
                                | Ok () -> WorkSqliteInternals.checkGuard connection transaction guard) (Ok ())
                            |> Result.bind (fun () -> applyRecordChanges command.Records)
                        match guardedRecords with
                        | Error error -> Error error
                        | Ok () ->
                            match applyCapacityChanges command.Capacities with
                            | Error error -> Error error
                            | Ok () ->
                                let sequence =
                                    WorkSqliteInternals.scalar connection (Some transaction)
                                        "SELECT last_sequence FROM work_meta WHERE id=1;" []
                                    |> Convert.ToInt64
                                    |> fun last -> last + 1L
                                WorkSqliteInternals.execute connection (Some transaction)
                                    "UPDATE work_meta SET last_sequence=$sequence WHERE id=1;"
                                    [ "$sequence", box sequence ] |> ignore
                                WorkSqliteInternals.execute connection (Some transaction)
                                    "INSERT INTO work_receipts(operation_id,command_hash,sequence) VALUES($id,$hash,$sequence);"
                                    [ "$id", box operationId; "$hash", box hash; "$sequence", box sequence ] |> ignore
                                WorkSqliteInternals.execute connection (Some transaction)
                                    "INSERT INTO work_audit(sequence,operation_id,command_hash,committed_at_utc) VALUES($sequence,$id,$hash,$now);"
                                    [ "$sequence", box sequence; "$id", box operationId; "$hash", box hash
                                      "$now", box (DateTimeOffset.UtcNow.ToString("O")) ] |> ignore
                                transaction.Commit()
                                Ok { OperationId = command.OperationId; Sequence = sequence; Replayed = false })

    // Historical default-off v1 callers remain untouched. On a v2 database,
    // writes require the separately verified typed ingress, never raw BodyJson.
    member this.Commit(command: WorkSqliteTransactionCommand) =
        this.CommitCore(command, None)

    // Internal physical adapter for a closed authenticated domain producer.
    // This does not itself attest a caller-supplied verifier; no live host is
    // allowed to bind it until the exact original-producer contract is reviewed.
    member internal this.CommitProvenanced(operationId: Guid,
                                          guards: WorkSqliteRecordGuard list,
                                          records: WorkSqliteProvenancedMutation list,
                                          capacities: WorkSqliteCapacityMutation list) =
        let command =
            { OperationId = operationId
              Guards = guards
              Records = records |> List.map (fun r -> r.Mutation)
              Capacities = capacities }
        this.CommitCore(command, Some records)

    // Expose the immutable applied receipt, never a reconstructed command.
    // Scheduler replay binds this receipt to original launch provenance.
    member _.ReadReceipt(operationId: Guid) : Result<WorkSqliteReceipt option, WorkSqliteFailure> =
        if operationId = Guid.Empty then
            Error(WorkSqliteFailure.InvalidCommand "operation ID is empty")
        else
            guarded (fun () ->
                use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
                match WorkSqliteInternals.requireSchema connection with
                | Error failure -> Error failure
                | Ok () ->
                    use query = WorkSqliteInternals.sql connection None
                                    "SELECT sequence FROM work_receipts WHERE operation_id=$id;"
                                    [ "$id", box (operationId.ToString("D")) ]
                    use reader = query.ExecuteReader()
                    if reader.Read() then
                        Ok(Some { OperationId = operationId; Sequence = reader.GetInt64 0; Replayed = true })
                    else Ok None)

    member _.ReadRecord(key: WorkSqliteRecordKey) : Result<WorkSqliteRecord option, WorkSqliteFailure> =
        guarded (fun () ->
            use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
            match WorkSqliteInternals.requireSchema connection with
            | Error failure -> Error failure
            | Ok () -> Ok(WorkSqliteInternals.readRecord connection key))

    // Exact physical source binding for the F1/G1b typed readers. A v1 row,
    // missing original source or a partially populated v2 row never becomes a
    // verified origin through a cache, checkout name, or content hash.
    member _.ReadSourceProvenance(key: WorkSqliteRecordKey)
            : Result<WorkSqliteSourceProvenance option, WorkSqliteFailure> =
        guarded (fun () ->
            use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
            match WorkSqliteInternals.requireSchema connection with
            | Error failure -> Error failure
            | Ok () when WorkSqliteInternals.version connection <> WorkSqliteInternals.TrustedSchemaVersion ->
                Error(WorkSqliteFailure.UnsupportedSchemaVersion WorkSqliteInternals.SchemaVersion)
            | Ok () ->
                use cmd = WorkSqliteInternals.sql connection None
                            """SELECT source_sha, source_repository_id, source_ref,
                                      source_verification_receipt_id, source_evidence_sha256
                               FROM work_records
                               WHERE project_id=$p AND record_kind=$k AND entity_id=$id;"""
                            [ "$p", box key.ProjectId
                              "$k", box (WorkSqliteInternals.kindText key.Kind)
                              "$id", box key.EntityId ]
                use reader = cmd.ExecuteReader()
                if not (reader.Read()) then
                    Error(WorkSqliteFailure.MissingEntity (WorkSqliteInternals.keyText key))
                else
                    let parts = [ for i in 0 .. 4 -> WorkSqliteInternals.readOptionalString reader i ]
                    match parts with
                    | [ None; None; None; None; None ] -> Ok None
                    | [ Some sha; Some repo; Some sourceRef; Some receipt; Some evidence ] ->
                        match Guid.TryParse receipt with
                        | true, receiptId ->
                            let proof =
                                { RepositoryId = repo; SourceRef = sourceRef
                                  SourceCommit = sha; VerificationReceiptId = receiptId
                                  EvidenceSha256 = evidence }
                            let dummy =
                                { Mutation =
                                    { Key = key; ExpectedVersion = None
                                      Value =
                                        { BodyJson = "{}"; GraphVersion = 0L; StageGeneration = 0L
                                          ClaimGeneration = 0L; StopGeneration = 0L; RoleVersion = 0L
                                          SourceSha = Some sha; ReviewSha = None; IntegrationSha = None
                                          ExternalEffect = WorkSqliteEffect.NoneRecorded } }
                                  Source = Some proof }
                            WorkSqliteInternals.validateProvenance dummy
                            |> Result.map (fun () -> Some proof)
                        | _ -> Error(WorkSqliteFailure.InvalidCommand "Invalid persisted original-source receipt.")
                    | _ ->
                        Error(WorkSqliteFailure.InvalidCommand "Incomplete trusted source-provenance columns."))

    member _.ReadCapacity(accountId: string) : Result<WorkSqliteCapacity option, WorkSqliteFailure> =
        guarded (fun () ->
            use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
            match WorkSqliteInternals.requireSchema connection with
            | Error failure -> Error failure
            | Ok () -> Ok(WorkSqliteInternals.currentCapacity connection None accountId))

    member _.CurrentSequence() : Result<int64, WorkSqliteFailure> =
        guarded (fun () ->
            use connection = WorkSqliteInternals.openConnection path SqliteOpenMode.ReadWrite
            match WorkSqliteInternals.requireSchema connection with
            | Error failure -> Error failure
            | Ok () ->
                WorkSqliteInternals.scalar connection None "SELECT last_sequence FROM work_meta WHERE id=1;" []
                |> Convert.ToInt64 |> Ok)
