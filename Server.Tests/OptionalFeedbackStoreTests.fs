module OptionalFeedbackStoreTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open Server.Services

let private tempDirectory () =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-stateless-server-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(path) |> ignore
    path

[<Test>]
let missingConfigurationAndUnusableDatabaseDoNotBlockInitializationOrRequests () = task {
    use disabled = new OptionalFeedbackStore("")
    let started = Stopwatch.StartNew()
    let! missing = disabled.TryExecute(fun _ -> "should never execute")
    Assert.That(missing.IsError, Is.True)
    Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.))

    let directory = tempDirectory()
    try
        // Real corrupt SQLite bytes: neither startup nor an authenticated
        // request is allowed to synchronously open/repair this file.
        let corruptPath = Path.Combine(directory, "corrupt.db")
        let bytes = [| for i in 0 .. 255 -> byte i |]
        File.WriteAllBytes(corruptPath, bytes)
        let unchanged = File.ReadAllBytes(corruptPath)
        use corrupt = new OptionalFeedbackStore(corruptPath, timeout = TimeSpan.FromMilliseconds 100.)
        let! result = corrupt.TryExecute(fun store -> store.DatabasePath)
        Assert.That(result.IsError, Is.True)
        Assert.That(File.ReadAllBytes(corruptPath) = unchanged, Is.True,
                    "Unavailable diagnostics must never delete or replace existing data.")

        // Simulates a write/open failure (e.g. read-only disk or disk full)
        // without needing privileged filesystem manipulation.
        let pathIsFile = Path.Combine(directory, "not-a-directory")
        File.WriteAllText(pathIsFile, "occupied")
        use unwritable = new OptionalFeedbackStore(Path.Combine(pathIsFile, "feedback.db"))
        let! unavailable = unwritable.TryExecute(fun store -> store.DatabasePath)
        Assert.That(unavailable.IsError, Is.True)
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let lockedOrHungTelemetryIsSingleFlightBoundedAndDoesNotBlockTwoClientsOrRestart () = task {
    let directory = tempDirectory()
    let dbPath = Path.Combine(directory, "feedback.db")
    use gate = new ManualResetEventSlim(false)
    try
        use clientA = new OptionalFeedbackStore(dbPath, timeout = TimeSpan.FromMilliseconds 70.)
        use clientB = new OptionalFeedbackStore(dbPath, timeout = TimeSpan.FromSeconds 2.)
        let! warmA = clientA.TryExecute(fun _ -> "A")
        let! warmB = clientB.TryExecute(fun _ -> "B")
        Assert.That(warmA, Is.EqualTo((Ok "A" : Result<string,string>)))
        Assert.That(warmB, Is.EqualTo((Ok "B" : Result<string,string>)))

        // One simulated SQLite hang must consume at most ONE background worker.
        let started = Stopwatch.StartNew()
        let! stuck = clientA.TryExecute(fun _ -> gate.Wait(TimeSpan.FromSeconds 5.) |> ignore)
        Assert.That(stuck.IsError, Is.True)
        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.))
        let droppedAt = Stopwatch.StartNew()
        clientA.TryPost(fun _ -> failwith "must not enqueue behind stuck SQLite")
        let! dropped = clientA.TryExecute(fun _ -> "unexpected")
        Assert.That(dropped.IsError, Is.True)
        Assert.That(droppedAt.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds 200.))

        // This second authenticated client is independent of the stalled
        // telemetry worker and can still access its own bounded diagnostics.
        let! other = clientB.TryExecute(fun _ -> "other-client")
        Assert.That(other, Is.EqualTo((Ok "other-client" : Result<string,string>)))
        gate.Set()
        do! Task.Delay(100)

        // Replacing the disposable server instance does not need any
        // continuity marker, migration or backup for noncanonical telemetry.
        use restarted = new OptionalFeedbackStore(dbPath, timeout = TimeSpan.FromSeconds 2.)
        let! fresh = restarted.TryExecute(fun _ -> "reconnected")
        Assert.That(fresh, Is.EqualTo((Ok "reconnected" : Result<string,string>)))
    finally
        gate.Set()
        Directory.Delete(directory, true)
}

