module WorkSqliteDomainTests

open System
open System.IO
open Microsoft.Data.Sqlite
open NUnit.Framework
open Server

// Physical B schema only: not a claim of authenticated original Git ingestion.
let private withAuthority f =
    let folder = Path.Combine(Path.GetTempPath(), "jarvis-b-provenance-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    let path = Path.Combine(folder, "authority.sqlite")
    try f (WorkSqliteAuthority(path)) path
    finally Directory.Delete(folder, true)

let private ok = function
    | Ok x -> x
    | Error error -> Assert.Fail(sprintf "Unexpected SQLite failure: %A" error); Unchecked.defaultof<_>

let private sha = String.replicate 40 "a"
let private digest = String.replicate 64 "b"
let private receiptId = Guid.Parse "94ef0895-0a90-438c-8298-74c1c6d06c21"
let private key project kind id : WorkSqliteRecordKey =
    { ProjectId = project; Kind = kind; EntityId = id }
let private value json source : WorkSqliteRecordValue =
    { BodyJson = json; GraphVersion = 3L; StageGeneration = 2L
      ClaimGeneration = 1L; StopGeneration = 4L; RoleVersion = 5L
      SourceSha = source
      ReviewSha = Some(String.replicate 40 "c")
      IntegrationSha = Some(String.replicate 40 "d")
      ExternalEffect = WorkSqliteEffect.Unknown }
let private mutation project kind id json source : WorkSqliteRecordMutation =
    { Key = key project kind id; ExpectedVersion = None; Value = value json source }
let private provenance source : WorkSqliteSourceProvenance =
    { RepositoryId = "original-repository"
      SourceRef = "refs/heads/main"
      SourceCommit = source
      VerificationReceiptId = receiptId
      EvidenceSha256 = digest }
let private singleRecord project kind id json =
    mutation project kind id json (Some sha)

[<Test>]
let v1_to_v2_never_fabricates_original_source_evidence () =
    withAuthority (fun store path ->
        Assert.That(store.Initialize() |> ok, Is.EqualTo 1)
        let original = singleRecord "project" WorkSqliteRecordKind.Stage "old" """{"state":"Available"}"""
        let command =
            { OperationId = Guid.NewGuid(); Guards = []; Records = [ original ]; Capacities = [] }
        let applied = store.Commit command |> ok
        Assert.That(applied.Sequence, Is.EqualTo 1L)
        Assert.That(store.InitializeProvenanceV2() |> ok, Is.EqualTo 2)
        Assert.That(store.InitializeProvenanceV2() |> ok, Is.EqualTo 2)
        Assert.That((WorkSqliteAuthority(path)).Initialize() |> ok, Is.EqualTo 2)
        let persisted = store.ReadRecord original.Key |> ok |> Option.get
        Assert.That(persisted.Version, Is.EqualTo 1L)
        Assert.That(persisted.Value, Is.EqualTo original.Value)
        match store.ReadSourceProvenance original.Key with
        | Error(WorkSqliteFailure.InvalidCommand _) -> ()
        | other -> Assert.Fail(sprintf "Pre-v2 SHA cannot be promoted to repo/ref proof: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 1L))

[<Test>]
let two_projects_eight_kinds_survive_restart_and_exact_receipt_replay () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        let kinds =
            [ WorkSqliteRecordKind.Project; WorkSqliteRecordKind.Work
              WorkSqliteRecordKind.Stage; WorkSqliteRecordKind.Launch
              WorkSqliteRecordKind.Role; WorkSqliteRecordKind.Review
              WorkSqliteRecordKind.Integration; WorkSqliteRecordKind.ExternalEffect ]
        let records =
            [ for project in [ "alpha"; "beta" ] do
                for index, kind in List.indexed kinds do
                    let m = singleRecord project kind ("entity" + string index) """{"schemaVersion":1}"""
                    yield { Mutation = m; Source = Some(provenance sha) } ]
        let id = Guid.NewGuid()
        let capacities =
            [ { AccountId = "account-shared"; ExpectedVersion = None
                Maximum = Some 2; ReservedDelta = 1 } ]
        let applied = store.CommitProvenanced(id, [], records, capacities) |> ok
        Assert.That(applied.Sequence, Is.EqualTo 1L)
        let restarted = WorkSqliteAuthority(path)
        Assert.That(restarted.CurrentSequence() |> ok, Is.EqualTo 1L)
        Assert.That((restarted.ReadCapacity "account-shared" |> ok |> Option.get).Reserved, Is.EqualTo 1)
        for item in records do
            let record = restarted.ReadRecord item.Mutation.Key |> ok |> Option.get
            Assert.That(record.Value, Is.EqualTo item.Mutation.Value)
            Assert.That(restarted.ReadSourceProvenance item.Mutation.Key |> ok, Is.EqualTo item.Source)
        let replay = restarted.CommitProvenanced(id, [], records, capacities) |> ok
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.Sequence, Is.EqualTo 1L)
        let changed = { records.Head with Source = Some { provenance sha with SourceRef = "refs/heads/other" } }
        match restarted.CommitProvenanced(id, [], changed :: records.Tail, capacities) with
        | Error(WorkSqliteFailure.OperationIdReused _) -> ()
        | other -> Assert.Fail(sprintf "Source change must alter receipt fingerprint: %A" other))

[<Test>]
let v2_fails_closed_on_raw_writes_missing_or_mismatched_provenance () =
    withAuthority (fun store _ ->
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        let m = singleRecord "project" WorkSqliteRecordKind.Stage "stage" """{"state":"Available"}"""
        let raw = { OperationId = Guid.NewGuid(); Guards = []; Records = [ m ]; Capacities = [] }
        match store.Commit raw with
        | Error(WorkSqliteFailure.InvalidCommand _) -> ()
        | other -> Assert.Fail(sprintf "Raw writer must fail in v2: %A" other)
        let attempt evidence = store.CommitProvenanced(Guid.NewGuid(), [], [ { Mutation = m; Source = evidence } ], [])
        for candidate in
            [ None
              Some { provenance sha with SourceCommit = String.replicate 40 "f" }
              Some { provenance sha with SourceRef = "refs/heads/a..b" }
              Some { provenance sha with SourceRef = "not-a-ref" }
              Some { provenance sha with VerificationReceiptId = Guid.Empty }
              Some { provenance sha with EvidenceSha256 = "not-a-digest" } ] do
            match attempt candidate with
            | Error(WorkSqliteFailure.InvalidCommand _) -> ()
            | other -> Assert.Fail(sprintf "Unverified original-source payload accepted: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 0L)
        Assert.That(store.ReadRecord m.Key |> ok, Is.EqualTo None))

[<Test>]
let partial_v1_upgrade_is_rejected_without_advancing_version () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use cmd = connection.CreateCommand()
        cmd.CommandText <- "ALTER TABLE work_records ADD COLUMN source_repository_id TEXT;"
        cmd.ExecuteNonQuery() |> ignore
        match store.InitializeProvenanceV2() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Partially altered authority was accepted: %A" other)
        use ver = connection.CreateCommand()
        ver.CommandText <- "PRAGMA user_version;"
        Assert.That(Convert.ToInt32(ver.ExecuteScalar()), Is.EqualTo 1))

[<Test>]
let v2_rejects_root_and_nested_duplicate_body_properties () =
    withAuthority (fun store _ ->
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        for json in
            [ """{"state":"Open","state":"Blocked"}"""
              """{"outer":{"queue":"a","queue":"b"}}"""
              """{"outer":[{"x":1,"x":2}]}""" ] do
            let m = singleRecord "alpha" WorkSqliteRecordKind.Stage "stage" json
            match store.CommitProvenanced(Guid.NewGuid(), [],
                   [ { Mutation = m; Source = Some(provenance sha) } ], []) with
            | Error(WorkSqliteFailure.InvalidCommand _) -> ()
            | other -> Assert.Fail(sprintf "Duplicate JSON property passed v2 guard: %A" other)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 0L))
