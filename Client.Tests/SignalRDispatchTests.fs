namespace Client.Tests.SignalR

open Microsoft.AspNetCore.SignalR

type DispatchProbeHub() =
    inherit Hub()
    member _.AcceptReply(_correlationId: string) = System.Threading.Tasks.Task.CompletedTask
    member this.AcceptWithAck(correlationId: string) =
        this.Clients.Caller.SendCoreAsync("ReplyReceived", [| box correlationId |], System.Threading.CancellationToken.None)

module DispatchProbeTests =
    open System
    open System.IO
    open System.Threading
    open System.Threading.Tasks
    open Client.SignalR
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Hosting
    open Microsoft.AspNetCore.SignalR
    open Microsoft.AspNetCore.SignalR.Client
    open Microsoft.Extensions.DependencyInjection
    open NUnit.Framework

    // Reproduces the actual SignalR transport behavior rather than invoking
    // the command handler directly. This is a diagnostic control: it should
    // keep passing after Jarvis decouples its own command handler.
    [<Test>]
    let ``SignalR awaits an unfinished client callback before delivering the next invocation`` () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        use server = builder.Build()
        server.MapHub<DispatchProbeHub>("/probe") |> ignore
        do! server.StartAsync()

        let endpoint = server.Urls |> Seq.head
        let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let fastEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()
        use registration =
            connection.On<string>(
                "ReceiveCommand",
                Func<string, Task>(fun command ->
                    task {
                        if command = "slow" then
                            slowEntered.TrySetResult(()) |> ignore
                            do! releaseSlow.Task
                        else
                            fastEntered.TrySetResult(()) |> ignore
                    } :> Task))
        do! connection.StartAsync()

        let sender = server.Services.GetRequiredService<IHubContext<DispatchProbeHub>>()
        do! sender.Clients.All.SendAsync("ReceiveCommand", "slow")
        do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! sender.Clients.All.SendAsync("ReceiveCommand", "fast")

        let! first = Task.WhenAny(fastEntered.Task, Task.Delay(200))
        let fastWasBlocked = not (obj.ReferenceEquals(first, fastEntered.Task))
        releaseSlow.TrySetResult(()) |> ignore
        do! fastEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()
        Assert.That(fastWasBlocked, Is.True,
            "The control requires actual SignalR callback serialization, not a synthetic mock.")
    }

    [<Test>]
    let ``bounded dispatcher does not let one blocked command delay another`` () = task {
        let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let fastFinished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let measurements = System.Collections.Concurrent.ConcurrentBag<CommandDispatchTiming>()
        let queue = CommandDispatchQueue(2, 8, measurements.Add)

        let slow () : Task = task {
            slowEntered.TrySetResult(()) |> ignore
            do! releaseSlow.Task
        }

        let fast () : Task = task {
            fastFinished.TrySetResult(()) |> ignore
        }

        Assert.That(queue.TrySubmit("slow", slow), Is.True)
        do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(queue.TrySubmit("fast", fast), Is.True)
        let! completedFirst = Task.WhenAny(fastFinished.Task, Task.Delay(1000))
        releaseSlow.TrySetResult(()) |> ignore
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)

        Assert.That(completedFirst, Is.SameAs(fastFinished.Task),
            "One blocked ledger command must not occupy the only progress lane.")
        Assert.That(measurements.Count, Is.EqualTo(2))
        Assert.That(measurements |> Seq.forall (fun item -> item.FailureType.IsNone), Is.True)
    }

    [<Test>]
    let ``bounded dispatcher rejects saturation and drains accepted commands`` () = task {
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let finished = System.Collections.Concurrent.ConcurrentBag<string>()
        let queue = CommandDispatchQueue(1, 1, (fun _ -> ()))

        let active () : Task = task {
            entered.TrySetResult(()) |> ignore
            do! release.Task
            finished.Add("active")
        }
        let queued () : Task = task {
            finished.Add("queued")
        }
        let overflow () : Task = task {
            finished.Add("overflow")
        }

        Assert.That(queue.TrySubmit("active", active), Is.True)
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(queue.TrySubmit("queued", queued), Is.True)
        Assert.That(queue.TrySubmit("overflow", overflow), Is.False)

        let draining = queue.Complete()
        Assert.That(queue.TrySubmit("after-complete", fun () -> Task.CompletedTask), Is.False)
        release.TrySetResult(()) |> ignore
        do! draining.WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(finished.Count, Is.EqualTo(2))
        Assert.That(finished |> Seq.contains "active", Is.True)
        Assert.That(finished |> Seq.contains "queued", Is.True)
    }

    [<Test>]
    let ``real SignalR callbacks use independent bounded workers and can invoke the hub for replies`` () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        use server = builder.Build()
        server.MapHub<DispatchProbeHub>("/probe") |> ignore
        do! server.StartAsync()

        let endpoint = server.Urls |> Seq.head
        let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let fastReplied = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let queue = CommandDispatchQueue(2, 8, (fun _ -> ()))
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()

        use registration =
            connection.On<string>(
                "ReceiveCommand",
                Func<string, Task>(fun command ->
                    let handler () : Task = task {
                        if command = "slow" then
                            slowEntered.TrySetResult(()) |> ignore
                            do! releaseSlow.Task
                        do! connection.InvokeAsync("AcceptReply", command)
                        if command = "fast" then
                            fastReplied.TrySetResult(()) |> ignore
                    }
                    if queue.TrySubmit(command, handler) then Task.CompletedTask
                    else Task.FromException(InvalidOperationException("Unexpected saturated test queue"))))
        do! connection.StartAsync()

        let sender = server.Services.GetRequiredService<IHubContext<DispatchProbeHub>>()
        do! sender.Clients.All.SendAsync("ReceiveCommand", "slow")
        do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! sender.Clients.All.SendAsync("ReceiveCommand", "fast")
        let! first = Task.WhenAny(fastReplied.Task, Task.Delay(1000))
        releaseSlow.TrySetResult(()) |> ignore
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()

        Assert.That(first, Is.SameAs(fastReplied.Task),
            "The fast response must reach the server without waiting for the slow command.")
    }

    [<Test>]
    let ``unhandled command reports uncertain outcome and subsequent commands still complete`` () = task {
        let failed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let succeeded = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let measurements = System.Collections.Concurrent.ConcurrentBag<CommandDispatchTiming>()
        let queue = CommandDispatchQueue(1, 4, measurements.Add)
        let thrown () : Task = Task.FromException(InvalidOperationException("secret-command-body"))
        let onUnhandled (ex: exn) : Task = task {
            Assert.That(ex, Is.TypeOf<InvalidOperationException>())
            failed.TrySetResult(()) |> ignore
        }
        let success () : Task = task {
            succeeded.TrySetResult(()) |> ignore
        }
        Assert.That(queue.TrySubmit("first", thrown, onUnhandled), Is.True)
        Assert.That(queue.TrySubmit("second", success), Is.True)
        do! failed.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! succeeded.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        let failedTiming = measurements |> Seq.find (fun item -> item.CorrelationId = "first")
        Assert.That(failedTiming.FailureType, Is.EqualTo(Some "InvalidOperationException"))
        Assert.That(failedTiming.ToString().Contains("secret-command-body"), Is.False)
    }

    [<Test>]
    let ``telemetry exceptions cannot terminate a dispatch worker`` () = task {
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let queue = CommandDispatchQueue(1, 4, (fun _ -> failwith "Telemetry endpoint failed"))
        let complete () : Task = task {
            completed.TrySetResult(()) |> ignore
        }
        Assert.That(queue.TrySubmit("one", fun () -> Task.CompletedTask), Is.True)
        Assert.That(queue.TrySubmit("two", complete), Is.True)
        do! completed.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
    }

    [<Test>]
    let ``real SignalR overflow returns a response without executing a rejected command`` () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        use server = builder.Build()
        server.MapHub<DispatchProbeHub>("/probe") |> ignore
        do! server.StartAsync()
        let endpoint = server.Urls |> Seq.head
        let sender = server.Services.GetRequiredService<IHubContext<DispatchProbeHub>>()
        let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let rejected = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let executed = System.Collections.Concurrent.ConcurrentBag<string>()
        let queue = CommandDispatchQueue(1, 1, (fun _ -> ()))
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()

        use responseRegistration =
            connection.On<string>(
                "ReplyReceived",
                Func<string, Task>(fun id ->
                    if id = "overflow" then rejected.TrySetResult(()) |> ignore
                    Task.CompletedTask))
        use commandRegistration =
            connection.On<string>(
                "ReceiveCommand",
                Func<string, Task>(fun id ->
                    let execute () : Task = task {
                        executed.Add(id)
                        if id = "slow" then
                            slowEntered.TrySetResult(()) |> ignore
                            do! releaseSlow.Task
                        do! connection.InvokeAsync("AcceptWithAck", id)
                    }
                    if queue.TrySubmit(id, execute) then Task.CompletedTask
                    else
                        // Production uses SendCoreAsync, not an awaited hub
                        // invocation, for explicit saturation rejection.
                        connection.SendCoreAsync(
                            "AcceptWithAck", [| box id |], CancellationToken.None)))

        do! connection.StartAsync()
        do! sender.Clients.All.SendAsync("ReceiveCommand", "slow")
        do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! sender.Clients.All.SendAsync("ReceiveCommand", "queued")
        do! sender.Clients.All.SendAsync("ReceiveCommand", "overflow")
        let! first = Task.WhenAny(rejected.Task, Task.Delay(1000))
        let promptRejection = obj.ReferenceEquals(first, rejected.Task)
        releaseSlow.TrySetResult(()) |> ignore
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()

        Assert.That(promptRejection, Is.True,
            "Queue saturation must produce a prompt correlated reply before a stalled command is released.")
        Assert.That(executed.Count, Is.EqualTo(2),
            "An overflow rejection must not execute a command or affect already admitted work.")
        Assert.That(executed |> Seq.contains "slow", Is.True)
        Assert.That(executed |> Seq.contains "queued", Is.True)
    }

    [<Test>]
    let ``a failed recovery response cannot terminate the dispatch worker`` () = task {
        let recovered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let observations = System.Collections.Concurrent.ConcurrentBag<CommandDispatchTiming>()
        let queue = CommandDispatchQueue(1, 2, observations.Add)
        let throws () : Task = Task.FromException(InvalidOperationException("private-payload"))
        let fallback (_: exn) : Task = Task.FromException(IOException("disconnected"))
        let next () : Task = task { recovered.TrySetResult(()) |> ignore }
        Assert.That(queue.TrySubmit("failure-id", throws, fallback), Is.True)
        Assert.That(queue.TrySubmit("next-id", next), Is.True)
        do! recovered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(observations.Count, Is.EqualTo(2))
        Assert.That(
            observations |> Seq.exists (fun timing ->
                timing.CorrelationId = "failure-id"
                && timing.FailureType = Some "InvalidOperationException"), Is.True)
        Assert.That(observations |> Seq.forall (fun timing ->
            not (timing.ToString().Contains("private-payload"))), Is.True)
    }

    [<Test>]
    let ``a blocked accepted command fails on disconnect and new work recovers after reconnect`` () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        use server = builder.Build()
        server.MapHub<DispatchProbeHub>("/probe") |> ignore
        do! server.StartAsync()
        let endpoint = server.Urls |> Seq.head
        let sender = server.Services.GetRequiredService<IHubContext<DispatchProbeHub>>()
        let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let unavailableReply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let recovered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let queue = CommandDispatchQueue(1, 4, (fun _ -> ()))
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()
        use responseRegistration =
            connection.On<string>(
                "ReplyReceived",
                Func<string, Task>(fun id ->
                    if id = "after-reconnect" then recovered.TrySetResult(()) |> ignore
                    Task.CompletedTask))
        use commandRegistration =
            connection.On<string>(
                "ReceiveCommand",
                Func<string, Task>(fun id ->
                    let execute () : Task = task {
                        if id = "before-disconnect" then
                            slowEntered.TrySetResult(()) |> ignore
                            do! releaseSlow.Task
                        do! connection.InvokeAsync("AcceptWithAck", id)
                    }
                    let recover (_: exn) : Task = task {
                        if id = "before-disconnect" then
                            unavailableReply.TrySetResult(()) |> ignore
                    }
                    if queue.TrySubmit(id, execute, recover) then Task.CompletedTask
                    else Task.FromException(InvalidOperationException("Unexpected saturation"))))

        do! connection.StartAsync()
        do! sender.Clients.All.SendAsync("ReceiveCommand", "before-disconnect")
        do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.StopAsync()
        releaseSlow.TrySetResult(()) |> ignore
        do! unavailableReply.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.StartAsync()
        do! sender.Clients.All.SendAsync("ReceiveCommand", "after-reconnect")
        do! recovered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()
    }

    [<Test>]
    let ``concurrent cheap command p50 p95 p99 improves while a source command is stalled`` () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        use server = builder.Build()
        server.MapHub<DispatchProbeHub>("/probe") |> ignore
        do! server.StartAsync()
        let endpoint = server.Urls |> Seq.head
        let sender = server.Services.GetRequiredService<IHubContext<DispatchProbeHub>>()
        let samples = 64

        let percentile (values: float array) p =
            let sorted = values |> Array.sort
            sorted[max 0 (min (sorted.Length - 1) (int (Math.Ceiling(float sorted.Length * p)) - 1))]

        let run useBoundedQueue = task {
            let slowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseSlow = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let allCheap = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let started = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()
            let latencies = System.Collections.Concurrent.ConcurrentBag<float>()
            let mutable completed = 0
            let queue = CommandDispatchQueue(4, 128, (fun _ -> ()))
            let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()

            let work (command: string) () : Task = task {
                if command = "slow-ledger-write" then
                    slowEntered.TrySetResult(()) |> ignore
                    do! releaseSlow.Task
                else
                    let beginAt = started[command]
                    latencies.Add(System.Diagnostics.Stopwatch.GetElapsedTime(beginAt).TotalMilliseconds)
                    if Interlocked.Increment(&completed) = samples then
                        allCheap.TrySetResult(()) |> ignore
            }

            use registration =
                connection.On<string>(
                    "ReceiveCommand",
                    Func<string, Task>(fun command ->
                        if useBoundedQueue then
                            if queue.TrySubmit(command, work command) then Task.CompletedTask
                            else Task.FromException(InvalidOperationException("Synthetic dispatch queue saturated"))
                        else
                            work command ()))
            do! connection.StartAsync()

            do! sender.Clients.All.SendAsync("ReceiveCommand", "slow-ledger-write")
            do! slowEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            for i in 1 .. samples do
                let label =
                    if i % 2 = 0 then $"get_job_result:{i}"
                    else $"read_file:{i}"
                started.TryAdd(label, System.Diagnostics.Stopwatch.GetTimestamp()) |> ignore
                do! sender.Clients.All.SendAsync("ReceiveCommand", label)

            let! first = Task.WhenAny(allCheap.Task, Task.Delay(350))
            let completedBeforeRelease = obj.ReferenceEquals(first, allCheap.Task)
            releaseSlow.TrySetResult(()) |> ignore
            do! allCheap.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
            do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 10.0)
            do! connection.DisposeAsync().AsTask()
            let values = latencies.ToArray()
            Assert.That(values.Length, Is.EqualTo(samples))
            return completedBeforeRelease,
                   percentile values 0.5,
                   percentile values 0.95,
                   percentile values 0.99
        }

        let! before = run false
        let! after = run true
        let beforeEarly, before50, before95, before99 = before
        let afterEarly, after50, after95, after99 = after
        TestContext.Progress.WriteLine(
            $"Synthetic SignalR cheap-command load (64 reads/job-status, 1 blocked ledger action) latency ms BEFORE p50={before50:F1} p95={before95:F1} p99={before99:F1}; AFTER p50={after50:F1} p95={after95:F1} p99={after99:F1}")

        Assert.That(beforeEarly, Is.False, "The baseline control must exhibit callback head-of-line blocking.")
        Assert.That(afterEarly, Is.True, "Fast operations should all complete while the slow action is still held.")
        Assert.That(after50, Is.LessThan(before50))
        do! server.StopAsync()
    }
