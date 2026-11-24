namespace Server.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions

type private PendingActivity =
    { Version: int64
      ObservedAtUtc: DateTimeOffset
      Completion: TaskCompletionSource<unit> }

type private ActivityCoverage =
    { mutable Version: int64
      mutable HighWaterUtc: DateTimeOffset
      mutable ConfirmedAtUtc: DateTimeOffset option
      mutable Pending: PendingActivity option }

type private StoreWorkKind = Activity | Diagnostic

// Execute performs physical work off the gate, returning its publication action.
// Publication and the next reservation are serialized with ingress revocation.
type private StoreWork =
    { Kind: StoreWorkKind
      Execute: unit -> (unit -> unit) }

/// Optional, single-flight access to the disposable server diagnostics database.
///
/// No filesystem or SQLite work occurs in the constructor or on an authenticated
/// request/SignalR routing thread. A stuck database consumes at most one worker;
/// later diagnostic reads fail immediately. Activity has one coalescing slot
/// per bounded authority key; a single posted diagnostic may take a turn
/// between activity writes, without a general-purpose diagnostic queue.
/// The underlying database is deliberately neither deleted nor migrated here.
type OptionalFeedbackStore(databasePath: string, ?timeout: TimeSpan, ?maxDiskBytes: int64, ?logger: ILogger) as this =
    let logger = defaultArg logger NullLogger.Instance
    let deadline = defaultArg timeout (TimeSpan.FromMilliseconds 200.)
    do if deadline <= TimeSpan.Zero then invalidArg "timeout" "Must be positive."
    // Soft pressure sheds expendable posts; hard pressure rejects new writes.
    // Reads and bounded maintenance have independent admission. Recovery must
    // cross the lower watermark before posts resume.
    let diskBudget = defaultArg maxDiskBytes (32L * 1024L * 1024L)
    do if diskBudget < 256L * 1024L then invalidArg "maxDiskBytes" "Must be at least 256 KiB."
    let databaseBudget = min (16L * 1024L * 1024L) (diskBudget / 2L)
    let fileBytes path =
        if File.Exists path then FileInfo(path).Length else 0L
    let diagnosticBytes () =
        [| databasePath; databasePath + "-wal"; databasePath + "-shm"; databasePath + "-journal" |]
        |> Array.sumBy fileBytes
    let softMain, softDisk = databaseBudget * 4L / 5L, diskBudget * 4L / 5L
    let recoverMain, recoverDisk = databaseBudget * 13L / 20L, diskBudget * 13L / 20L
    let newDatabase () =
        lazy (new FeedbackStore(databasePath, runPruneTimer = false, maxDatabaseBytes = databaseBudget))
    let mutable database = newDatabase()
    let getDatabase () =
        try database.Value
        with _ ->
            // Lazy caches initialization errors; a transient quota/lock must
            // not poison the write store for the rest of this process.
            database <- newDatabase()
            reraise()
    let readDatabase = lazy (new FeedbackStore(databasePath, readOnly = true, runPruneTimer = false))
    // These fields are accessed only by the existing physical worker.
    let mutable pressure = false
    let mutable lastRoutinePrune = DateTimeOffset.MinValue
    let mutable lastMaintenanceFault = ""
    let mutable storageStatus = "NotObserved"
    let markPressure () =
        pressure <- true
        if Volatile.Read(&storageStatus) <> "MaintenanceRequired" then
            Volatile.Write(&storageStatus, "Pressure")
    let measure () =
        let main, total = fileBytes databasePath, diagnosticBytes()
        if main >= softMain || total >= softDisk then markPressure()
        main, total
    let releasePressure main total =
        if main < recoverMain && total < recoverDisk then pressure <- false
    let freeSpace () =
        let directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
        DriveInfo.GetDrives()
        |> Array.filter (fun drive ->
            let root = drive.RootDirectory.FullName
            String.Equals(directory, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            || directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar, StringComparison.Ordinal))
        |> Array.sortByDescending (fun drive -> drive.RootDirectory.FullName.Length)
        |> Array.tryHead
        |> Option.map (fun drive -> drive.AvailableFreeSpace)
    let quotaMessage = "TelemetryQuotaExceeded: diagnostic disk budget reached; optional retention maintenance required."
    let maintenanceRequired reason = invalidOp ("TelemetryMaintenanceRequired: " + reason)
    let maintain () =
        let main, total = measure()
        if not (File.Exists databasePath) then
            if total > 0L then maintenanceRequired "database absent with existing sidecars; operator inspection required."
            else releasePressure main total
        elif not pressure then
            let now = DateTimeOffset.UtcNow
            if now - lastRoutinePrune >= TimeSpan.FromHours 1. then
                getDatabase().PruneTelemetry(now)
                lastRoutinePrune <- now
        else
            // Never create a replacement DB or manually delete a SQLite sidecar.
            // Limit legacy compaction to one configured disk budget, with
            // explicit temporary free-space admission and a progress deadline.
            if main > diskBudget || total - diskBudget > diskBudget then
                maintenanceRequired $"legacy footprint exceeds bounded reclamation envelope (main={main}, aggregate={total} bytes); operator archival required."
            match freeSpace() with
            | Some available when available >= min total diskBudget + diskBudget / 4L -> ()
            | _ -> maintenanceRequired "insufficient verified free space for safe checkpoint and retention."
            let builder = SqliteConnectionStringBuilder()
            builder.DataSource <- databasePath
            builder.Mode <- SqliteOpenMode.ReadWrite
            builder.Cache <- SqliteCacheMode.Private
            builder.Pooling <- false
            builder.DefaultTimeout <- 1
            use connection = new SqliteConnection(builder.ToString())
            connection.Open()
            let started = Stopwatch.GetTimestamp()
            SQLitePCL.raw.sqlite3_progress_handler(
                connection.Handle, 1000,
                SQLitePCL.delegate_progress(fun _ ->
                    if Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds 2. then 1 else 0), null)
            let scalar sql =
                use command = connection.CreateCommand()
                command.CommandText <- sql
                Convert.ToInt64(command.ExecuteScalar())
            let exec sql =
                use command = connection.CreateCommand()
                command.CommandText <- sql
                command.ExecuteNonQuery()
            let checkpoint () =
                use command = connection.CreateCommand()
                command.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE);"
                use reader = command.ExecuteReader()
                if not (reader.Read()) || reader.GetInt32(0) <> 0 then
                    maintenanceRequired "WAL checkpoint blocked by another SQLite connection; retry after it releases."
            // Confirm the expected schema before maintenance. Only two
            // expendable tables may be pruned; protected feedback and activity
            // rows (and operation links retained by feedback) are untouched.
            if scalar "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('operations','connection_events','feedback');" <> 3L then
                maintenanceRequired "diagnostic schema is unavailable; operator inspection required."
            checkpoint()
            let currentMain, currentTotal = measure()
            releasePressure currentMain currentTotal
            if not pressure then ()
            else
                if currentMain > diskBudget || (currentTotal >= diskBudget && currentTotal - currentMain > diskBudget / 4L) then
                    maintenanceRequired "database or sidecars remain above bounded maintenance headroom after checkpoint."
                let pageSize = scalar "PRAGMA page_size;"
                let pageCount = scalar "PRAGMA page_count;"
                scalar $"PRAGMA max_page_count = {max pageCount (databaseBudget / pageSize)};" |> ignore
                exec "PRAGMA foreign_keys = ON;" |> ignore
                scalar "PRAGMA wal_autocheckpoint = 64;" |> ignore
                // One pass is bounded; the recurring worker can continue over
                // subsequent passes. Oldest optional rows go first even if
                // the normal 30-day horizon has not been reached.
                use transaction = connection.BeginTransaction()
                let delete sql =
                    use command = connection.CreateCommand()
                    command.Transaction <- transaction
                    command.CommandText <- sql
                    command.ExecuteNonQuery()
                let deletedEvents =
                    delete "DELETE FROM connection_events WHERE rowid IN (SELECT rowid FROM connection_events ORDER BY created_at, rowid LIMIT 128);"
                let deletedOperations =
                    delete "DELETE FROM operations WHERE rowid IN (SELECT o.rowid FROM operations o WHERE NOT EXISTS (SELECT 1 FROM feedback f WHERE f.operation_id=o.id) ORDER BY o.created_at, o.rowid LIMIT 128);"
                transaction.Commit()
                checkpoint()
                let usedBytes = (scalar "PRAGMA page_count;" - scalar "PRAGMA freelist_count;") * pageSize
                let beforeCompact, _ = measure()
                if pressure && beforeCompact >= recoverMain && usedBytes < recoverMain then
                    // VACUUM is transactional, but SQLite may need roughly two
                    // database sizes of temporary space. Keep an extra budget
                    // free and never attempt compaction without this evidence.
                    match freeSpace() with
                    | Some available when available >= 2L * beforeCompact + diskBudget ->
                        exec "VACUUM;" |> ignore
                        checkpoint()
                    | _ -> maintenanceRequired "insufficient verified free disk space for safe compaction."
                let afterMain, afterTotal = measure()
                releasePressure afterMain afterTotal
                if pressure && deletedEvents + deletedOperations = 0 then
                    let feedbackBytes =
                        try
                            scalar "SELECT COALESCE(SUM(pgsize),0) FROM dbstat WHERE name IN (SELECT name FROM sqlite_master WHERE tbl_name='feedback');"
                            |> string
                        with :? SqliteException -> "unavailable (dbstat)"
                    maintenanceRequired $"no eligible telemetry remains; main={afterMain} bytes, aggregate={afterTotal} bytes, allocated live pages={usedBytes} bytes, feedback pages and indexes={feedbackBytes} bytes. Protected feedback/activity or schema require an operator archival decision."
                elif pressure then
                    maintenanceRequired $"bounded retention pass incomplete; main={afterMain} bytes, aggregate={afterTotal} bytes; retry maintenance."
    let unavailable = "TelemetryUnavailable: optional server SQLite is disabled, busy, or unresponsive."
    // A persisted row cannot prove complete observation: writes can be dropped,
    // including just before a server restart. Only a successful write during
    // this process establishes coverage, revoked at each newer agent ingress.
    // The finite proof cache fails closed at capacity; it never blocks dispatch.
    let activityGate = obj()
    let mutable activityCoverage = Dictionary<struct (string * string * string), ActivityCoverage>()
    let mutable readyKeys = Queue<struct (string * string * string)>()
    let mutable active: StoreWorkKind option = None
    let mutable pendingPost: StoreWork option = None
    let mutable disposed = false
    let maxActivityKeys = 16384
    let mutable activityCoverageExhausted = false
    // Abandon exhausted slots in constant time under the ingress gate. Their
    // bounded waits expire independently; GC can reclaim the abandoned graph.
    let discardActivityLocked () =
        activityCoverage <- Dictionary<_, _>()
        readyKeys <- Queue<_>()
    let canonicalProject (project: string) =
        if isNull project then ""
        else
            let separator = project.IndexOf("::", StringComparison.Ordinal)
            if separator < 0 then project else project.Substring(0, separator)
    let validActivityKey (user: string) (project: string) (agent: string) =
        not (String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace project ||
             String.IsNullOrWhiteSpace agent)
        && user.Length <= 512 && project.Length <= 256 && agent.Length <= 128
    let timedOut = "TelemetryUnavailable: SQLite timed out; diagnostic outcome unknown."
    let executePhysical (operation: unit -> 'T) : Result<'T, string> =
        try Ok(operation())
        with
        | :? TimeoutException -> Error timedOut
        | :? SqliteException as ex when ex.SqliteErrorCode = 13 ->
            markPressure()
            Error quotaMessage
        | :? SqliteException as ex ->
            Error($"TelemetryUnavailable: SQLite error {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}.")
        | :? InvalidOperationException as ex when
            ex.Message.StartsWith("TelemetryQuotaExceeded:", StringComparison.Ordinal) ||
            ex.Message.StartsWith("TelemetryMaintenanceRequired:", StringComparison.Ordinal) ->
            Error ex.Message
        | ex -> Error("TelemetryUnavailable: " + ex.GetType().Name)
    let execute (operation: FeedbackStore -> 'T) =
        executePhysical (fun () ->
            let main, total = measure()
            if main >= databaseBudget || total >= diskBudget then invalidOp quotaMessage
            operation (getDatabase()))
    let executePost operation =
        executePhysical (fun () ->
            measure() |> ignore
            if pressure then invalidOp quotaMessage
            operation (getDatabase()))
    let executeMaintenance () =
        let result = executePhysical maintain
        match result with
        | Ok () ->
            lastMaintenanceFault <- ""
            Volatile.Write(&storageStatus, if pressure then "Pressure" else "Healthy")
        | Error reason ->
            Volatile.Write(&storageStatus, "MaintenanceRequired")
            // Details are server-local, not exposed to another user's MCP
            // request. Do not log row contents or raw SQLite error messages.
            if reason <> lastMaintenanceFault then
                lastMaintenanceFault <- reason
                try logger.LogWarning("Optional diagnostic maintenance: {Reason}", reason)
                with _ -> ()
        result

    // A shared worker must not inherit the first request's AsyncLocals for
    // later observations. Diagnostics may legitimately need their own request
    // context (e.g. feedback authorization via IHttpContextAccessor), so only
    // an admitted diagnostic captures and restores its individual context.
    let diagnosticWork (work: unit -> (unit -> unit)) =
        let context = ExecutionContext.Capture()
        { Kind = Diagnostic
          Execute = fun () ->
            if isNull context then work()
            else
                let mutable publish = ignore
                ExecutionContext.Run(context, ContextCallback(fun _ -> publish <- work()), null)
                publish }

    let activityWork key observation =
        // Time spent awaiting admission is not an attempted SQLite write.
        // Once dispatched, even physical success after the deadline is uncertain
        // and cannot publish proof. No retry of this version is scheduled.
        let dispatchedAt = Stopwatch.GetTimestamp()
        let struct (user, project, agent) = key
        { Kind = Activity
          Execute = fun () ->
            let outcome = execute (fun db ->
                db.RecordAttributedAgentActivity(user, project, agent, observation.ObservedAtUtc))
            fun () ->
                match outcome, activityCoverage.TryGetValue(key) with
                | Ok (), (true, coverage)
                    when not activityCoverageExhausted && not disposed &&
                         coverage.Version = observation.Version &&
                         Stopwatch.GetElapsedTime(dispatchedAt) <= deadline ->
                    coverage.ConfirmedAtUtc <- Some observation.ObservedAtUtc
                | _ -> ()
                observation.Completion.TrySetResult(()) |> ignore }

    // Only the worker (or the idle-to-active transition) consumes ready keys.
    // Each key is enqueued once while Pending is populated; replacing a pending
    // observation keeps its place. A running key may have one further slot.
    let reserveNextLocked () =
        let next =
            if disposed then None
            else
                match pendingPost with
                | Some post ->
                    pendingPost <- None
                    Some post
                | None when activityCoverageExhausted ->
                    discardActivityLocked()
                    None
                | None when readyKeys.Count > 0 ->
                    let key = readyKeys.Dequeue()
                    let coverage = activityCoverage[key]
                    let observation = coverage.Pending.Value
                    coverage.Pending <- None
                    Some(activityWork key observation)
                | None -> None
        active <- next |> Option.map (fun work -> work.Kind)
        next

    let runWorker first =
        let mutable next = Some first
        while next.IsSome do
            // A deadline never releases the physical slot. Only this worker,
            // after SQLite returns, may publish and reserve the next operation.
            let publish = next.Value.Execute()
            next <- lock activityGate (fun () ->
                publish()
                reserveNextLocked())

    let startLocked work =
        active <- Some work.Kind
        try
            let schedule () = Task.Run(Action(fun () -> runWorker work)) |> ignore
            if ExecutionContext.IsFlowSuppressed() then schedule()
            else
                use _flow = ExecutionContext.SuppressFlow()
                schedule()
        with _ ->
            active <- None
            reraise()

    // Periodic retention must follow the same single-flight budget; the
    // underlying FeedbackStore timer is disabled for this wrapper.
    let pruneTimer =
        new Timer(TimerCallback(fun _ ->
            this.PostMaintenance()),
            null, TimeSpan.FromMinutes 1.0, TimeSpan.FromMinutes 1.0)

    member private _.TryExecuteCore<'T>(operation: unit -> Result<'T, string>) : Task<Result<'T, string>> =
        task {
            let completion = TaskCompletionSource<Result<'T, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let admitted =
                lock activityGate (fun () ->
                    if disposed || String.IsNullOrWhiteSpace databasePath || active.IsSome then false
                    else
                        startLocked (diagnosticWork (fun () ->
                            let result = operation()
                            fun () -> completion.TrySetResult(result) |> ignore))
                        true)
            if not admitted then return Error unavailable
            else
                try return! completion.Task.WaitAsync(deadline)
                with :? TimeoutException -> return Error timedOut
        }

    member this.TryExecute<'T>(operation: FeedbackStore -> 'T) =
        this.TryExecuteCore(fun () -> execute operation)

    /// Existing history is opened read-only, independently of write quota
    /// and schema initialization. A missing or corrupt DB remains unavailable.
    member this.TryRead<'T>(operation: FeedbackStore -> 'T) =
        this.TryExecuteCore(fun () -> executePhysical(fun () ->
            // Size inspection is not a prerequisite for reading history.
            try measure() |> ignore with _ -> ()
            operation readDatabase.Value))

    /// Explicit single-flight maintenance for deterministic callers/tests.
    /// The timer uses the same physical operation; never release a timed-out slot.
    member this.TryMaintain() =
        this.TryExecuteCore executeMaintenance

    /// No SQLite access or tenant-specific data on this status read.
    member _.StorageStatus = Volatile.Read(&storageStatus)

    /// Best-effort diagnostics retain immediate, nonwaiting admission. While
    /// activity owns the worker, reserve at most one posted operation for its
    /// next turn. Further posts (and posts during diagnostics) are dropped as
    /// before. An expired reservation is not executed later.
    member private _.PostWork(operation: unit -> unit) =
        lock activityGate (fun () ->
            if not disposed && not (String.IsNullOrWhiteSpace databasePath) then
                let admittedAt = Stopwatch.GetTimestamp()
                let work () = diagnosticWork (fun () ->
                    if Stopwatch.GetElapsedTime(admittedAt) <= deadline then
                        operation()
                    ignore)
                match active, pendingPost with
                | None, _ -> startLocked (work())
                | Some Activity, None -> pendingPost <- Some(work())
                | _ -> ())

    member this.TryPost(operation: FeedbackStore -> unit) =
        this.PostWork(fun () -> executePost operation |> ignore)

    /// Retention is not an expendable request post. Reserve the existing
    /// single pending diagnostic slot even during another diagnostic; replace
    /// an expendable reservation if needed. No additional queue or worker and
    /// no request deadline that could discard scheduled recovery under load.
    member _.PostMaintenance() =
        lock activityGate (fun () ->
            if not disposed && not (String.IsNullOrWhiteSpace databasePath) then
                let work =
                    { Kind = Diagnostic
                      Execute = fun () ->
                          executeMaintenance() |> ignore
                          ignore }
                match active with
                | None -> startLocked work
                | Some _ -> pendingPost <- Some work)

    /// Invalidate synchronously, before dispatch, even if storage is unavailable.
    /// The returned task is only a bounded best-effort wait, never proof. A
    /// superseded pending waiter is completed immediately; at most one waiter
    /// per key and one running waiter are retained. A caller's bounded wait
    /// is independent of queue/dispatch time and does not certify or cancel
    /// the observation. The dispatched write has its own deadline: a physical
    /// completion past that deadline can never certify that version.
    member _.RecordAgentActivity(userId: string, projectName: string,
                                 agentId: string, observedAtUtc: DateTimeOffset) : Task =
        let project = canonicalProject projectName
        if not (validActivityKey userId project agentId) then Task.CompletedTask
        else
            let key = struct (userId, project, agentId)
            let completion =
                lock activityGate (fun () ->
                    if activityCoverageExhausted || disposed then None
                    else
                        let coverage =
                            match activityCoverage.TryGetValue(key) with
                            | true, known when known.Version < Int64.MaxValue ->
                                known.Version <- known.Version + 1L
                                known.HighWaterUtc <- max known.HighWaterUtc observedAtUtc
                                known.ConfirmedAtUtc <- None
                                Some known
                            | false, _ when activityCoverage.Count < maxActivityKeys ->
                                let fresh =
                                    { Version = 1L; HighWaterUtc = observedAtUtc
                                      ConfirmedAtUtc = None; Pending = None }
                                activityCoverage.Add(key, fresh)
                                Some fresh
                            | _ ->
                                activityCoverageExhausted <- true
                                None
                        match coverage with
                        | Some known when not (String.IsNullOrWhiteSpace databasePath) ->
                            let waiter = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                            let observation =
                                { Version = known.Version; ObservedAtUtc = known.HighWaterUtc
                                  Completion = waiter }
                            match known.Pending with
                            | Some previous -> previous.Completion.TrySetResult(()) |> ignore
                            | None -> readyKeys.Enqueue(key)
                            known.Pending <- Some observation
                            if active.IsNone then
                                reserveNextLocked() |> Option.iter startLocked
                            Some waiter.Task
                        | _ -> None)
            match completion with
            | None -> Task.CompletedTask
            | Some waiter ->
                task {
                    try do! waiter.WaitAsync(deadline)
                    with :? TimeoutException -> ()
                } :> Task

    /// A fresh, authenticated Hub read. A row from a previous server process
    /// is not coverage; missing rows, failed reads and concurrent ingress HOLD.
    member this.GetVerifiedAgentActivity(userId: string, projectName: string,
                                         agentId: string) : Task<DateTimeOffset * DateTimeOffset> =
        task {
            let project = canonicalProject projectName
            if not (validActivityKey userId project agentId) then
                invalidOp "ActivityProofUnavailable: invalid or unbounded activity identity."
            let key = struct (userId, project, agentId)
            let expected =
                lock activityGate (fun () ->
                    if activityCoverageExhausted then None
                    else
                        match activityCoverage.TryGetValue(key) with
                        | true, coverage ->
                            coverage.ConfirmedAtUtc
                            |> Option.map (fun seen -> coverage.Version, seen)
                        | _ -> None)
            match expected with
            | None ->
                return invalidOp "ActivityProofUnavailable: no complete current-process activity evidence."
            | Some (version, confirmedAt) ->
                let! outcome =
                    this.TryExecute(fun db -> db.GetAttributedAgentActivity(userId, project, agentId))
                let serverNow = DateTimeOffset.UtcNow
                let unchanged =
                    lock activityGate (fun () ->
                        if activityCoverageExhausted then false
                        else
                            match activityCoverage.TryGetValue(key) with
                            | true, coverage ->
                                coverage.Version = version && coverage.ConfirmedAtUtc = Some confirmedAt
                            | _ -> false)
                match outcome with
                | Ok (Some seen) when unchanged && seen >= confirmedAt && seen <= serverNow ->
                    return seen, serverNow
                | _ ->
                    return invalidOp "ActivityProofUnavailable: SQLite observation is missing, stale or unverified."
        }

    interface IDisposable with
        member _.Dispose() =
            pruneTimer.Dispose()
            lock activityGate (fun () ->
                disposed <- true
                activityCoverageExhausted <- true
                pendingPost <- None
                discardActivityLocked())
            if database.IsValueCreated then
                (database.Value :> IDisposable).Dispose()