[<Test>]
let v2_source_debinding_clears_all_columns_on_update_and_survives_restart_replay () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        let initial = singleRecord "alpha" WorkSqliteRecordKind.Stage "stage" """{"state":"Ready"}"""
        let first = { Mutation = initial; Source = Some(provenance sha) }
        store.CommitProvenanced(Guid.NewGuid(), [], [ first ], []) |> ok |> ignore
        let revised =
            { Mutation =
                { initial with
                    ExpectedVersion = Some 1L
                    Value =
                        { initial.Value with
                            BodyJson = """{"state":"Removed"}"""
                            SourceSha = None } }
              Source = None }
        let updateId = Guid.NewGuid()
        let updated = store.CommitProvenanced(updateId, [], [ revised ], []) |> ok
        Assert.That(updated.Sequence, Is.EqualTo 2L)
        let restarted = WorkSqliteAuthority(path)
        Assert.That(restarted.Initialize() |> ok, Is.EqualTo 2)
        let persisted = restarted.ReadRecord initial.Key |> ok |> Option.get
        Assert.That(persisted.Version, Is.EqualTo 2L)
        Assert.That(persisted.Value.SourceSha, Is.EqualTo None)
        Assert.That(restarted.ReadSourceProvenance initial.Key |> ok, Is.EqualTo None)
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use query = connection.CreateCommand()
        query.CommandText <-
            """SELECT source_sha, source_repository_id, source_ref,
                      source_verification_receipt_id, source_evidence_sha256
               FROM work_records WHERE project_id='alpha' AND record_kind='stage' AND entity_id='stage';"""
        use reader = query.ExecuteReader()
        Assert.That(reader.Read(), Is.True)
        for column in 0 .. 4 do
            Assert.That(reader.IsDBNull column, Is.True, sprintf "Stale provenance column %d" column)
        reader.Close()
        let replay = restarted.CommitProvenanced(updateId, [], [ revised ], []) |> ok
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.Sequence, Is.EqualTo 2L)
        Assert.That(restarted.CurrentSequence() |> ok, Is.EqualTo 2L)
        match restarted.CommitProvenanced(updateId, [], [ first ], []) with
        | Error(WorkSqliteFailure.OperationIdReused _) -> ()
        | other -> Assert.Fail(sprintf "Changed replay must be rejected: %A" other))