[<Test>]
let diagnosticWriteFailureIsNotReportedAsSuccessAndFutureOperationsCanRecover () = task {
    let directory = tempDirectory()
    try
        use host = new OptionalFeedbackStore(Path.Combine(directory, "feedback.db"))
        let! failed = host.TryExecute(fun _ -> (raise (IOException("no space left")) : unit))
        Assert.That(failed.IsError, Is.True)
        let! recovered = host.TryExecute(fun _ -> 42)
        Assert.That(recovered, Is.EqualTo((Ok 42 : Result<int,string>)))
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let realSQLiteWriterLockIsOptionalAndCannotGateRequestThread () = task {
    let directory = tempDirectory()
    let path = Path.Combine(directory, "locked.db")
    try
        use host = new OptionalFeedbackStore(path, timeout = TimeSpan.FromMilliseconds 300.)
        let! ready = host.TryExecute(fun db -> db.DatabasePath)
        Assert.That(ready.IsOk, Is.True)
        use writer = new SqliteConnection($"Data Source={path};Cache=Shared")
        writer.Open()
        use transaction = writer.BeginTransaction()
        use command = writer.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <-
            """INSERT INTO operations(id,created_at,user_id,tool_name,server_version)
               VALUES('writer-lock','2026-11-15T00:00:00Z','alice','ListProjects','11');"""
        command.ExecuteNonQuery() |> ignore
        let elapsed = Stopwatch.StartNew()
        let! blocked = host.TryExecute(fun db ->
            db.BeginOperation("alice", "ListProjects", None, "11", None, None))
        Assert.That(blocked.IsError, Is.True,
                    "Telemetry operation must fail/timeout under a real concurrent writer lock.")
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.),
                    "SQLite writer lock must not delay the MCP request.")
        transaction.Rollback()
        do! Task.Delay(1200)
        let! recovered = host.TryExecute(fun db ->
            db.BeginOperation("alice", "ListProjects", None, "11", None, None))
        Assert.That(recovered.IsOk, Is.True, "Telemetry can resume after the lock clears.")
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let existingOversizedDatabaseOrWalIsQuarantinedWithoutDestructiveCleanup () = task {
    let directory = tempDirectory()
    try
        let path = Path.Combine(directory, "oversized.db")
        let prior = Array.init (512 * 1024) (fun i -> byte (i % 251))
        File.WriteAllBytes(path, prior)
        use oversized =
            new OptionalFeedbackStore(path, maxDiskBytes = 256L * 1024L,
                                      timeout = TimeSpan.FromSeconds(1.))
        let! blocked = oversized.TryExecute(fun db -> db.DatabasePath)
        match blocked with
        | Error reason -> Assert.That(reason, Does.Contain("TelemetryQuotaExceeded"))
        | Ok _ -> Assert.Fail("Oversized pre-existing diagnostics must not be opened or mutated.")
        Assert.That(File.ReadAllBytes(path) = prior, Is.True,
                    "Quota exhaustion must not delete or rewrite legacy feedback.")

        // Also fail closed when the main DB alone exceeds its page ceiling,
        // even if the aggregate 1 MiB high-water has not yet been reached.
        let aboveMainLimit = Path.Combine(directory, "over-main-only.db")
        let oldMain = Array.create (600 * 1024) 19uy
        File.WriteAllBytes(aboveMainLimit, oldMain)
        use mainLimit =
            new OptionalFeedbackStore(aboveMainLimit, maxDiskBytes = 1024L * 1024L,
                                      timeout = TimeSpan.FromSeconds(1.))
        let! mainBlocked = mainLimit.TryExecute(fun db -> db.DatabasePath)
        match mainBlocked with
        | Error reason -> Assert.That(reason, Does.Contain("TelemetryQuotaExceeded"))
        | Ok _ -> Assert.Fail("An already oversized main database must not gain new pages.")
        Assert.That(File.ReadAllBytes(aboveMainLimit) = oldMain, Is.True)

        let fresh = Path.Combine(directory, "wal-present.db")
        let walPath = fresh + "-wal"
        let oldWal = Array.create (384 * 1024) 7uy
        File.WriteAllBytes(walPath, oldWal)
        use oversizedWal =
            new OptionalFeedbackStore(fresh, maxDiskBytes = 256L * 1024L,
                                      timeout = TimeSpan.FromSeconds(1.))
        let! walBlocked = oversizedWal.TryExecute(fun db -> db.DatabasePath)
        Assert.That(walBlocked.IsError, Is.True)
        Assert.That(File.Exists(fresh), Is.False,
                    "An oversized WAL sidecar must be rejected before opening SQLite.")
        Assert.That(File.ReadAllBytes(walPath) = oldWal, Is.True)
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let sqlitePageBudgetRejectsUnboundedFeedbackWritesWithoutGrowingMainFile () = task {
    let directory = tempDirectory()
    try
        let path = Path.Combine(directory, "bounded.db")
        use diagnostics =
            new OptionalFeedbackStore(path, maxDiskBytes = 1024L * 1024L,
                                      timeout = TimeSpan.FromSeconds(2.))
        let input =
            { ProjectName = Some "Jarvis"; ToolName = Some "ListProjects"
              Category = "Other"; Severity = "Info"; Summary = "bounded report"
              Details = Some(String('x', 8192)); Workaround = None; OperationId = None }
        let mutable rejected = false
        let mutable accepted = 0
        for _ in 1 .. 300 do
            let! outcome = diagnostics.TryExecute(fun db -> db.AddFeedback("alice", input, None))
            match outcome with
            | Ok _ -> accepted <- accepted + 1
            | Error _ -> rejected <- true
        Assert.That(accepted, Is.GreaterThan(0), "The finite diagnostics budget should allow small feedback.")
        Assert.That(rejected, Is.True, "A stream of optional feedback must eventually hit a finite quota.")
        Assert.That(FileInfo(path).Length, Is.LessThanOrEqualTo(512L * 1024L),
                    "SQLite max_page_count must limit even repeated feedback inserts.")
    finally
        Directory.Delete(directory, true)
}

[<Test>]
let expiredTelemetryPruningDeletesAtMost128RowsPerTablePerPass () =
    let directory = tempDirectory()
    try
        let path = Path.Combine(directory, "prune.db")
        use store = new FeedbackStore(path, runPruneTimer = false)
        use connection = new SqliteConnection($"Data Source={path}")
        connection.Open()
        use transaction = connection.BeginTransaction()
        for i in 1 .. 300 do
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <-
                $"""
INSERT INTO operations(id,created_at,user_id,tool_name,server_version)
VALUES('expired-{i}', '2020-01-01T00:00:00.0000000+00:00','alice','ListProjects','1');
INSERT INTO connection_events(created_at,user_id,event_type,connection_id)
VALUES('2020-01-01T00:00:00.0000000+00:00','alice','Disconnect','expired-{i}');
"""
            command.ExecuteNonQuery() |> ignore
        transaction.Commit()
        let rowCount table =
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT COUNT(*) FROM {table};"
            Convert.ToInt32(command.ExecuteScalar())
        Assert.That(rowCount "operations", Is.EqualTo(300))
        for remaining in [ 172; 44; 0 ] do
            store.PruneTelemetry(DateTimeOffset.UtcNow)
            Assert.That(rowCount "operations", Is.EqualTo(remaining))
            Assert.That(rowCount "connection_events", Is.EqualTo(remaining))
    finally
        Directory.Delete(directory, true)
