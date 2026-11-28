module DurableFeedbackTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open NUnit.Framework
open Server.Services

let private ok = function
    | Ok value -> value
    | Error error -> failwith error

let private error = function
    | Error message -> message
    | Ok _ -> failwith "Expected failure."

let private wait (work: Task<'T>) = work.GetAwaiter().GetResult()

let private withDirectory test =
    let path = Path.Combine(Path.GetTempPath(), "jarvis-durable-feedback-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(path) |> ignore
    try test path
    finally
        SqliteConnection.ClearAllPools()
        Directory.Delete(path, true)

let private connection path =
    let db = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1")
    db.Open()
    db

let private exec (db: SqliteConnection) sql =
    use cmd = db.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

let private input summary operation =
    { ProjectName = Some "Project"; ToolName = Some "RunCommand"
      Category = "ToolFailure"; Severity = "Blocking"; Summary = summary
      Details = Some "Detailed report"; Workaround = Some "Manual workaround"; OperationId = operation }

let private newStore path =
    let store = DurableFeedbackStore(path)
    store.InitializeOffline()
    store

[<Test>]
let ``diagnostic quota and contention do not admit or reject durable feedback`` () =
    withDirectory (fun directory ->
        let diagnostic = Path.Combine(directory,"diagnostic.db")
        do
            use legacy = new FeedbackStore(diagnostic, runPruneTimer=false)
            use db = connection diagnostic
            exec db "CREATE TABLE padding(data BLOB); INSERT INTO padding VALUES(zeroblob(400000));"
        use optional = new OptionalFeedbackStore(diagnostic, maxDiskBytes=256L*1024L)
        let rejected = optional.TryExecute(fun db -> db.AddFeedback("alice",input "Old path" None,None)) |> wait |> error
        Assert.That(rejected, Does.Contain("TelemetryQuotaExceeded"))
        let durable = newStore (Path.Combine(directory,"feedback.db"))
        let created = durable.TrySubmit("alice",Guid.NewGuid(),input "Independent" (Some "unavailable-op"),None) |> wait |> ok
        Assert.That(created.OperationLinkState, Is.EqualTo("Unverified"))
        Assert.That(created.OperationId, Is.Empty)
        Assert.That(created.RequestedOperationId, Is.EqualTo("unavailable-op"))
        let entries = durable.TryList("alice",None,None,None,None,10) |> wait |> ok
        Assert.That(entries.Length, Is.EqualTo(1))
        optional.TryMaintain() |> wait |> ignore
        Assert.That((durable.TryGet("alice",created.Id) |> wait |> ok).Value.Summary, Is.EqualTo("Independent")))

[<Test>]
let ``idempotency survives restart and changes in optional evidence`` () =
    withDirectory (fun directory ->
        let path = Path.Combine(directory,"feedback.db")
        let store = newStore path
        let request = Guid.NewGuid()
        let report = input "First" (Some "operation-1")
        let created = store.TrySubmit("alice",request,report,None) |> wait |> ok
        let restarted = DurableFeedbackStore(path)
        let evidence = { UserId="alice"; OperationId="operation-1"; Success=Some false }
        let replay = restarted.TrySubmit("alice",request,report,Some evidence) |> wait |> ok
        Assert.That(replay, Is.EqualTo(created))
        Assert.That(replay.OperationId, Is.Empty)
        Assert.That(restarted.TrySubmit("alice",request,input "Different" None,None) |> wait |> error,
                    Does.Contain("FeedbackRequestConflict"))
        let other = restarted.TrySubmit("bob",request,report,Some evidence) |> wait |> ok
        Assert.That(other.Id, Is.Not.EqualTo(created.Id))
        Assert.That(other.OperationId, Is.Empty)
        Assert.That((restarted.TryList("alice",None,None,None,None,10) |> wait |> ok).Length, Is.EqualTo(1))
        Assert.That(restarted.TryGet("bob",created.Id) |> wait |> ok, Is.EqualTo(None))
        Assert.That(restarted.TryUpdate("alice",request,created.Id,"Open",None,None) |> wait |> error,
                    Does.Contain("FeedbackRequestConflict")))

[<Test>]
let ``update replay returns original result without undoing a later update`` () =
    withDirectory (fun directory ->
        let store = newStore (Path.Combine(directory,"feedback.db"))
        let created = store.TrySubmit("alice",Guid.NewGuid(),input "Report" None,None) |> wait |> ok
        let request = Guid.NewGuid()
        let closed = store.TryUpdate("alice",request,created.Id,"Resolved",Some "abc",Some "fixed") |> wait |> ok
        Assert.That(closed.Value.ResolvedAt.HasValue, Is.True)
        let opened = store.TryUpdate("alice",Guid.NewGuid(),created.Id,"Open",None,None) |> wait |> ok
        Assert.That(opened.Value.ResolvedAt.HasValue, Is.False)
        let replay = store.TryUpdate("alice",request,created.Id,"Resolved",Some "abc",Some "fixed") |> wait |> ok
        Assert.That(replay, Is.EqualTo(closed))
        Assert.That((store.TryGet("alice",created.Id) |> wait |> ok).Value.Status, Is.EqualTo("Open"))
        Assert.That(store.TryUpdate("bob",Guid.NewGuid(),created.Id,"Resolved",None,None) |> wait |> ok, Is.EqualTo(None)))

[<Test>]
let ``verified operation evidence is tenant scoped and survives diagnostic pruning`` () =
    withDirectory (fun directory ->
        let path = Path.Combine(directory,"diagnostic.db")
        use diagnostic = new FeedbackStore(path, runPruneTimer=false)
        let operation = diagnostic.BeginOperation("alice","RunCommand",Some "Project","server",None,None)
        diagnostic.CompleteOperation(operation.Id,false,None,5L)
        let store = newStore (Path.Combine(directory,"feedback.db"))
        let evidence = { UserId="alice"; OperationId=operation.Id; Success=Some false }
        let created = store.TrySubmit("alice",Guid.NewGuid(),input "Linked" (Some operation.Id),Some evidence) |> wait |> ok
        Assert.That(created.OperationLinkState, Is.EqualTo("Verified"))
        let denied = store.TrySubmit("bob",Guid.NewGuid(),input "Other tenant" (Some operation.Id),Some evidence) |> wait |> ok
        Assert.That(denied.OperationId, Is.Empty)
        diagnostic.PruneTelemetry(DateTimeOffset.UtcNow.AddDays(31.))
        let summary = store.TrySummary("alice",Some "Project",Some "RunCommand") |> wait |> ok
        Assert.That(summary.Total, Is.EqualTo(1))
        Assert.That(summary.WithWorkaround, Is.EqualTo(1))
        Assert.That(summary.LinkedToFailedOperation, Is.EqualTo(1))
        Assert.That((store.TryGet("alice",created.Id) |> wait |> ok).Value.OperationId, Is.EqualTo(operation.Id)))

[<Test>]
let ``migration copies legacy rows and surviving operation provenance without altering source`` () =
    withDirectory (fun directory ->
        let source = Path.Combine(directory,"legacy.db")
        let destination = Path.Combine(directory,"feedback.db")
        use legacy = new FeedbackStore(source,runPruneTimer=false)
        let operation = legacy.BeginOperation("alice","RunCommand",Some "Project","server",None,None)
        legacy.CompleteOperation(operation.Id,false,Some "Failure",1L)
        let one = legacy.AddFeedback("alice",input "Legacy report" (Some operation.Id),Some operation.Id)
        let closed = legacy.UpdateFeedback("alice",one.Id,"Resolved",Some "commit",Some "notes").Value
        let two = legacy.AddFeedback("bob",input "Other user" None,None)
        let store = DurableFeedbackStore(destination)
        store.InitializeOffline(source)
        let imported = (store.TryGet("alice",one.Id) |> wait |> ok).Value
        Assert.That(imported.CreatedAt, Is.EqualTo(closed.CreatedAt))
        Assert.That(imported.ResolvedAt, Is.EqualTo(closed.ResolvedAt))
        Assert.That(imported.Status, Is.EqualTo("Resolved"))
        Assert.That(imported.ResolutionCommit, Is.EqualTo("commit"))
        Assert.That(imported.ResolutionNotes, Is.EqualTo("notes"))
        Assert.That(imported.OperationId, Is.EqualTo(operation.Id))
        Assert.That((store.TrySummary("alice",None,None) |> wait |> ok).LinkedToFailedOperation, Is.EqualTo(1))
        Assert.That((store.TryGet("bob",two.Id) |> wait |> ok).Value.Summary, Is.EqualTo("Other user"))
        Assert.That(legacy.GetFeedback("alice",one.Id).Value, Is.EqualTo(closed))
        Assert.Throws<InvalidOperationException>(fun () -> store.InitializeOffline(source)) |> ignore)

[<Test>]
let ``pre-resolution legacy schema migrates with Open default`` () =
    withDirectory (fun directory ->
        let source = Path.Combine(directory,"old.db")
        do
            use db = connection source
            exec db """CREATE TABLE feedback(id TEXT PRIMARY KEY, created_at TEXT NOT NULL, user_id TEXT NOT NULL, project_name TEXT, tool_name TEXT, category TEXT NOT NULL, severity TEXT NOT NULL, summary TEXT NOT NULL, details TEXT, workaround TEXT, operation_id TEXT);
                       INSERT INTO feedback(id,created_at,user_id,category,severity,summary,operation_id) VALUES('id','2026-01-01T00:00:00Z','alice','Other','Info','Old','unverified');"""
        let store = DurableFeedbackStore(Path.Combine(directory,"feedback.db"))
        store.InitializeOffline(source)
        let migrated = (store.TryGet("alice","id") |> wait |> ok).Value
        Assert.That(migrated.Status, Is.EqualTo("Open"))
        Assert.That(migrated.OperationId, Is.Empty)
        Assert.That(migrated.RequestedOperationId, Is.EqualTo("unverified"))
        Assert.That(migrated.OperationLinkState, Is.EqualTo("Unverified")))

[<Test>]
let ``missing or failed migration does not silently initialize or overwrite feedback`` () =
    withDirectory (fun directory ->
        let path = Path.Combine(directory,"feedback.db")
        let store = DurableFeedbackStore(path)
        Assert.That(store.TryList("alice",None,None,None,None,10) |> wait |> error, Does.Contain("FeedbackUnavailable"))
        Assert.That(File.Exists(path), Is.False)
        let source = Path.Combine(directory,"invalid.db")
        do
            use db = connection source
            exec db "CREATE TABLE unrelated(id TEXT);"
        Assert.Throws<InvalidOperationException>(fun () -> store.InitializeOffline(source)) |> ignore
        Assert.That(File.Exists(path), Is.True)
        Assert.That(store.TryList("alice",None,None,None,None,10) |> wait |> error, Does.Contain("FeedbackUnavailable"))
        Assert.Throws<InvalidOperationException>(fun () -> store.InitializeOffline()) |> ignore)

[<Test>]
let ``durable quota preserves existing feedback and receipt replay`` () =
    withDirectory (fun directory ->
        let path = Path.Combine(directory,"feedback.db")
        let store = DurableFeedbackStore(path,maxDiskBytes=256L*1024L)
        store.InitializeOffline()
        let request = Guid.NewGuid()
        let report = input "Kept" None
        let created = store.TrySubmit("alice",request,report,None) |> wait |> ok
        do
            use db = connection path
            exec db "CREATE TABLE padding(data BLOB); INSERT INTO padding VALUES(zeroblob(400000)); PRAGMA wal_checkpoint(TRUNCATE);"
        Assert.That(store.TrySubmit("alice",Guid.NewGuid(),input "New" None,None) |> wait |> error,
                    Does.Contain("FeedbackQuotaExceeded"))
        Assert.That(store.TrySubmit("alice",request,report,None) |> wait |> ok, Is.EqualTo(created))
        Assert.That((store.TryList("alice",None,None,None,None,10) |> wait |> ok).Length, Is.EqualTo(1)))

[<Test>]
let ``timeout retains the physical writer and exact retry reconciles without duplicate`` () =
    withDirectory (fun directory ->
        let path = Path.Combine(directory,"feedback.db")
        let store = DurableFeedbackStore(path,timeout=TimeSpan.FromMilliseconds 100.)
        store.InitializeOffline()
        use db = connection path
        use locked = db.BeginTransaction()
        let request = Guid.NewGuid()
        let report = input "Contended" None
        let first = store.TrySubmit("alice",request,report,None) |> wait |> error
        Assert.That(first, Does.Contain("FeedbackOutcomeUnknown"))
        Assert.That(first, Does.Contain(request.ToString("N")))
        Assert.That(store.TrySubmit("alice",Guid.NewGuid(),input "Busy" None,None) |> wait |> error,
                    Does.Contain("FeedbackBusy"))
        let readers = [| store.TryList("alice",None,None,None,None,10); store.TryList("alice",None,None,None,None,10) |]
        for reader in readers do Assert.That((reader |> wait |> ok).Length, Is.EqualTo(0))
        locked.Rollback()
        // The original call may have committed after the response deadline.
        // Reconcile the original UUID, not a new submission.
        let mutable result = store.TrySubmit("alice",request,report,None) |> wait
        let mutable attempts = 0
        while Result.isError result && attempts < 30 do
            System.Threading.Thread.Sleep(100)
            result <- store.TrySubmit("alice",request,report,None) |> wait
            attempts <- attempts+1
        let created = ok result
        let replay = store.TrySubmit("alice",request,report,None) |> wait |> ok
        Assert.That(replay, Is.EqualTo(created))
        Assert.That((store.TryList("alice",None,None,None,None,10) |> wait |> ok).Length, Is.EqualTo(1)))
