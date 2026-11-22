module Client.Tests.WorkQueueSchedulerHostLoopTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Client.WorkQueueReactiveWake
open Client.WorkQueueSchedulerHostLoop

type private ControlledClock() =
    let waiters = ConcurrentQueue<TaskCompletionSource<unit>>()

    member _.Delay (_: TimeSpan) (cancellation: CancellationToken) : Task =
        let source = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let registration =
            cancellation.Register(fun () -> source.TrySetCanceled(cancellation) |> ignore)
        source.Task.ContinueWith(fun (_: Task) -> registration.Dispose()) |> ignore
        waiters.Enqueue source
        source.Task :> Task

    member _.HasWaitingTick = not waiters.IsEmpty

    member _.Tick() =
        let mutable source = Unchecked.defaultof<TaskCompletionSource<unit>>
        if waiters.TryDequeue(&source) then
            source.TrySetResult(()) |> ignore
            true
        else false

[<TestFixture>]
type WorkQueueSchedulerHostLoopTests() =
    let heads () =
        Ok { AccountHead = String.replicate 40 "a"
             ProjectHead = String.replicate 40 "b" }

    let ports (clock: ControlledClock) read wake report : SchedulerHostPorts =
        { ReadVerifiedHeads = read
          Wake = wake
          Report = report
          Delay = fun duration cancellation -> clock.Delay duration cancellation }

    [<Test>]
    member _.DefaultOffIsQuiescentEvenWhenStartedOrManuallySignalled() =
        let clock = ControlledClock()
        let mutable reads, callbacks, reports = 0, 0, 0
        use host =
            new WorkQueueSchedulerHostLoop.Host(
                ports clock
                    (fun () -> reads <- reads + 1; heads ())
                    (fun _ -> callbacks <- callbacks + 1; WorkWakeResult.Dormant)
                    (fun _ -> reports <- reports + 1))
        Assert.That(host.Start(), Is.False)
        Assert.That(host.Notify WorkWakeTrigger.WorkCreated, Is.False)
        Assert.That(host.RunOnce [WorkWakeTrigger.ManualReconcile],
                    Is.EqualTo(SchedulerHostOutcome.Disabled))
        Assert.That(host.IsRunning, Is.False)
        Assert.That(clock.HasWaitingTick, Is.False)
        Assert.That(reads, Is.Zero)
        Assert.That(callbacks, Is.Zero)
        Assert.That(reports, Is.Zero)

    [<Test>]
    member _.ManualStepRechecksVerifiedHeadsBeforeTheCoordinator() =
        let clock = ControlledClock()
        let seen = ResizeArray<string>()
        use host =
            new WorkQueueSchedulerHostLoop.Host(
                ports clock
                    (fun () -> seen.Add "heads"; heads ())
                    (fun signals ->
                        Assert.That(signals, Is.EqualTo(box [WorkWakeTrigger.DependencyCommitted]))
                        seen.Add "coordinator"
                        WorkWakeResult.Dormant)
                    (fun _ -> seen.Add "report"), enabled = true)
        Assert.That(host.RunOnce [],
                    Is.EqualTo(SchedulerHostOutcome.Dormant))
        Assert.That(seen, Is.Empty)
        let result = host.RunOnce [WorkWakeTrigger.DependencyCommitted]
        Assert.That(result, Is.EqualTo(SchedulerHostOutcome.Reconciled WorkWakeResult.Dormant))
        Assert.That(seen, Is.EqualTo(box ["heads"; "coordinator"; "report"]))
        Assert.That(host.LastOutcome, Is.EqualTo(Some result))

    [<Test>]
    member _.HeadFailuresAndCoordinatorExceptionsCannotAuthorizeAnEffect() =
        let clock = ControlledClock()
        let mutable effects = 0
        let readFailure =
            ports clock (fun () -> Error "account remote unavailable")
                (fun _ -> effects <- effects + 1; WorkWakeResult.Dormant) ignore
        use missing =
            new WorkQueueSchedulerHostLoop.Host(readFailure, enabled = true)
        match missing.RunOnce [WorkWakeTrigger.WorkTransitionCommitted] with
        | SchedulerHostOutcome.NeedsAttention("CanonicalHeadsUnavailable", text) ->
            Assert.That(text, Does.Contain("account remote unavailable"))
        | other -> Assert.Fail(sprintf "Expected strict canonical-head failure: %A" other)
        Assert.That(effects, Is.Zero)
        let blank =
            ports clock (fun () -> Ok { AccountHead = ""; ProjectHead = "valid" })
                (fun _ -> effects <- effects + 1; WorkWakeResult.Dormant) ignore
        use empty = new WorkQueueSchedulerHostLoop.Host(blank, enabled = true)
        match empty.RunOnce [WorkWakeTrigger.ReviewAccepted] with
        | SchedulerHostOutcome.NeedsAttention("CanonicalHeadsUnavailable", _) -> ()
        | other -> Assert.Fail(sprintf "Blank head must fail closed: %A" other)
        Assert.That(effects, Is.Zero)
        let throws =
            ports clock heads (fun _ -> failwith "unknown original outcome") ignore
        use broken = new WorkQueueSchedulerHostLoop.Host(throws, enabled = true)
        match broken.RunOnce [WorkWakeTrigger.CapacityReleased] with
        | SchedulerHostOutcome.NeedsAttention("CoordinatorFailure", action) ->
            Assert.That(action, Does.Contain("unknown original outcome"))
        | other -> Assert.Fail(sprintf "Coordinator failure must not be silently retried: %A" other)

    [<Test>]
    member _.HardDependencyAndCapacityRemainDormantUntilBothAreDurablyReady() =
        let clock = ControlledClock()
        let mutable dependency, capacity = false, false
        let mutable calls = 0
        use host =
            new WorkQueueSchedulerHostLoop.Host(
                ports clock heads
                    (fun _ ->
                        if not dependency || not capacity then WorkWakeResult.Dormant
                        else
                            calls <- calls + 1
                            WorkWakeResult.NoCapacity)
                    ignore, enabled = true)
        host.RunOnce [WorkWakeTrigger.WorkCreated] |> ignore
        Assert.That(calls, Is.Zero)
        dependency <- true
        host.RunOnce [WorkWakeTrigger.DependencyCommitted] |> ignore
        Assert.That(calls, Is.Zero)
        capacity <- true
        host.RunOnce [WorkWakeTrigger.CapacityReleased] |> ignore
        Assert.That(calls, Is.EqualTo(1))
        Assert.That(host.IsRunning, Is.False, "Manual-only dry run never creates loops.")

    [<Test>]
    member _.SameThreadReentryIsDeferredRatherThanNestingWake() =
        let clock = ControlledClock()
        let mutable calls = 0
        let mutable nested: SchedulerHostOutcome option = None
        let mutable host: WorkQueueSchedulerHostLoop.Host option = None
        let wake _ =
            calls <- calls + 1
            if calls = 1 then
                nested <- Some(host.Value.RunOnce [WorkWakeTrigger.IntegrationCommitted])
            WorkWakeResult.Dormant
        use current =
            new WorkQueueSchedulerHostLoop.Host(ports clock heads wake ignore, enabled = true)
        host <- Some current
        current.RunOnce [WorkWakeTrigger.ReviewAccepted] |> ignore
        Assert.That(nested, Is.EqualTo(Some SchedulerHostOutcome.Busy))
        Assert.That(calls, Is.EqualTo(1))
        current.RunOnce [] |> ignore
        Assert.That(calls, Is.EqualTo(2), "Deferred signal is not lost.")

    [<Test>]
    member _.ManualStepDoesNotRetryUnknownOutcomeInAnUnboundedLoop() =
        let clock = ControlledClock()
        let mutable reads, calls = 0, 0
        use host =
            new WorkQueueSchedulerHostLoop.Host(
                ports clock
                    (fun () -> reads <- reads + 1; heads ())
                    (fun _ ->
                        calls <- calls + 1
                        WorkWakeResult.NeedsAttention(
                            "OriginalOperationUnknown", "Reconcile original journal receipt."))
                    ignore, enabled = true)
        let result = host.RunOnce [WorkWakeTrigger.WorkTransitionCommitted]
        match result with
        | SchedulerHostOutcome.Reconciled(WorkWakeResult.NeedsAttention("OriginalOperationUnknown", _)) -> ()
        | other -> Assert.Fail(sprintf "Uncertain operation must surface: %A" other)
        Assert.That(reads, Is.EqualTo(1))
        Assert.That(calls, Is.EqualTo(1))
        Assert.That(host.RunOnce [], Is.EqualTo(SchedulerHostOutcome.Dormant))
        Assert.That(calls, Is.EqualTo(1))

    [<Test>]
    member _.TwoHostInstancesCannotBypassAnInjectedCanonicalOriginalClaim() =
        let clock = ControlledClock()
        let authority = obj ()
        let mutable originalClaimed = false
        let mutable callbacks = 0
        let claimed _ =
            lock authority (fun () ->
                if originalClaimed then
                    WorkWakeResult.NeedsAttention(
                        "OriginalAlreadyClaimed", "Inspect the canonical original and account token.")
                else
                    originalClaimed <- true
                    callbacks <- callbacks + 1
                    WorkWakeResult.Dormant)
        use first =
            new WorkQueueSchedulerHostLoop.Host(ports clock heads claimed ignore, enabled = true)
        use second =
            new WorkQueueSchedulerHostLoop.Host(ports clock heads claimed ignore, enabled = true)
        let a =
            Task.Run(Func<SchedulerHostOutcome>(fun () ->
                first.RunOnce [WorkWakeTrigger.WorkCreated]))
        let b =
            Task.Run(Func<SchedulerHostOutcome>(fun () ->
                second.RunOnce [WorkWakeTrigger.CapacityReleased]))
        Task.WaitAll([| a :> Task; b :> Task |])
        Assert.That(callbacks, Is.EqualTo(1))
        let results = [a.Result; b.Result]
        Assert.That(results |> List.exists (function
            | SchedulerHostOutcome.Reconciled(WorkWakeResult.NeedsAttention("OriginalAlreadyClaimed", _)) -> true
            | _ -> false), Is.True)

    [<Test>]
    member _.CoalescesConcurrentHintsInOneInitialGitReconciliation() : Task =
        task {
            let clock = ControlledClock()
            let batches = ConcurrentQueue<WorkWakeTrigger list>()
            let reported = ConcurrentQueue<SchedulerHostOutcome>()
            use host =
                new WorkQueueSchedulerHostLoop.Host(
                    ports clock heads
                        (fun batch -> batches.Enqueue batch; WorkWakeResult.Dormant)
                        reported.Enqueue, enabled = true)
            Assert.That(host.Notify WorkWakeTrigger.WorkCreated, Is.True)
            Assert.That(host.Notify WorkWakeTrigger.DependencyCommitted, Is.True)
            Assert.That(host.Notify WorkWakeTrigger.WorkCreated, Is.False)
            Assert.That(host.Start(), Is.True)
            Assert.That(SpinWait.SpinUntil((fun () -> batches.Count = 1), 3000), Is.True)
            Assert.That(batches.Count, Is.EqualTo(1))
            let mutable batch = []
            Assert.That(batches.TryDequeue(&batch), Is.True)
            Assert.That(batch,
                        Is.EquivalentTo([WorkWakeTrigger.WorkCreated;
                                         WorkWakeTrigger.DependencyCommitted;
                                         WorkWakeTrigger.ManualReconcile]))
            Assert.That(SpinWait.SpinUntil((fun () -> reported.Count = 1), 3000), Is.True)
            do! host.StopAsync()
            Assert.That(host.IsRunning, Is.False)
        } :> Task

    [<Test>]
    member _.PeriodicFullRescanRecoversLostHintsAndRestartState() : Task =
        task {
            let clock = ControlledClock()
            let calls = ConcurrentQueue<WorkWakeTrigger list>()
            let mutable eligible = false
            use host =
                new WorkQueueSchedulerHostLoop.Host(
                    ports clock heads
                        (fun signals ->
                            calls.Enqueue signals
                            if eligible then WorkWakeResult.NoCapacity
                            else WorkWakeResult.Dormant)
                        ignore, enabled = true,
                    fullRescanInterval = TimeSpan.FromMinutes 3.0)
            Assert.That(host.Start(), Is.True)
            Assert.That(SpinWait.SpinUntil((fun () -> calls.Count = 1), 3000), Is.True)
            Assert.That(SpinWait.SpinUntil((fun () -> clock.HasWaitingTick), 3000), Is.True)
            // The durable state changed while its transient notification was
            // lost; a timer scan must recover without any Notify call.
            eligible <- true
            Assert.That(clock.Tick(), Is.True)
            Assert.That(SpinWait.SpinUntil((fun () -> calls.Count = 2), 3000), Is.True)
            match host.LastOutcome with
            | Some(SchedulerHostOutcome.Reconciled WorkWakeResult.NoCapacity) -> ()
            | other -> Assert.Fail(sprintf "Expected full rescan after lost hint: %A" other)
            do! host.StopAsync()
            Assert.That(host.Notify WorkWakeTrigger.WorkCreated, Is.False)
        } :> Task

    [<Test>]
    member _.StopJoinsTheInflightStepAndCancelsTheClock() : Task =
        task {
            let clock = ControlledClock()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use host =
                new WorkQueueSchedulerHostLoop.Host(
                    ports clock heads
                        (fun _ ->
                            entered.TrySetResult(()) |> ignore
                            release.Task.GetAwaiter().GetResult()
                            WorkWakeResult.Dormant)
                        ignore, enabled = true)
            Assert.That(host.Start(), Is.True)
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
            let completion = host.StopAsync()
            Assert.That(completion.IsCompleted, Is.False)
            release.TrySetResult(()) |> ignore
            do! completion.WaitAsync(TimeSpan.FromSeconds 5.0)
            Assert.That(host.IsRunning, Is.False)
            Assert.That(host.Notify WorkWakeTrigger.ManualReconcile, Is.False)
            Assert.That(host.RunOnce [WorkWakeTrigger.WorkCreated],
                        Is.EqualTo(SchedulerHostOutcome.Stopped))
        } :> Task

    [<Test>]
    member _.StopJoinsConcurrentManualStepWithoutBackgroundLoops() : Task =
        task {
            let clock = ControlledClock()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let reported = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let wakeCalls = [| 0 |]
            let reportCalls = [| 0 |]
            use host =
                new WorkQueueSchedulerHostLoop.Host(
                    ports clock heads
                        (fun _ ->
                            Interlocked.Increment(&wakeCalls[0]) |> ignore
                            entered.TrySetResult(()) |> ignore
                            release.Task.GetAwaiter().GetResult()
                            WorkWakeResult.Dormant)
                        (fun _ ->
                            Interlocked.Increment(&reportCalls[0]) |> ignore
                            reported.TrySetResult(()) |> ignore), enabled = true)
            let manuallyRunning =
                Task.Run(Func<SchedulerHostOutcome>(fun () ->
                    host.RunOnce [WorkWakeTrigger.ManualReconcile]))
            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                let stopped = host.StopAsync()
                try
                    // Stop owns the in-flight Wake/Report, not the caller's Task.Run wrapper.
                    Assert.That(stopped.IsCompleted, Is.False)
                finally
                    release.TrySetResult(()) |> ignore
                do! stopped.WaitAsync(TimeSpan.FromSeconds 5.0)
                Assert.That(reported.Task.IsCompletedSuccessfully, Is.True)
                Assert.That(Volatile.Read(&wakeCalls[0]), Is.EqualTo(1))
                Assert.That(Volatile.Read(&reportCalls[0]), Is.EqualTo(1))
                Assert.That(host.IsRunning, Is.False)
                Assert.That(host.Notify WorkWakeTrigger.ManualReconcile, Is.False)
                Assert.That(host.RunOnce [WorkWakeTrigger.WorkCreated],
                            Is.EqualTo(SchedulerHostOutcome.Stopped))
                Assert.That(Volatile.Read(&wakeCalls[0]), Is.EqualTo(1))
                Assert.That(Volatile.Read(&reportCalls[0]), Is.EqualTo(1))
                // The external wrapper may finish after Stop; await it separately.
                let! outcome = manuallyRunning.WaitAsync(TimeSpan.FromSeconds 5.0)
                Assert.That(outcome,
                            Is.EqualTo(SchedulerHostOutcome.Reconciled WorkWakeResult.Dormant))
            finally
                release.TrySetResult(()) |> ignore
        } :> Task

    [<Test>]
    member _.DisposeFromSameThreadCallbackDoesNotDeadlockThePump() : Task =
        task {
            let clock = ControlledClock()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable host: WorkQueueSchedulerHostLoop.Host option = None
            let wake _ =
                host.Value.Dispose()
                entered.TrySetResult(()) |> ignore
                WorkWakeResult.Dormant
            let current =
                new WorkQueueSchedulerHostLoop.Host(
                    ports clock heads wake ignore, enabled = true)
            host <- Some current
            Assert.That(current.Start(), Is.True)
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
            do! current.StopAsync().WaitAsync(TimeSpan.FromSeconds 5.0)
            Assert.That(current.IsRunning, Is.False)
        } :> Task