[<Test>]
let v1_same_count_wrong_named_column_rejected_without_upgrade_or_receipt_change () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        let initial = singleRecord "alpha" WorkSqliteRecordKind.Stage "stage" "{}"
        let receiptId = Guid.NewGuid()
        store.Commit { OperationId = receiptId; Guards = []; Records = [ initial ]; Capacities = [] }
        |> ok |> ignore
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use change = connection.CreateCommand()
        change.CommandText <- "ALTER TABLE work_records RENAME COLUMN source_sha TO unrelated_sha;"
        change.ExecuteNonQuery() |> ignore
        match (WorkSqliteAuthority(path)).InitializeProvenanceV2() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Same-count invalid v1 layout was upgraded: %A" other)
        match (WorkSqliteAuthority(path)).Initialize() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Same-count invalid v1 layout was initialized: %A" other)
        use verify = connection.CreateCommand()
        verify.CommandText <-
            "SELECT (SELECT schema_version FROM work_meta WHERE id=1), (SELECT last_sequence FROM work_meta WHERE id=1);"
        use reader = verify.ExecuteReader()
        Assert.That(reader.Read(), Is.True)
        Assert.That(reader.GetInt64 0, Is.EqualTo 1L)
        Assert.That(reader.GetInt64 1, Is.EqualTo 1L)
        reader.Close()
        use version = connection.CreateCommand()
        version.CommandText <- "PRAGMA user_version;"
        Assert.That(Convert.ToInt32(version.ExecuteScalar()), Is.EqualTo 1)
        Assert.That(store.ReadReceipt receiptId |> ok |> Option.isSome, Is.True))

[<Test>]
let v2_same_count_fabricated_provenance_layout_rejected_after_restart () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use fabricate = connection.CreateCommand()
        fabricate.CommandText <-
            """ALTER TABLE work_records ADD COLUMN fake_repository TEXT;
               ALTER TABLE work_records ADD COLUMN fake_ref TEXT;
               ALTER TABLE work_records ADD COLUMN fake_receipt TEXT;
               ALTER TABLE work_records ADD COLUMN fake_evidence TEXT;
               UPDATE work_meta SET schema_version=2 WHERE id=1;
               PRAGMA user_version=2;"""
        fabricate.ExecuteNonQuery() |> ignore
        let restarted = WorkSqliteAuthority(path)
        match restarted.Initialize() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Fabricated v2 layout initialized: %A" other)
        match restarted.InitializeProvenanceV2() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Fabricated v2 layout upgraded: %A" other)
        use version = connection.CreateCommand()
        version.CommandText <- "PRAGMA user_version;"
        Assert.That(Convert.ToInt32(version.ExecuteScalar()), Is.EqualTo 2)
        use sequence = connection.CreateCommand()
        sequence.CommandText <- "SELECT last_sequence FROM work_meta WHERE id=1;"
        Assert.That(Convert.ToInt64(sequence.ExecuteScalar()), Is.EqualTo 0L))

[<Test>]
let v2_missing_required_index_is_foreign_even_with_correct_columns () =
    withAuthority (fun store path ->
        store.Initialize() |> ok |> ignore
        store.InitializeProvenanceV2() |> ok |> ignore
        use connection = new SqliteConnection(sprintf "Data Source=%s" path)
        connection.Open()
        use dropIndex = connection.CreateCommand()
        dropIndex.CommandText <- "DROP INDEX work_records_stage_claim;"
        dropIndex.ExecuteNonQuery() |> ignore
        match (WorkSqliteAuthority(path)).InitializeProvenanceV2() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Malformed v2 index identity was accepted: %A" other)
        match (WorkSqliteAuthority(path)).Initialize() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail(sprintf "Malformed v2 index initialized: %A" other))
