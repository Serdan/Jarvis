namespace Server.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

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
    let execute (operation: FeedbackStore -> 'T) : Result<'T, string> =
        try
            // Neither filesystem inspection nor lazy SQLite initialization runs
            // on an ingress thread. Every physical operation shares these quotas.
            if fileBytes databasePath >= databaseBudget ||
               diagnosticBytes() >= diskBudget then
                invalidOp "TelemetryQuotaExceeded: diagnostic disk budget reached."
            Ok(operation database.Value)
        with
        | :? TimeoutException -> Error timedOut
        | :? InvalidOperationException as ex when
            ex.Message.StartsWith("TelemetryQuotaExceeded:", StringComparison.Ordinal) ->
            Error ex.Message
        | ex -> Error("TelemetryUnavailable: " + ex.GetType().Name)

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
            this.TryPost(fun db -> db.PruneTelemetry(DateTimeOffset.UtcNow))),
            null, TimeSpan.FromHours 1.0, TimeSpan.FromHours 1.0)

    member _.TryExecute<'T>(operation: FeedbackStore -> 'T) : Task<Result<'T, string>> =
        task {
            let completion = TaskCompletionSource<Result<'T, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let admitted =
                lock activityGate (fun () ->
                    if disposed || String.IsNullOrWhiteSpace databasePath || active.IsSome then false
                    else
                        startLocked (diagnosticWork (fun () ->
                            let result = execute operation
                            fun () -> completion.TrySetResult(result) |> ignore))
                        true)
            if not admitted then return Error unavailable
            else
                try return! completion.Task.WaitAsync(deadline)
                with :? TimeoutException -> return Error timedOut
        }

    /// Best-effort diagnostics retain immediate, nonwaiting admission. While
    /// activity owns the worker, reserve at most one posted operation for its
    /// next turn. Further posts (and posts during diagnostics) are dropped as
    /// before. An expired reservation is not executed later.
    member _.TryPost(operation: FeedbackStore -> unit) =
        lock activityGate (fun () ->
            if not disposed && not (String.IsNullOrWhiteSpace databasePath) then
                let admittedAt = Stopwatch.GetTimestamp()
                let work () = diagnosticWork (fun () ->
                    if Stopwatch.GetElapsedTime(admittedAt) <= deadline then
                        execute operation |> ignore
                    ignore)
                match active, pendingPost with
                | None, _ -> startLocked (work())
                | Some Activity, None -> pendingPost <- Some(work())
                | _ -> ())

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
