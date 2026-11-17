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
