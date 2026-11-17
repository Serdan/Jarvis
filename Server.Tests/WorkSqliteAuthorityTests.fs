module WorkSqliteAuthorityTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open Server

let private withStore f =
    let directory = Path.Combine(Path.GetTempPath(), "jarvis-work-sqlite-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    let databasePath = Path.Combine(directory, "work-authority.sqlite")
    try
        let store = WorkSqliteAuthority(databasePath)
        f store databasePath
    finally
        Directory.Delete(directory, true)

let private ok = function
    | Ok value -> value
    | Error failure -> Assert.Fail($"Unexpected Work SQLite failure: {failure}"); Unchecked.defaultof<_>

let private key project kind id =
    { ProjectId = project
      Kind = kind
      EntityId = id }

let private value json =
    { BodyJson = json
      GraphVersion = 1L
      StageGeneration = 1L
      ClaimGeneration = 0L
      StopGeneration = 0L
      RoleVersion = 1L
      SourceSha = None
      ReviewSha = None
      IntegrationSha = None
      ExternalEffect = WorkSqliteEffect.NoneRecorded }

let private write key version body =
    { Key = key; ExpectedVersion = version; Value = body }

let private command records capacities =
    { OperationId = Guid.NewGuid()
      Guards = []
      Records = records
      Capacities = capacities }

let private account accountId version maximum delta =
    { AccountId = accountId
      ExpectedVersion = version
      Maximum = maximum
      ReservedDelta = delta }

[<Test>]
let constructor_does_not_create_database () =
    withStore (fun _ path ->
        Assert.That(File.Exists path, Is.False)
    )

[<Test>]
let restart_preserves_sequence_versions_and_frozen_evidence () =
    withStore (fun store path ->
        store.Initialize() |> ok |> fun x -> Assert.That(x, Is.EqualTo 1)
        store.Initialize() |> ok |> fun x -> Assert.That(x, Is.EqualTo 1)
        let stage = key "project-1" WorkSqliteRecordKind.Stage "stage-1"
        let verified =
            { value """{"status":"Blocked","credentialVerifier":"sha256-only"}""" with
                GraphVersion = 4L
                StageGeneration = 3L
                ClaimGeneration = 2L
                StopGeneration = 1L
                RoleVersion = 7L
                SourceSha = Some(String.replicate 40 "a")
                ReviewSha = Some(String.replicate 40 "b")
                IntegrationSha = Some(String.replicate 40 "c")
                ExternalEffect = WorkSqliteEffect.Unknown }
        let initial =
            command [ write stage None verified ]
                    [ account "global" None (Some 2) 0 ]
        let receipt = store.Commit initial |> ok
        Assert.That(receipt.Sequence, Is.EqualTo 1L)
        let restarted = WorkSqliteAuthority(path)
        Assert.That(restarted.CurrentSequence() |> ok, Is.EqualTo 1L)
        let persisted = restarted.ReadRecord stage |> ok |> Option.get
        Assert.That(persisted.Version, Is.EqualTo 1L)
        Assert.That(persisted.Value, Is.EqualTo verified)
        let capacity = restarted.ReadCapacity "global" |> ok |> Option.get
        Assert.That(capacity.Maximum, Is.EqualTo 2)
        Assert.That(capacity.Reserved, Is.EqualTo 0)
        Assert.That(capacity.Version, Is.EqualTo 1L)
    )

[<Test>]
let replay_precedes_stale_cas_and_rejects_different_payload () =
    withStore (fun store _ ->
        store.Initialize() |> ok |> ignore
        let stage = key "project-a" WorkSqliteRecordKind.Stage "implementation"
        let initial = command [ write stage None (value """{"state":"Available"}""") ] []
        let first = store.Commit initial |> ok
        let replay = store.Commit initial |> ok
        Assert.That(replay.Replayed, Is.True)
        Assert.That(replay.Sequence, Is.EqualTo first.Sequence)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 1L)
        let reuse = { initial with Records = [ write stage None (value """{"state":"Changed"}""") ] }
        match store.Commit reuse with
        | Error(WorkSqliteFailure.OperationIdReused id) ->
            Assert.That(id, Is.EqualTo initial.OperationId)
        | other -> Assert.Fail($"Expected operation ID collision: {other}")
        Assert.That((store.ReadRecord stage |> ok |> Option.get).Version, Is.EqualTo 1L)
    )

[<Test>]
let stale_cas_rolls_back_prior_writes_and_keeps_sequence () =
    withStore (fun store _ ->
        store.Initialize() |> ok |> ignore
        let a = key "project-a" WorkSqliteRecordKind.Stage "a"
        let b = key "project-a" WorkSqliteRecordKind.Stage "b"
        command [ write a None (value """{"state":"Available"}""")
                  write b None (value """{"state":"Available"}""") ] []
        |> store.Commit |> ok |> ignore
        let invalid = command
                        [ write a (Some 1L) (value """{"state":"Claimed"}""")
                          write b (Some 999L) (value """{"state":"Claimed"}""") ] []
        match store.Commit invalid with
        | Error(WorkSqliteFailure.StaleVersion(entity, 999L, 1L)) ->
            Assert.That(entity, Does.Contain "project-a/stage/b")
        | other -> Assert.Fail($"Expected typed stale-version result: {other}")
        let existing = store.ReadRecord a |> ok |> Option.get
        Assert.That(existing.Version, Is.EqualTo 1L)
        Assert.That(existing.Value.BodyJson, Is.EqualTo """{"state":"Available"}""")
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 1L)
    )

