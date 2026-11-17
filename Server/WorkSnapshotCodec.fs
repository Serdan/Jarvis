namespace Jarvis.WorkSnapshots

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

// F0: pure, storage-independent backup format. The SQLite B lane owns the
// authoritative row mapping; adding a logical field changes the format version.
type WorkSnapshotSource =
    { RepositoryId: string
      Ref: string
      Commit: string }

type WorkSnapshotRecord =
    { Id: string
      Kind: string
      Revision: int64
      Sequence: int64
      Source: WorkSnapshotSource option
      Fields: Map<string, string> }

type WorkSnapshotProject =
    { ProjectId: string
      Records: WorkSnapshotRecord list }

type WorkSnapshot =
    { CommittedSequence: int64
      AccountRecords: WorkSnapshotRecord list
      Projects: WorkSnapshotProject list }

type WorkSnapshotBundle = { Files: Map<string, byte[]> }

[<RequireQualifiedAccess>]
module WorkSnapshotCodec =
    [<Literal>]
    let FormatVersion = 1

    let private utf8 = UTF8Encoding(false, true)
    let private ordinal (a: string) (b: string) = StringComparer.Ordinal.Compare(a, b)
    let private sha256 (bytes: byte[]) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun x -> x.ToLowerInvariant()

    let private reject message = invalidOp message
    let private ensure condition message = if not condition then reject message
    let private legalId (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 256
        && value = value.Trim()
        && not (value |> Seq.exists Char.IsControl)

    let private safeProjectId (value: string) =
        legalId value && value.Length <= 128
        && Char.IsAsciiLetterOrDigit value.[0]
        && (value |> Seq.forall (fun c ->
            Char.IsAsciiLetterOrDigit c || c = '_' || c = '-' || c = '.'))
        && not (value.Contains("..", StringComparison.Ordinal))
        && not (value.EndsWith(".", StringComparison.Ordinal))

    let private hash64 (value: string) =
        not (isNull value)
        && value.Length = 64
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    let private commitHash (value: string) =
        not (isNull value) && (value.Length = 40 || value.Length = 64)
        && (value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))

    // A closed vocabulary means bearer plaintext, free-form payloads and
    // ephemeral operator grants have no serializable representation in F0.
    let private allowedFields kind =
        match kind with
        | "AccountCapacity" -> Some(set [ "maxConcurrent"; "stopRequested"; "status" ])
        | "WorkUnit" -> Some(set [ "unitKind"; "parentId"; "status"; "specSha"; "required"; "priority" ])
        | "WorkStage" -> Some(set [ "workId"; "queueId"; "roleId"; "status"; "priority"; "generation"; "availableSequence"; "claimId" ])
        | "Reservation" -> Some(set [ "workId"; "roleId"; "status"; "generation"; "stageId" ])
        | "ScheduledVerifier" -> Some(set [ "verifierSha256"; "reservationId" ])
        | "EffectJournal" -> Some(set [ "workId"; "effectKind"; "status"; "operationId"; "targetRef" ])
        | "Receipt" -> Some(set [ "workId"; "operationId"; "status"; "reviewSha"; "integrationSha" ])
        | "AuditEvent" -> Some(set [ "workId"; "eventKind"; "operationId"; "status" ])
        | _ -> None

    let private numberField (_key: string) (value: string) =
        let mutable result = 0L
        Int64.TryParse(value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, &result)
        && result >= 0L
        && result.ToString(Globalization.CultureInfo.InvariantCulture) = value

    let private validField (kind: string) (key: string) (value: string) =
        legalId key && not (isNull value) && value.Length <= 1024
        && not (value |> Seq.exists Char.IsControl)
        && (match kind, key with
            | "ScheduledVerifier", "verifierSha256" -> hash64 value
            | _, ("priority" | "generation" | "availableSequence" | "maxConcurrent") -> numberField key value
            | _, ("stopRequested" | "required") -> value = "true" || value = "false"
            | _, ("specSha" | "reviewSha" | "integrationSha") -> commitHash value
            | _, "status" -> value.Length > 0 && value.Length <= 64
                               && (value |> Seq.forall Char.IsAsciiLetter)
            | _, "unitKind" -> Set.contains value (set ["Project"; "System"; "Feature"])
            | _, "effectKind" -> value.Length > 0 && value.Length <= 64
                                 && (value |> Seq.forall Char.IsAsciiLetter)
            | _, "eventKind" -> value.Length > 0 && value.Length <= 64
                                && (value |> Seq.forall Char.IsAsciiLetter)
            | _ -> legalId value)

    let private validateRecord maxSeq (record: WorkSnapshotRecord) =
        ensure (legalId record.Id) "Invalid record identity"
        ensure (record.Revision > 0L && record.Sequence > 0L && record.Sequence <= maxSeq) "Invalid record sequence"
        let allowed =
            match allowedFields record.Kind with
            | Some names -> names
            | None -> reject "Unsupported record kind"
        ensure (not record.Fields.IsEmpty) "Empty logical record"
        ensure (record.Fields |> Map.forall (fun key value ->
            allowed.Contains key && validField record.Kind key value)) "Unsupported or invalid logical field"
        if record.Kind = "ScheduledVerifier" then
            ensure (record.Fields.ContainsKey "verifierSha256"
                    && record.Fields.ContainsKey "reservationId"
                    && record.Fields.Count = 2) "Incomplete scheduled credential verifier"
        record.Source
        |> Option.iter (fun source ->
            ensure (legalId source.RepositoryId) "Invalid source repository"
            ensure (not (isNull source.Ref) && source.Ref.StartsWith("refs/", StringComparison.Ordinal)
                    && source.Ref.Length <= 512
                    && not (source.Ref |> Seq.exists Char.IsWhiteSpace)
                    && not (source.Ref.Contains("..", StringComparison.Ordinal)))
                "Invalid source ref"
            ensure (commitHash source.Commit) "Invalid source commit")

    let private validate (snapshot: WorkSnapshot) =
        ensure (snapshot.CommittedSequence >= 0L) "Invalid committed sequence"
        ensure (not (isNull (box snapshot.Projects))) "Missing project set"
        ensure (not (isNull (box snapshot.AccountRecords))) "Missing account records"
        let names = snapshot.Projects |> List.map _.ProjectId
        ensure (names |> List.forall safeProjectId) "Unsafe project identity"
        ensure (names.Length = (names |> Set.ofList |> Set.count)) "Duplicate project identity"
        let all = snapshot.AccountRecords @ (snapshot.Projects |> List.collect _.Records)
        ensure (all.Length = (all |> List.map _.Id |> Set.ofList |> Set.count)) "Duplicate logical record identity"
        ensure (all |> List.forall (fun r -> r.Kind <> "AccountCapacity" || snapshot.AccountRecords |> List.contains r))
            "Account authority in a project file"
        ensure (snapshot.AccountRecords |> List.forall (fun r -> r.Kind <> "ScheduledVerifier"))
            "Scheduled verifier must belong to a project"
        all |> List.iter (validateRecord snapshot.CommittedSequence)
        let maxSeq = if List.isEmpty all then 0L else all |> List.maxBy (fun x -> x.Sequence) |> fun x -> x.Sequence
        ensure (maxSeq = snapshot.CommittedSequence) "Committed sequence does not match exported records"

    let private sortedRecords records =
        records |> List.sortWith (fun a b ->
            let k = ordinal a.Kind b.Kind
            if k <> 0 then k else ordinal a.Id b.Id)

    let private sortedSources (snapshot: WorkSnapshot) =
        (snapshot.AccountRecords @ (snapshot.Projects |> List.collect _.Records))
        |> List.choose _.Source
        |> List.distinct
        |> List.sortWith (fun a b ->
            let r = ordinal a.RepositoryId b.RepositoryId
            if r <> 0 then r else
            let f = ordinal a.Ref b.Ref
            if f <> 0 then f else ordinal a.Commit b.Commit)

    let private writeSource (writer: Utf8JsonWriter) (source: WorkSnapshotSource) =
        writer.WriteStartObject()
        writer.WriteString("repositoryId", source.RepositoryId)
        writer.WriteString("ref", source.Ref)
        writer.WriteString("commit", source.Commit)
        writer.WriteEndObject()

    let private encode (write: Utf8JsonWriter -> unit) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true))
        write writer
        writer.Flush()
        // Stable LF ending and UTF-8 without BOM.
        Array.append (buffer.ToArray()) [| byte '\n' |]

    let private writeRecord (writer: Utf8JsonWriter) (record: WorkSnapshotRecord) =
        writer.WriteStartObject()
        writer.WriteString("id", record.Id)
        writer.WriteString("kind", record.Kind)
        writer.WriteNumber("revision", record.Revision)
        writer.WriteNumber("sequence", record.Sequence)
        writer.WritePropertyName("source")
        match record.Source with
        | Some source -> writeSource writer source
        | None -> writer.WriteNullValue()
        writer.WritePropertyName("fields")
        writer.WriteStartObject()
        record.Fields
        |> Map.toList
        |> List.sortWith (fun (a, _) (b, _) -> ordinal a b)
        |> List.iter (fun (key, value) -> writer.WriteString(key, value))
        writer.WriteEndObject()
        writer.WriteEndObject()

    let private writeFile (projectId: string option) (sequence: int64) (records: WorkSnapshotRecord list) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", FormatVersion)
            writer.WriteNumber("committedSequence", sequence)
            writer.WritePropertyName("projectId")
            match projectId with
            | Some name -> writer.WriteStringValue(name)
            | None -> writer.WriteNullValue()
            writer.WritePropertyName("records")
            writer.WriteStartArray()
            records |> sortedRecords |> List.iter (writeRecord writer)
            writer.WriteEndArray()
            writer.WriteEndObject())

    let private projectPath id = "projects/" + id + ".json"

    let private writeManifest (snapshot: WorkSnapshot) (entries: (string * byte[]) list) =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", FormatVersion)
            writer.WriteNumber("committedSequence", snapshot.CommittedSequence)
            writer.WritePropertyName("projects")
            writer.WriteStartArray()
            snapshot.Projects
            |> List.map _.ProjectId
            |> List.sortWith ordinal
            |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WritePropertyName("sourceRefs")
            writer.WriteStartArray()
            sortedSources snapshot |> List.iter (writeSource writer)
            writer.WriteEndArray()
            writer.WritePropertyName("files")
            writer.WriteStartArray()
            entries |> List.sortWith (fun (a, _) (b, _) -> ordinal a b)
            |> List.iter (fun (path, bytes) ->
                writer.WriteStartObject()
                writer.WriteString("path", path)
                writer.WriteString("sha256", sha256 bytes)
                writer.WriteEndObject())
            writer.WriteEndArray()
            writer.WriteEndObject())

    let private produce (snapshot: WorkSnapshot) =
        validate snapshot
        let entries =
            ("account.json", writeFile None snapshot.CommittedSequence snapshot.AccountRecords)
            :: (snapshot.Projects |> List.map (fun item ->
                projectPath item.ProjectId, writeFile (Some item.ProjectId) snapshot.CommittedSequence item.Records))
        { Files = Map.ofList (("manifest.json", writeManifest snapshot entries) :: entries) }

    /// Canonical, diffable, pure; no SQLite, IO or Git side effects.
    let private describeFailure (error: exn) =
        match error with
        | :? InvalidOperationException -> error.Message
        | :? JsonException -> "Malformed snapshot JSON"
        | :? DecoderFallbackException -> "Invalid snapshot UTF-8"
        | _ -> "Malformed or unsupported Work snapshot"

    let exportSnapshot (snapshot: WorkSnapshot) : Result<WorkSnapshotBundle, string> =
        try Ok(produce snapshot) with error -> Error(describeFailure error)

    let private fields expected (element: JsonElement) =
        ensure (element.ValueKind = JsonValueKind.Object) "Expected JSON object"
        let actual = element.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
        ensure (actual.Length = (actual |> Set.ofList |> Set.count)) "Duplicate JSON property"
        ensure (Set.ofList actual = Set.ofList expected) "Unsupported or missing JSON property"

    let private getString (element: JsonElement) (key: string) =
        let value = element.GetProperty(key)
        ensure (value.ValueKind = JsonValueKind.String) "Expected JSON string"
        value.GetString()

    let private getInt64 (element: JsonElement) (key: string) =
        let value = element.GetProperty(key)
        ensure (value.ValueKind = JsonValueKind.Number) "Expected JSON number"
        value.GetInt64()

    let private getVersion element =
        let number = getInt64 element "formatVersion"
        ensure (number = int64 FormatVersion) "Unsupported Work snapshot format"

    let private sourceFromJson (element: JsonElement) =
        fields [ "repositoryId"; "ref"; "commit" ] element
        { RepositoryId = getString element "repositoryId"
          Ref = getString element "ref"
          Commit = getString element "commit" }

    let private parseRecord (element: JsonElement) =
        fields [ "id"; "kind"; "revision"; "sequence"; "source"; "fields" ] element
        let source = element.GetProperty("source")
        let payload = element.GetProperty("fields")
        ensure (payload.ValueKind = JsonValueKind.Object) "Expected logical record fields"
        let items = payload.EnumerateObject() |> Seq.toList
        ensure (items.Length = (items |> List.map _.Name |> Set.ofList |> Set.count)) "Duplicate logical field"
        ensure (items |> List.forall (fun item -> item.Value.ValueKind = JsonValueKind.String))
            "Nonstring logical field"
        { Id = getString element "id"
          Kind = getString element "kind"
          Revision = getInt64 element "revision"
          Sequence = getInt64 element "sequence"
          Source =
              match source.ValueKind with
              | JsonValueKind.Null -> None
              | JsonValueKind.Object -> Some(sourceFromJson source)
              | _ -> reject "Malformed source provenance"
          Fields = items |> List.map (fun item -> item.Name, item.Value.GetString()) |> Map.ofList }

    let private parseRecords path committedSequence projectId (bytes: byte[]) =
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 24))
        let root = doc.RootElement
        fields [ "formatVersion"; "committedSequence"; "projectId"; "records" ] root
        getVersion root
        ensure (getInt64 root "committedSequence" = committedSequence) "Torn snapshot sequence"
        let element = root.GetProperty("projectId")
        match projectId with
        | Some id -> ensure (element.ValueKind = JsonValueKind.String && element.GetString() = id) "Project provenance mismatch"
        | None -> ensure (element.ValueKind = JsonValueKind.Null) "Account provenance mismatch"
        let array = root.GetProperty("records")
        ensure (array.ValueKind = JsonValueKind.Array) "Malformed record collection"
        array.EnumerateArray() |> Seq.map parseRecord |> List.ofSeq

    let private parseManifest (bytes: byte[]) =
        use doc = JsonDocument.Parse(bytes, JsonDocumentOptions(MaxDepth = 24))
        let root = doc.RootElement
        fields [ "formatVersion"; "committedSequence"; "projects"; "sourceRefs"; "files" ] root
        getVersion root
        let sequence = getInt64 root "committedSequence"
        let projects = root.GetProperty("projects")
        ensure (projects.ValueKind = JsonValueKind.Array) "Missing project manifest"
        let names = projects.EnumerateArray() |> Seq.map (fun e ->
            ensure (e.ValueKind = JsonValueKind.String) "Invalid project identity"
            e.GetString()) |> List.ofSeq
        let refs = root.GetProperty("sourceRefs")
        ensure (refs.ValueKind = JsonValueKind.Array) "Missing source provenance"
        let sources = refs.EnumerateArray() |> Seq.map sourceFromJson |> List.ofSeq
        let manifestFiles = root.GetProperty("files")
        ensure (manifestFiles.ValueKind = JsonValueKind.Array) "Missing file manifest"
        let entries = manifestFiles.EnumerateArray() |> Seq.map (fun e ->
            fields [ "path"; "sha256" ] e
            getString e "path", getString e "sha256") |> List.ofSeq
        sequence, names, sources, entries

    /// Fully verifies a whole logical backup before returning restorable data.
    /// The caller must supply *all* files; no dynamic paths, migrations or writes.
    let importSnapshot (bundle: WorkSnapshotBundle) : Result<WorkSnapshot, string> =
        try
            ensure (not (isNull (box bundle.Files))) "Missing snapshot bundle"
            let manifest = bundle.Files |> Map.tryFind "manifest.json" |> Option.defaultWith (fun () -> reject "Missing manifest")
            let sequence, projects, sources, entries = parseManifest manifest
            ensure (projects |> List.forall safeProjectId) "Unsafe project identity"
            ensure (projects = (projects |> List.sortWith ordinal)) "Noncanonical project list"
            ensure (projects.Length = (projects |> Set.ofList |> Set.count)) "Duplicate project"
            let expectedNames = "account.json" :: (projects |> List.map projectPath) |> List.sortWith ordinal
            let actualNames = entries |> List.map fst
            ensure (actualNames = expectedNames) "Missing, duplicate or unexpected manifest file"
            ensure (bundle.Files.Count = entries.Length + 1) "Missing or unexpected backup file"
            for path, hash in entries do
                ensure (hash64 hash) "Invalid manifest digest"
                let bytes = bundle.Files |> Map.tryFind path |> Option.defaultWith (fun () -> reject "Missing backup content")
                ensure (sha256 bytes = hash) "Backup content checksum mismatch"
            let account = parseRecords "account.json" sequence None bundle.Files.["account.json"]
            let items =
                projects |> List.map (fun name ->
                    { ProjectId = name
                      Records = parseRecords (projectPath name) sequence (Some name) bundle.Files.[projectPath name] })
            let snapshot =
                { CommittedSequence = sequence
                  AccountRecords = account
                  Projects = items }
            validate snapshot
            ensure (sources = sortedSources snapshot) "Manifest Git provenance mismatch"
            let canonical = produce snapshot
            ensure (canonical.Files.Count = bundle.Files.Count
                    && canonical.Files |> Map.forall (fun key value ->
                        match bundle.Files.TryFind key with
                        | Some bytes -> value.AsSpan().SequenceEqual(bytes)
                        | None -> false)) "Noncanonical or manipulated backup"
            Ok snapshot
        with error -> Error(describeFailure error)
