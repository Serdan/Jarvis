namespace Server.Services

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Optional, single-flight access to the disposable server diagnostics database.
///
/// No filesystem or SQLite work occurs in the constructor or on an authenticated
/// request/SignalR routing thread. A stuck database consumes at most one worker;
/// later diagnostics fail immediately instead of piling up blocking SQLite calls.
/// The underlying database is deliberately neither deleted nor migrated here.
type OptionalFeedbackStore(databasePath: string, ?timeout: TimeSpan, ?maxDiskBytes: int64) as this =
    let deadline = defaultArg timeout (TimeSpan.FromMilliseconds 200.)
    do if deadline <= TimeSpan.Zero then invalidArg "timeout" "Must be positive."
    // Hard high-water: no new diagnostic operation starts once the main DB
    // plus WAL/SHM/rollback sidecars consume 32 MiB. SQLite itself is limited
    // to 16 MiB of database pages; WAL is periodically checkpointed.
    // Legacy oversized files are left byte-for-byte untouched and unavailable.
    let diskBudget = defaultArg maxDiskBytes (32L * 1024L * 1024L)
    do if diskBudget < 256L * 1024L then invalidArg "maxDiskBytes" "Must be at least 256 KiB."
    let databaseBudget = min (16L * 1024L * 1024L) (diskBudget / 2L)
    let fileBytes path =
        if File.Exists path then FileInfo(path).Length else 0L
    let diagnosticBytes () =
        [| databasePath; databasePath + "-wal"; databasePath + "-shm"; databasePath + "-journal" |]
        |> Array.sumBy fileBytes
    let database =
        lazy (new FeedbackStore(databasePath, runPruneTimer = false, maxDatabaseBytes = databaseBudget))
    let mutable inFlight = 0
    let unavailable = "TelemetryUnavailable: optional server SQLite is disabled, busy, or unresponsive."
    // Periodic retention must follow the same single-flight budget; the
    // underlying FeedbackStore timer is disabled for this wrapper.
    let pruneTimer =
        new Timer(TimerCallback(fun _ ->
            this.TryPost(fun db -> db.PruneTelemetry(DateTimeOffset.UtcNow))),
            null, TimeSpan.FromHours 1.0, TimeSpan.FromHours 1.0)

    member _.TryExecute<'T>(operation: FeedbackStore -> 'T) : Task<Result<'T, string>> =
        task {
            if String.IsNullOrWhiteSpace databasePath then
                return Error unavailable
            elif Interlocked.CompareExchange(&inFlight, 1, 0) <> 0 then
                return Error unavailable
            else
                // Task.Run is essential: even initializing a corrupt/locked database
                // must never execute on the caller's request/dispatch thread.
                let background =
                    try
                        Task.Run(Func<'T>(fun () ->
                            try
                                // Stat and SQLite both execute off the MCP request
                                // path; over-budget existing DBs are not opened.
                                if fileBytes databasePath >= databaseBudget ||
                                   diagnosticBytes() >= diskBudget then
                                    invalidOp "TelemetryQuotaExceeded: diagnostic disk budget reached."
                                operation database.Value
                            finally Interlocked.Exchange(&inFlight, 0) |> ignore))
                    with _ ->
                        Interlocked.Exchange(&inFlight, 0) |> ignore
                        reraise()
                try
                    let! result = background.WaitAsync(deadline)
                    return Ok result
                with
                | :? TimeoutException ->
                    // The worker may still finish. Never start another SQLite
                    // operation until it returns, and do not claim a write failed.
                    return Error "TelemetryUnavailable: SQLite timed out; diagnostic outcome unknown."
                | :? InvalidOperationException as ex when
                    ex.Message.StartsWith("TelemetryQuotaExceeded:", StringComparison.Ordinal) ->
                    return Error ex.Message
                | ex ->
                    return Error("TelemetryUnavailable: " + ex.GetType().Name)
        }

    /// Fire-and-forget best-effort diagnostics. No database activity or wait on
    /// this calling thread; overload, startup failure and storage loss drop data.
    member this.TryPost(operation: FeedbackStore -> unit) =
        this.TryExecute(operation) |> ignore

    interface IDisposable with
        member _.Dispose() =
            pruneTimer.Dispose()
            if database.IsValueCreated then
                (database.Value :> IDisposable).Dispose()
