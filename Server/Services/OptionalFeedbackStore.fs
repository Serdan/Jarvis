namespace Server.Services

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

type private ActivityCoverage =
    { mutable Version: int64
      mutable ConfirmedAtUtc: DateTimeOffset option }

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
    // A persisted row cannot prove complete observation: writes can be dropped,
    // including just before a server restart. Only a successful write during
    // this process establishes coverage, revoked at each newer agent ingress.
    // The finite proof cache fails closed at capacity; it never blocks dispatch.
    let activityGate = obj()
    let activityCoverage = Dictionary<struct (string * string * string), ActivityCoverage>()
    let maxActivityKeys = 16384
    let mutable activityCoverageExhausted = false
    let canonicalProject (project: string) =
        if isNull project then ""
        else
            let separator = project.IndexOf("::", StringComparison.Ordinal)
            if separator < 0 then project else project.Substring(0, separator)
    let validActivityKey (user: string) (project: string) (agent: string) =
        not (String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace project ||
             String.IsNullOrWhiteSpace agent)
        && user.Length <= 512 && project.Length <= 256 && agent.Length <= 128
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

    /// The in-process gate is invalidated SYNCHRONOUSLY before dispatch.
    /// Rejected, failed or timed-out SQLite writes leave the key unverified.
    /// An older write completing late cannot certify a newer agent call.
    member this.RecordAgentActivity(userId: string, projectName: string,
                                    agentId: string, observedAtUtc: DateTimeOffset) : Task =
        let project = canonicalProject projectName
        if not (validActivityKey userId project agentId) then Task.CompletedTask
        else
            let key = struct (userId, project, agentId)
            let version =
                lock activityGate (fun () ->
                    if activityCoverageExhausted then None
                    else
                        match activityCoverage.TryGetValue(key) with
                        | true, coverage ->
                            coverage.Version <- coverage.Version + 1L
                            coverage.ConfirmedAtUtc <- None
                            Some coverage.Version
                        | _ when activityCoverage.Count < maxActivityKeys ->
                            activityCoverage[key] <- { Version = 1L; ConfirmedAtUtc = None }
                            Some 1L
                        | _ ->
                            activityCoverageExhausted <- true
                            None)
            match version with
            | None -> Task.CompletedTask
            | Some currentVersion ->
                task {
                    try
                        let! result =
                            this.TryExecute(fun db ->
                                db.RecordAttributedAgentActivity(userId, project, agentId, observedAtUtc))
                        match result with
                        | Ok () ->
                            lock activityGate (fun () ->
                                match activityCoverage.TryGetValue(key) with
                                | true, coverage when coverage.Version = currentVersion ->
                                    coverage.ConfirmedAtUtc <- Some observedAtUtc
                                | _ -> ())
                        | Error _ -> () // Fail closed, including quota and uncertain timeouts.
                    with _ -> () // Telemetry must never change command semantics.
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
            if database.IsValueCreated then
                (database.Value :> IDisposable).Dispose()
