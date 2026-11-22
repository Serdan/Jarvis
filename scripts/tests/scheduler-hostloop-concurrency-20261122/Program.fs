module HostLoopConcurrencyProperty

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop

// Source-bound executable: it uses the real, unchanged Client.Host through
// a ProjectReference. No source copy, host mock, shared fsproj edit or live port.
let heads () =
    Ok { AccountHead = String.replicate 40 "a"
         ProjectHead = String.replicate 40 "b" }

let check seed scenario condition reason =
    if not condition then
        invalidOp (sprintf "FAIL seed=%d scenario=%d: %s" seed scenario reason)

let bounded (task: Task) =
    task.WaitAsync(TimeSpan.FromSeconds 5.)

let runCase seed scenario faultInject : Task<bool> =
    task {
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let callbacks = ConcurrentQueue<WorkWakeTrigger list>()
        let reports = ConcurrentQueue<SchedulerHostOutcome>()
        let reads = ConcurrentQueue<int>()
        let mutable current: WorkQueueSchedulerHostLoop.Host option = None
        let mutable reentrant: SchedulerHostOutcome option = None

        let wake (signals: WorkWakeTrigger list) =
            callbacks.Enqueue(signals)
            if faultInject then callbacks.Enqueue(signals) // deliberately faulty external port
            if scenario = 4 then
                reentrant <- Some(current.Value.RunOnce [WorkWakeTrigger.ReviewAccepted])
            entered.TrySetResult(()) |> ignore
            release.Task.GetAwaiter().GetResult()
            WorkWakeResult.Dormant

        let ports =
            { ReadVerifiedHeads = (fun () -> reads.Enqueue(1); heads())
              Wake = wake
              Report = reports.Enqueue
              Delay = (fun _ cancellation ->
                Task.Delay(Timeout.InfiniteTimeSpan, cancellation) :> Task) }

        use host = new WorkQueueSchedulerHostLoop.Host(ports, enabled = true)
        current <- Some host
        let manual =
            Task.Run(Func<SchedulerHostOutcome>(fun () ->
                host.RunOnce [WorkWakeTrigger.ManualReconcile]))
        do! bounded entered.Task

        // The original NUnit RED checks the task wrapper's completed flag
        // immediately after StopAsync. StopAsync only knows about Host's
        // stepFinished TCS, not the caller-owned Task.Run wrapper.
        if scenario = 1 then
            check seed scenario
                (host.RunOnce [WorkWakeTrigger.DependencyCommitted] = SchedulerHostOutcome.Busy)
                "overlapping manual step was not Busy"
        if scenario = 2 then
            check seed scenario (host.Start()) "start during blocked manual step should succeed"
        if scenario = 4 then
            check seed scenario (reentrant = Some SchedulerHostOutcome.Busy)
                "same-thread reentrant step was not Busy"

        let stopped = host.StopAsync()
        check seed scenario (not stopped.IsCompleted) "StopAsync returned before blocked Wake"
        let alsoStopped = host.StopAsync()
        check seed scenario (not alsoStopped.IsCompleted) "second StopAsync lost active step"
        let disposing =
            if scenario = 3 then
                Some(Task.Run(Action(fun () -> host.Dispose())) :> Task)
            else None

        check seed scenario (host.RunOnce [WorkWakeTrigger.WorkCreated] = SchedulerHostOutcome.Stopped)
            "work admitted after StopAsync"
        check seed scenario (not (host.Notify WorkWakeTrigger.WorkCreated))
            "notification accepted after StopAsync"

        release.TrySetResult(()) |> ignore
        do! bounded stopped
        do! bounded alsoStopped
        let earlyCompletion = not manual.IsCompletedSuccessfully
        let! outcome = manual.WaitAsync(TimeSpan.FromSeconds 5.)
        match disposing with
        | Some d -> do! bounded d
        | None -> ()

        check seed scenario (outcome = SchedulerHostOutcome.Reconciled WorkWakeResult.Dormant)
            "in-flight result altered or cancelled"
        check seed scenario (callbacks.Count = 1)
            "unexpected number of coordinator invocations"
        check seed scenario (reports.Count = 1) "in-flight outcome not reported exactly once"
        check seed scenario (reads.Count = 1) "verified heads not read exactly once"
        check seed scenario (not host.IsRunning) "background loop not stopped"
        check seed scenario (not (host.Start())) "host restarted after terminal Stop"
        return earlyCompletion
    } :> Task<bool>

let runClosed seed =
    task {
        let callbacks = ConcurrentQueue<int>()
        let ports =
            { ReadVerifiedHeads = heads
              Wake = (fun _ -> callbacks.Enqueue(1); WorkWakeResult.Dormant)
              Report = ignore
              Delay = (fun _ token ->
                  Task.Delay(Timeout.InfiniteTimeSpan, token) :> Task) }
        use host = new WorkQueueSchedulerHostLoop.Host(ports, enabled = true)
        do! bounded (host.StopAsync())
        check seed 5
            (host.RunOnce [WorkWakeTrigger.ManualReconcile] = SchedulerHostOutcome.Stopped)
            "closed host reconciled after stop"
        check seed 5 (not (host.Notify WorkWakeTrigger.WorkCreated))
            "closed host accepted hint"
        check seed 5 (callbacks.Count = 0) "closed host executed effect"
    }

let runAll seed count faultInject =
    task {
        let mutable earlyCount = 0
        for i in 0 .. count - 1 do
            // Independently replayable scenario selection; every scenario
            // occurs in each block, with fresh hosts and task gates.
            let scenario = (i + seed % 5) % 5
            try
                let! early = runCase seed scenario faultInject
                if early then earlyCount <- earlyCount + 1
            with ex ->
                failwithf "TRACE seed=%d iteration=%d scenario=%d: %s" seed i scenario ex.Message
        do! runClosed seed
        printfn "PASS seed=%d iterations=%d source-bound-host=true early-stop-before-TaskRun-return=%d"
            seed count earlyCount
    }

[<EntryPoint>]
let main argv =
    try
        let seed = if argv.Length > 0 then int argv[0] else 20261122
        let count = if argv.Length > 1 then int argv[1] else 100
        let negative = argv.Length > 2 && argv[2] = "--negative"
        runAll seed count negative |> fun x -> x.GetAwaiter().GetResult()
        if negative then
            failwith "NEGATIVE ORACLE FAILURE: injected duplicate Wake was not rejected"
        0
    with ex ->
        eprintfn "%s" ex.Message
        1