[<Test>]
let competing_connections_select_one_claim_winner () =
    withStore (fun store path ->
        store.Initialize() |> ok |> ignore
        let stage = key "project-a" WorkSqliteRecordKind.Stage "one"
        command [ write stage None (value """{"state":"Available"}""") ]
                [ account "global" None (Some 1) 0 ]
        |> store.Commit |> ok |> ignore
        use start = new ManualResetEventSlim(false)
        let attempt claimId =
            Task.Run(fun () ->
                let client = WorkSqliteAuthority(path)
                start.Wait()
                client.Commit(
                    command [ write stage (Some 1L)
                                { value ($"""{{"state":"Claimed","launch":"{claimId}"}}""") with
                                    ClaimGeneration = 1L } ]
                            [ account "global" (Some 1L) None 1 ]))
        let first = attempt "a"
        let second = attempt "b"
        start.Set()
        let outcomes = Task.WhenAll([| first; second |]).GetAwaiter().GetResult()
        Assert.That(outcomes |> Array.filter Result.isOk |> Array.length, Is.EqualTo 1)
        let losers = outcomes |> Array.filter Result.isError
        Assert.That(losers.Length, Is.EqualTo 1)
        match losers[0] with
        | Error(WorkSqliteFailure.StaleVersion _) -> ()
        | other -> Assert.Fail($"Expected stale CAS loser: {other}")
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
        Assert.That((store.ReadRecord stage |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That((store.ReadCapacity "global" |> ok |> Option.get).Reserved, Is.EqualTo 1)
    )

[<Test>]
let two_projects_and_account_capacity_commit_atomically () =
    withStore (fun store _ ->
        store.Initialize() |> ok |> ignore
        let a = key "project-a" WorkSqliteRecordKind.Stage "implementation"
        let b = key "project-b" WorkSqliteRecordKind.Stage "review"
        command [ write a None (value """{"state":"Available"}""")
                  write b None (value """{"state":"Available"}""") ]
                [ account "shared-capacity" None (Some 2) 0 ]
        |> store.Commit |> ok |> ignore
        let claim =
            command [ write a (Some 1L)
                        { value """{"state":"Claimed"}""" with ClaimGeneration = 1L }
                      write b (Some 1L)
                        { value """{"state":"Claimed"}""" with ClaimGeneration = 1L } ]
                    [ account "shared-capacity" (Some 1L) None 2 ]
        let applied = store.Commit claim |> ok
        Assert.That(applied.Sequence, Is.EqualTo 2L)
        Assert.That((store.ReadRecord a |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That((store.ReadRecord b |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That((store.ReadCapacity "shared-capacity" |> ok |> Option.get).Reserved, Is.EqualTo 2)
        let denied =
            command [ write a (Some 2L) (value """{"state":"Overcommitted"}""") ]
                    [ account "shared-capacity" (Some 2L) None 1 ]
        match store.Commit denied with
        | Error(WorkSqliteFailure.CapacityExceeded "shared-capacity") -> ()
        | other -> Assert.Fail($"Expected bounded account capacity: {other}")
        Assert.That((store.ReadRecord a |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
    )

[<Test>]
let foreign_telemetry_db_and_future_schema_are_refused () =
    withStore (fun store path ->
        use foreign = new SqliteConnection($"Data Source={path}")
        foreign.Open()
        use create = foreign.CreateCommand()
        create.CommandText <- "CREATE TABLE feedback (id TEXT PRIMARY KEY);"
        create.ExecuteNonQuery() |> ignore
        foreign.Close()
        match store.Initialize() with
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail($"Expected foreign database refusal: {other}")
        use read = new SqliteConnection($"Data Source={path}")
        read.Open()
        use check = read.CreateCommand()
        check.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE name='feedback';"
        Assert.That(Convert.ToInt32(check.ExecuteScalar()), Is.EqualTo 1)
    )
    withStore (fun store path ->
        store.Initialize() |> ok |> ignore
        use connection = new SqliteConnection($"Data Source={path}")
        connection.Open()
        use migrate = connection.CreateCommand()
        migrate.CommandText <- "PRAGMA user_version=99;"
        migrate.ExecuteNonQuery() |> ignore
        connection.Close()
        match store.CurrentSequence() with
        | Error(WorkSqliteFailure.UnsupportedSchemaVersion 99) -> ()
        | other -> Assert.Fail($"Expected refusal of future schema: {other}")
    )

[<Test>]
let unopened_storage_and_unverified_payloads_fail_closed () =
    withStore (fun store _ ->
        let stage = key "a" WorkSqliteRecordKind.Stage "s"
        let valid = command [ write stage None (value """{"state":"Available"}""") ] []
        match store.Commit valid with
        | Error(WorkSqliteFailure.StorageUnavailable _) -> ()
        | Error WorkSqliteFailure.ForeignDatabase -> ()
        | other -> Assert.Fail($"Expected unopened store refusal: {other}")
        store.Initialize() |> ok |> ignore
        let invalidSha = { value """{"x":1}""" with SourceSha = Some "unverified" }
        match store.Commit(command [ write stage None invalidSha ] []) with
        | Error(WorkSqliteFailure.InvalidCommand _) -> ()
        | other -> Assert.Fail($"Expected invalid source proof refusal: {other}")
        match store.Commit(command [ write stage None (value "not-json") ] []) with
        | Error(WorkSqliteFailure.InvalidCommand _) -> ()
        | other -> Assert.Fail($"Expected invalid JSON refusal: {other}")
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 0L)
    )

let private witness key version graph stage claim stop role source =
    { Key = key
      ExpectedVersion = version
      GraphVersion = graph
      StageGeneration = stage
      ClaimGeneration = claim
      StopGeneration = stop
      RoleVersion = role
      SourceSha = source }

[<Test>]
let cross_record_graph_witness_fences_unrelated_stage_and_capacity () =
    withStore (fun store _ ->
        store.Initialize() |> ok |> ignore
        let graph = key "upstream" WorkSqliteRecordKind.Project "graph"
        let downstream = key "downstream" WorkSqliteRecordKind.Stage "implementation"
        command [ write graph None (value """{"graph":"accepted"}""")
                  write downstream None (value """{"state":"Available"}""") ]
                [ account "global" None (Some 1) 0 ]
        |> store.Commit |> ok |> ignore
        let graphGuard = witness graph 1L (Some 1L) None None None None None
        let claim = 
            { command [ write downstream (Some 1L) (value """{"state":"Claimed"}""") ]
                      [ account "global" (Some 1L) None 1 ] with
                Guards = [ graphGuard ] }
        // The graph changed on a DIFFERENT row and the stage remained version 1.
        command [ write graph (Some 1L)
                    { value """{"graph":"changed"}""" with GraphVersion = 2L } ] []
        |> store.Commit |> ok |> ignore
        match store.Commit claim with
        | Error(WorkSqliteFailure.StaleVersion(entity, 1L, 2L)) ->
            Assert.That(entity, Does.Contain "upstream/project/graph")
        | other -> Assert.Fail($"Expected independent graph CAS witness: {other}")
        Assert.That((store.ReadRecord downstream |> ok |> Option.get).Version, Is.EqualTo 1L)
        Assert.That((store.ReadCapacity "global" |> ok |> Option.get).Reserved, Is.EqualTo 0)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
    )

[<Test>]
let source_role_stop_and_generation_guards_are_typed_and_replayable () =
    withStore (fun store _ ->
        store.Initialize() |> ok |> ignore
        let stage = key "project-a" WorkSqliteRecordKind.Stage "implementation"
        let sha = String.replicate 40 "a"
        let initial =
            { value """{"state":"Available"}""" with
                SourceSha = Some sha
                StageGeneration = 5L
                ClaimGeneration = 3L
                StopGeneration = 2L
                RoleVersion = 4L
                GraphVersion = 7L }
        command [ write stage None initial ] [] |> store.Commit |> ok |> ignore
        let guard = witness stage 1L (Some 7L) (Some 5L) (Some 3L) (Some 2L) (Some 4L) (Some sha)
        let expectMismatch (field: string) (bad: WorkSqliteRecordGuard) =
            let request = { command [ write stage (Some 1L) initial ] [] with Guards = [ bad ] }
            match store.Commit request with
            | Error(WorkSqliteFailure.StaleEvidence(entity, mismatch)) ->
                Assert.That(entity, Does.Contain "project-a/stage/implementation")
                Assert.That(mismatch, Is.EqualTo field)
            | other -> Assert.Fail($"Expected typed {field} guard rejection: {other}")
        expectMismatch "source-sha" { guard with SourceSha = Some(String.replicate 40 "b") }
        expectMismatch "role-version" { guard with RoleVersion = Some 99L }
        expectMismatch "stop-generation" { guard with StopGeneration = Some 99L }
        expectMismatch "stage-generation" { guard with StageGeneration = Some 99L }
        expectMismatch "claim-generation" { guard with ClaimGeneration = Some 99L }
        expectMismatch "graph" { guard with GraphVersion = Some 99L }
        let claim = 
            { command [ write stage (Some 1L)
                          { initial with BodyJson = """{"state":"Claimed"}"""
                                         ClaimGeneration = 4L } ] [] with
                Guards = [ guard ] }
        let applied = store.Commit claim |> ok
        let replay = store.Commit claim |> ok
        Assert.That(applied.Sequence, Is.EqualTo replay.Sequence)
        Assert.That(replay.Replayed, Is.True)
        Assert.That((store.ReadRecord stage |> ok |> Option.get).Version, Is.EqualTo 2L)
        Assert.That(store.CurrentSequence() |> ok, Is.EqualTo 2L)
    )
