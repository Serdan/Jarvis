module Client.WorkQueueSchedulerHostLoop

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Client.WorkQueueReactiveWake

/// The heads must be freshly fetched and verified against the approved Git
/// remotes. They are a preflight, not authority to dispatch: Coordinator.Wake
/// independently rereads and fences the canonical heads before any effect.
type VerifiedWorkHeads =
    { AccountHead: string
      ProjectHead: string }

[<RequireQualifiedAccess>]
type SchedulerHostOutcome =
    | Disabled
    | Stopped
    | Dormant
    | Busy
    | NeedsAttention of code: string * nextAction: string
    | Reconciled of WorkWakeResult

/// All inputs are client-owned. A host never stores authoritative state, mints
/// identities, reserves capacity or submits an original browser operation.
type SchedulerHostPorts =
    { ReadVerifiedHeads: unit -> Result<VerifiedWorkHeads, string>
      Wake: WorkWakeTrigger list -> WorkWakeResult
      Report: SchedulerHostOutcome -> unit
      // An injected, cancellation-aware clock. Production uses Task.Delay;
      // tests can advance a controlled clock without sleeping.
      Delay: TimeSpan -> CancellationToken -> Task }

module WorkQueueSchedulerHostLoop =
    let productionPorts (coordinator: WorkQueueReactiveWake.Coordinator)
                        readVerifiedHeads report =
        { ReadVerifiedHeads = readVerifiedHeads
          Wake = coordinator.Wake
          Report = report
          Delay = fun interval cancellation -> Task.Delay(interval, cancellation) }

    /// Explicitly started, default-off service. Work signals are lossy hints,
    /// represented by a bounded in-memory set. Startup and periodic full scans
    /// always invoke the same verified-head and Coordinator.Wake path.
    /// There is no background task until Start is explicitly called.
    type Host(ports: SchedulerHostPorts, ?enabled: bool, ?fullRescanInterval: TimeSpan) as this =
        let enabled = defaultArg enabled false
        let interval = defaultArg fullRescanInterval (TimeSpan.FromMinutes 2.0)
        do
            if interval <= TimeSpan.Zero then
                invalidArg "fullRescanInterval" "The full rescan interval must be positive."

        let sync = obj ()
        let pending = HashSet<WorkWakeTrigger>()
        let pulse = new SemaphoreSlim(0, 1)
        let stop = new CancellationTokenSource()
        let mutable started = false
        let mutable closed = false
        let mutable inStep = false
        let mutable stepThreadId = 0
        let mutable stepFinished: TaskCompletionSource<unit> option = None
        let mutable pump: Task option = None
        let mutable timer: Task option = None
        let mutable lastOutcome: SchedulerHostOutcome option = None

        let wakePump () =
            // A full semaphore is already a pending wake. Repeated signals
            // never accumulate an unbounded number of scheduling tasks.
            try pulse.Release() |> ignore
            with :? SemaphoreFullException -> ()

        let queue (trigger: WorkWakeTrigger) =
            lock sync (fun () ->
                if not enabled || closed then false
                else
                    let wasEmpty = pending.Count = 0
                    let added = pending.Add trigger
                    if wasEmpty && added then wakePump ()
                    added)

        let reconcile (triggers: WorkWakeTrigger list) =
            try
                match ports.ReadVerifiedHeads() with
                | Error error ->
                    SchedulerHostOutcome.NeedsAttention(
                        "CanonicalHeadsUnavailable",
                        "Inspect the approved project/account Git bindings and retry from verified heads: " + error)
                | Ok heads when
                    isNull (box heads)
                    || String.IsNullOrWhiteSpace heads.AccountHead
                    || String.IsNullOrWhiteSpace heads.ProjectHead ->
                    SchedulerHostOutcome.NeedsAttention(
                        "CanonicalHeadsUnavailable",
                        "A complete verified account and project Git head is required before reconciliation.")
                | Ok _ ->
                    try SchedulerHostOutcome.Reconciled(ports.Wake triggers)
                    with ex ->
                        SchedulerHostOutcome.NeedsAttention(
                            "CoordinatorFailure",
                            "Inspect the coordinator failure and original durable operations before retrying: " + ex.Message)
            with ex ->
                SchedulerHostOutcome.NeedsAttention(
                    "CanonicalHeadReadFailed",
                    "Restore the approved Git read ports and retry; no callback was made: " + ex.Message)

        /// One deterministic reconciliation, independently usable without
        /// running the service. Disabled/closed/empty calls have no Git I/O.
        /// Concurrent and same-thread reentrancy never invoke nested effects.
        member _.RunOnce(triggers: WorkWakeTrigger list) =
            let decision =
                lock sync (fun () ->
                    if not enabled then Choice1Of2 SchedulerHostOutcome.Disabled
                    elif closed then Choice1Of2 SchedulerHostOutcome.Stopped
                    elif inStep then
                        // Preserve concurrent/reentrant manual hints for the
                        // current pump, without running the coordinator twice.
                        for trigger in triggers do
                            let wasEmpty = pending.Count = 0
                            if pending.Add trigger && wasEmpty then wakePump ()
                        Choice1Of2 SchedulerHostOutcome.Busy
                    else
                        let batch = HashSet<WorkWakeTrigger>(pending)
                        for trigger in triggers do batch.Add trigger |> ignore
                        pending.Clear()
                        if batch.Count = 0 then
                            Choice1Of2 SchedulerHostOutcome.Dormant
                        else
                            inStep <- true
                            stepThreadId <- Environment.CurrentManagedThreadId
                            stepFinished <- Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
                            Choice2Of2(batch |> Seq.toList |> List.sort))
            match decision with
            | Choice1Of2 result -> result
            | Choice2Of2 batch ->
                let result = reconcile batch
                try
                    lock sync (fun () -> lastOutcome <- Some result)
                    // Reporting is observational. A broken reporter must not
                    // kill the host or turn a known operation into a retry.
                    try ports.Report result with _ -> ()
                finally
                    lock sync (fun () ->
                        inStep <- false
                        stepThreadId <- 0
                        let finished = stepFinished
                        stepFinished <- None
                        if pending.Count > 0 && not closed then wakePump ()
                        finished |> Option.iter (fun doneStep -> doneStep.TrySetResult(()) |> ignore))
                result

        member _.Notify(trigger: WorkWakeTrigger) = queue trigger

        member _.LastOutcome =
            lock sync (fun () -> lastOutcome)

        member _.IsRunning =
            lock sync (fun () -> started && not closed)

        member private _.Pump(cancellation: CancellationToken) : Task =
            task {
                try
                    while not cancellation.IsCancellationRequested do
                        do! pulse.WaitAsync cancellation
                        if not cancellation.IsCancellationRequested then
                            this.RunOnce [] |> ignore
                with :? OperationCanceledException when cancellation.IsCancellationRequested ->
                    ()
            } :> Task

        member private _.Timer(cancellation: CancellationToken) : Task =
            task {
                try
                    while not cancellation.IsCancellationRequested do
                        do! ports.Delay interval cancellation
                        if not cancellation.IsCancellationRequested then
                            this.Notify WorkWakeTrigger.ManualReconcile |> ignore
                with
                | :? OperationCanceledException when cancellation.IsCancellationRequested -> ()
                | ex ->
                    // A broken clock is not authority to continuously retry.
                    // Surface the failure, and stop the periodic task.
                    let result =
                        SchedulerHostOutcome.NeedsAttention(
                            "HostClockFailed",
                            "Repair the client-owned rescan clock; hints are not durable: " + ex.Message)
                    lock sync (fun () -> lastOutcome <- Some result)
                    try ports.Report result with _ -> ()
            } :> Task

        /// Requires explicit opt-in. On start, reconcile durable state even if
        /// all transient notifications were lost while this client was down.
        member _.Start() =
            lock sync (fun () ->
                if not enabled || closed || started then false
                else
                    started <- true
                    pending.Add WorkWakeTrigger.ManualReconcile |> ignore
                    wakePump ()
                    pump <- Some(Task.Run(Func<Task>(fun () -> this.Pump stop.Token)))
                    timer <- Some(Task.Run(Func<Task>(fun () -> this.Timer stop.Token)))
                    true)

        /// Cancels both bounded loops and awaits an in-flight Wake, including
        /// a concurrent manual step. Injected read/execute callbacks must be
        /// themselves bounded; synchronous external work cannot be preempted.
        /// Callers that need a full join should await this instead of relying
        /// on a cancellation-only signal.
        member _.StopAsync() : Task =
            let tasks =
                lock sync (fun () ->
                    closed <- true
                    pending.Clear()
                    let active = stepFinished |> Option.map (fun doneStep -> doneStep.Task :> Task)
                    [ pump; timer; active ] |> List.choose id |> List.toArray)
            stop.Cancel()
            Task.WhenAll tasks

        member _.Dispose() =
            // Dispose called from Wake/Report itself must not wait for its own
            // pump task; the pump exits once the callback unwinds.
            let reentrant =
                lock sync (fun () ->
                    inStep && stepThreadId = Environment.CurrentManagedThreadId)
            let completion = this.StopAsync()
            if not reentrant then completion.GetAwaiter().GetResult()

        interface IDisposable with
            member this.Dispose() = this.Dispose()

        interface IAsyncDisposable with
            member this.DisposeAsync() = ValueTask(this.StopAsync())
