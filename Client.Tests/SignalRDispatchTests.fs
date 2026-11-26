namespace Client.Tests.SignalR

open Microsoft.AspNetCore.SignalR

type DispatchProbeHub() =
    inherit Hub()
    member _.AcceptReply(_correlationId: string) = System.Threading.Tasks.Task.CompletedTask
    member this.AcceptWithAck(correlationId: string) =
        this.Clients.Caller.SendCoreAsync("ReplyReceived", [| box correlationId |], System.Threading.CancellationToken.None)

// Gated real Hub acknowledgement proves that the production command reply
// handler returns its end-to-end Task, rather than detaching a second queue.
type ResponseCompletionGate() =
    member val Entered =
        System.Threading.Tasks.TaskCompletionSource<string>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously) with get
    member val Release =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously) with get

type ResponseCompletionHub(gate: ResponseCompletionGate) =
    inherit Hub()
    member _.SendClientResponse(correlationId: string, _payload: obj) : System.Threading.Tasks.Task =
        task {
            gate.Entered.TrySetResult(correlationId) |> ignore
            do! gate.Release.Task
        } :> System.Threading.Tasks.Task

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
    let ``mutate then throw keeps original correlation and recovery operation ID without replay`` () = task {
        let operationId = Guid.NewGuid().ToString("D")
        let command : Common.AgentCommand =
            Common.LedgerWriteCommand
                { ProjectName = "Jarvis"; Action = "Write"
                  OperationId = operationId; ProjectId = ""; LedgerId = ""
                  Epoch = 0; RemoteName = ""; ApprovedFetchUrl = ""
                  ApprovedPushUrl = ""; ExpectedTrustedHead = ""
                  ExpectedRequestHash = ""; AcknowledgeVisibility = false }
        let commandJson = System.Text.Json.JsonSerializer.Serialize command
        let captured =
            TaskCompletionSource<string * Common.AgentCommandResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable committedWrites = 0
        let queue = CommandDispatchQueue(1, 4, (fun _ -> ()))
        let correlationId = "original-correlation"
        let execute () : Task =
            Interlocked.Increment(&committedWrites) |> ignore
            // Crash after a simulated durable mutation but before the
            // regular response can be serialized or acknowledged.
            Task.FromException(InvalidOperationException("failure after commit"))
        let recover (_: exn) : Task =
            let response = CommandDispatchRecovery.unexpectedResponse commandJson
            captured.TrySetResult((correlationId, response)) |> ignore
            Task.CompletedTask
        Assert.That(queue.TrySubmit(correlationId, execute, recover), Is.True)
        let! originalCorrelation, response = captured.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(originalCorrelation, Is.EqualTo(correlationId))
        Assert.That(committedWrites, Is.EqualTo(1), "A failed response must not replay a mutation.")
        Assert.That(Common.AgentStateEvidence.isUnknown response, Is.True)
        match response.Error with
        | Some(Common.UncertainOutcome info) ->
            Assert.That(info.OperationId, Is.EqualTo(Some operationId))
            Assert.That(info.Message, Does.Contain("may already be committed"))
        | other -> Assert.Fail(sprintf "Expected typed uncertainty, got %A" other)
        let restored =
            System.Text.Json.JsonSerializer.Deserialize<Common.AgentCommandResponse>(
                System.Text.Json.JsonSerializer.Serialize response)
        Assert.That(Common.AgentStateEvidence.isUnknown restored, Is.True)
        Assert.That(queue.TrySubmit(correlationId, execute, recover), Is.False)
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

    [<Test>]
    let productionResponseHandlerHoldsTaskUntilCorrelatedReplyAck () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        let gate = ResponseCompletionGate()
        builder.Services.AddSingleton<ResponseCompletionGate>(gate) |> ignore
        use server = builder.Build()
        server.MapHub<ResponseCompletionHub>("/probe") |> ignore
        do! server.StartAsync()

        let endpoint = server.Urls |> Seq.head
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()
        do! connection.StartAsync()
        let runtime = Client.Runtime(".", Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        let command : Common.AgentCommand =
            Common.MessageCommand { ProjectName = None; Message = "reply-gate-probe" }
        let commandJson = System.Text.Json.JsonSerializer.Serialize<Common.AgentCommand>(command)
        let correlationId = "original-reply-id"
        let mutable delivered = false

        // Test the actual function called by Program.fs. A nested second
        // ingress previously returned Task.CompletedTask before the hub replied.
        let pending : Task =
            Client.SignalR.Client.receiveCommandAndReplyWithTiming
                connection runtime correlationId commandJson "Worker"
                (fun _ _ _ ok -> delivered <- ok)
        let! observed = gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        let releasedEarly = pending.IsCompleted
        gate.Release.TrySetResult(()) |> ignore
        do! pending.WaitAsync(TimeSpan.FromSeconds 5.0)
        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()

        Assert.That(observed, Is.EqualTo(correlationId))
        Assert.That(releasedEarly, Is.False,
                    "The worker must await the real reply Task, not a detached ingress.")
        Assert.That(delivered, Is.True)
    }


    [<Test>]
    let productionDispatchSaturationCountsCommandsUntilTheirRepliesFinish () = task {
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Services.AddSignalR() |> ignore
        let gate = ResponseCompletionGate()
        builder.Services.AddSingleton<ResponseCompletionGate>(gate) |> ignore
        use server = builder.Build()
        server.MapHub<ResponseCompletionHub>("/probe") |> ignore
        do! server.StartAsync()
        let endpoint = server.Urls |> Seq.head
        let connection = HubConnectionBuilder().WithUrl(endpoint + "/probe").Build()
        do! connection.StartAsync()

        let runtime = Client.Runtime(".", Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
        let command : Common.AgentCommand =
            Common.MessageCommand { ProjectName = None; Message = "saturation-gate-probe" }
        let commandJson = System.Text.Json.JsonSerializer.Serialize<Common.AgentCommand>(command)
        let queue = CommandDispatchQueue(1, 2, (fun _ -> ()))
        let mutable started = 0
        let reported = System.Collections.Concurrent.ConcurrentBag<string>()
        let failures = System.Collections.Concurrent.ConcurrentBag<string>()
        let execute id () : Task =
            Interlocked.Increment(&started) |> ignore
            Client.SignalR.Client.receiveCommandAndReplyWithTiming
                connection runtime id commandJson "Worker"
                (fun corr _ _ delivered ->
                    if delivered then reported.Add(corr)
                    else failures.Add(corr))
        let onUnhandled (error: exn) : Task =
            failures.Add(error.GetType().Name)
            Task.CompletedTask

        Assert.That(queue.TrySubmit("one", execute "one", onUnhandled), Is.True)
        let! first = gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
        Assert.That(first, Is.EqualTo("one"))
        Assert.That(queue.TrySubmit("two", execute "two", onUnhandled), Is.True)
        Assert.That(queue.TrySubmit("three", execute "three", onUnhandled), Is.True)
        // Give the worker enough time to drain if the response handler
        // incorrectly detaches work to a second BoundedResponseIngress.
        do! Task.Delay(150)
        let startedBeforeRelease = Volatile.Read(&started)
        let overflowWasRejected = not (queue.TrySubmit("four", execute "four", onUnhandled))

        gate.Release.TrySetResult(()) |> ignore
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 8.0)
        do! connection.DisposeAsync().AsTask()
        do! server.StopAsync()

        Assert.That(startedBeforeRelease, Is.EqualTo(1),
            "While the reply is pending, the sole worker must not start queued commands.")
        Assert.That(overflowWasRejected, Is.True,
            "Only work with no available execution or queue slot may be rejected.")
        Assert.That(started, Is.EqualTo(3))
        Assert.That(reported.Count, Is.EqualTo(3))
        Assert.That(failures, Is.Empty)
    }

    [<Test>]
    let ``thirty two cheap dispatch reads bypass a stalled one second ledger operation`` () = task {
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let observed = System.Collections.Concurrent.ConcurrentBag<CommandDispatchTiming>()
        let completed = System.Collections.Concurrent.ConcurrentBag<unit>()
        let queue = CommandDispatchQueue(4, 64, observed.Add)

        let stalled () : Task = task {
            entered.TrySetResult(()) |> ignore
            do! release.Task
        }
        let cheap () : Task = task {
            completed.Add(())
            if completed.Count = 32 then finished.TrySetResult(()) |> ignore
        }
        Assert.That(queue.TrySubmit("stalled", stalled), Is.True)
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        for _ in 1 .. 32 do
            Assert.That(queue.TrySubmit("cheap", cheap), Is.True)

        // A distinct worker drains 32 while the first is held. Causal
        // ordering, not a machine-specific absolute latency threshold.
        let! outcome = Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds 5.))
        let drainedBeforeRelease = obj.ReferenceEquals(outcome, finished.Task)
        do! Task.Delay(1000)
        release.TrySetResult(()) |> ignore
        do! queue.Complete().WaitAsync(TimeSpan.FromSeconds 5.)
        Assert.That(drainedBeforeRelease, Is.True, "Slow ledger work must not serialize cheap status reads.")
        Assert.That(completed.Count, Is.EqualTo(32))
        Assert.That(observed.Count, Is.EqualTo(33))
        Assert.That(observed |> Seq.forall (fun m -> m.FailureType.IsNone), Is.True)
        let values =
            observed
            |> Seq.filter (fun m -> m.CorrelationId = "cheap")
            |> Seq.map (fun m -> m.QueueMilliseconds + m.ExecutionMilliseconds)
            |> Seq.sort |> Seq.toArray
        let percentile p =
            values[max 0 (min (values.Length - 1) (int (Math.Ceiling(float values.Length * p)) - 1))]
        TestContext.Progress.WriteLine(
            System.Text.Json.JsonSerializer.Serialize(
                {| workload = "command-dispatch-32-plus-one-stalled"
                   cheapReads = completed.Count
                   stalledMs = 1000
                   p50Ms = percentile 0.50
                   p95Ms = percentile 0.95
                   p99Ms = percentile 0.99
                   maxMs = values |> Array.max |}))
    }


// Independent disposable fixture for the ordinary (non-inbox) authenticated
// dispatch path. Measure only the actual command, not setup or observations.
module OrdinaryDispatchGitTests =
    open System
    open System.Diagnostics
    open System.IO
    open System.Text.Json
    open Client.LedgerStore
    open Client.AccountCapacityLedger
    open Client.WorkQueueGitLedger
    open Client.ReviewWorkflow
    open Common
    open NUnit.Framework

    let private success = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected fixture result: %A" error

    let private git directory args =
        use proc = new Process()
        proc.StartInfo.FileName <- "git"
        proc.StartInfo.WorkingDirectory <- directory
        proc.StartInfo.RedirectStandardOutput <- true
        proc.StartInfo.RedirectStandardError <- true
        args |> List.iter proc.StartInfo.ArgumentList.Add
        if not (proc.Start()) then failwith "Could not start fixture Git."
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        if not (proc.WaitForExit(10000)) then
            proc.Kill(true)
            failwith "Fixture Git timed out."
        if proc.ExitCode <> 0 then failwithf "Fixture Git failed: %s" (stderr.GetAwaiter().GetResult())
        stdout.GetAwaiter().GetResult().Trim()

    [<TestCase(false)>]
    [<TestCase(true)>]
    let OrdinarySourceDispatchActivationGitBudget(missing: bool) =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-dispatch-git-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let remote = Path.Combine(root, "remote.git")
        let checkout = Path.Combine(root, "project")
        let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
        try
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; checkout ] |> ignore
            git checkout [ "config"; "user.name"; "Dispatch Test" ] |> ignore
            git checkout [ "config"; "user.email"; "dispatch@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "source.txt"), "base")
            git checkout [ "add"; "source.txt" ] |> ignore
            git checkout [ "commit"; "-m"; "base" ] |> ignore
            git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            let binding: LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            LedgerStore.initialize binding true |> success |> ignore
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "Owner" |> success |> ignore
            ReviewWorkflow.createTask binding (Guid.NewGuid()) "dispatch-task" "Dispatch" |> success |> ignore
            ReviewWorkflow.assignTask binding (Guid.NewGuid()) "dispatch-task" (Guid.NewGuid()) "Owner"
            |> success |> ignore
            let before = LedgerStore.fetch binding |> success
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting "project" rt
                binding.ProjectId binding.LedgerId binding.Epoch binding.RemoteName
                binding.ApprovedFetchUrl binding.ApprovedPushUrl before.Commit true
            |> success |> ignore
            let command =
                ReadFileCommand
                    { ProjectName = "project"
                      FilePath = if missing then "missing.txt" else "source.txt"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let payload = JsonSerializer.Serialize(command)
            let invoke () =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    rt payload "Owner" None false (fun _ _ -> ())
                |> Async.AwaitTask |> Async.RunSynchronously
            let measure label =
                let sw = Stopwatch.StartNew()
                let response, io =
                    LedgerStore.measureGitReads (fun () ->
                        LedgerStore.withGitTransportFaults (fun _ args ->
                            match args with
                            | "fetch" :: _ -> System.Threading.Thread.Sleep(50); None
                            | _ -> None) invoke)
                sw.Stop()
                TestContext.Progress.WriteLine(
                    sprintf "ordinary dispatch %s missing=%b: git=%d fetches=%d elapsedMs=%d"
                        label missing io.ProcessStarts io.RemoteFetches sw.ElapsedMilliseconds)
                response, io
            let first, firstIo = measure "cold Ready"
            let afterFirst = LedgerStore.fetch binding |> success
            let warm, warmIo = measure "warm Working"
            // The pinned main baseline used 104 Git process starts and six
            // remote fetches for the cold Ready call. The confirmed activation
            // snapshot should replace the post-activation refetch; the warm
            // Working read remains a zero-Git negative control.
            Assert.That(firstIo.ProcessStarts, Is.LessThan(104), "Cold dispatch must remove redundant Git work from the pinned baseline.")
            Assert.That(firstIo.RemoteFetches, Is.EqualTo(3), "Cold dispatch should retain only the eligibility, transaction and confirmation fetches.")
            Assert.That(warmIo.ProcessStarts, Is.EqualTo(0))
            Assert.That(warmIo.RemoteFetches, Is.EqualTo(0))
            Assert.That(first.Error.IsSome, Is.EqualTo(missing), sprintf "%A" first.Error)
            Assert.That(warm.Error, Is.EqualTo(first.Error))
            Assert.That(warm.Result, Is.EqualTo(first.Result))
            if not missing then Assert.That(JsonSerializer.Deserialize<string>(first.Result.Value), Is.EqualTo("base"))
            Assert.That(first.Instructions, Is.Empty)
            Assert.That(warm.Instructions, Is.Empty)
            Assert.That(first.AgentStateChanges |> List.map (fun change -> change.AgentId), Is.EqualTo(box [ "Owner" ]))
            Assert.That(warm.AgentStateChanges, Is.Empty)
            Assert.That(first.InboxActivation.IsNone, Is.True)
            Assert.That(warm.InboxActivation.IsNone, Is.True)
            let canonical = ReviewWorkflow.getAgentInbox binding "Owner" |> success
            Assert.That(canonical.Agent.State, Is.EqualTo("Working"))
            Assert.That(canonical.Focused.Value.Attempt.Phase, Is.EqualTo("Started"))
            let afterWarm = LedgerStore.fetch binding |> success
            Assert.That(afterFirst.Commit, Is.Not.EqualTo(before.Commit))
            Assert.That(afterWarm.Commit, Is.EqualTo(afterFirst.Commit), "Warm Working dispatch must not create a new durable ledger head.")
            Assert.That(git checkout [ "status"; "--porcelain" ], Is.Empty)
        finally
            if File.Exists config then File.Delete config
            if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)


    [<Test; NonParallelizable>]
    let WorkingDispatchSkipsLegacyFenceGitWhenAlreadyWorking () =
        let oldHost =
            Environment.GetEnvironmentVariable(
                Client.LegacyWriterProtocolFence.LegacyWriterProtocolFence.HostBindingEnvironment)
        let root = Path.Combine(Path.GetTempPath(), "jarvis-dispatch-fence-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let hostPath = Path.Combine(root, "host-fence.json")
        let projectCheckout = Path.Combine(root, "project")
        let accountCheckout = Path.Combine(root, "account")
        let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot projectCheckout
        try
            let repository name checkout =
                let remote = Path.Combine(root, name + ".git")
                git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
                git root [ "clone"; remote; checkout ] |> ignore
                git checkout [ "config"; "user.name"; "Dispatch Fence Test" ] |> ignore
                git checkout [ "config"; "user.email"; "dispatch-fence@example.invalid" ] |> ignore
                File.WriteAllText(Path.Combine(checkout, "source.txt"), "base")
                git checkout [ "add"; "source.txt" ] |> ignore
                git checkout [ "commit"; "-m"; "base" ] |> ignore
                git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
                remote

            let projectRemote = repository "project" projectCheckout
            let accountRemote = repository "account" accountCheckout
            let binding: LedgerBinding =
                { RepositoryPath = projectCheckout; RemoteName = "origin"
                  ApprovedFetchUrl = projectRemote; ApprovedPushUrl = projectRemote
                  ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            let accountBinding: AccountBinding =
                { RepositoryPath = accountCheckout; RemoteName = "origin"
                  ApprovedFetchUrl = accountRemote; ApprovedPushUrl = accountRemote
                  AccountId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            AccountCapacityLedger.initialize accountBinding 3 |> success |> ignore
            LedgerStore.initialize binding true |> success |> ignore
            WorkQueueGitLedger.initialize binding "project" (Guid.NewGuid())
                (Map.ofList [ "Owner", set [ "source" ]; "BlockedOwner", set [ "source" ] ])
            |> success |> ignore
            for agentId, taskId, title in
                [ "Owner", "dispatch-task", "Dispatch"
                  "BlockedOwner", "blocked-task", "Blocked dispatch" ] do
                ReviewWorkflow.registerAgent binding (Guid.NewGuid()) agentId |> success |> ignore
                ReviewWorkflow.createTask binding (Guid.NewGuid()) taskId title |> success |> ignore
                ReviewWorkflow.assignTask binding (Guid.NewGuid()) taskId (Guid.NewGuid()) agentId
                |> success |> ignore

            let before = LedgerStore.fetch binding |> success
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting "project" rt
                binding.ProjectId binding.LedgerId binding.Epoch binding.RemoteName
                binding.ApprovedFetchUrl binding.ApprovedPushUrl before.Commit true
            |> success |> ignore

            let writeHost expectedAccountHead expectedProjectHead =
                let document =
                    {| schemaVersion = 1
                       protocolVersion = Client.LegacyWriterProtocolFence.LegacyWriterProtocolFence.ProtocolVersion
                       projectName = "project"
                       projectRepositoryPath = binding.RepositoryPath
                       projectRemoteName = binding.RemoteName
                       projectApprovedFetchUrl = binding.ApprovedFetchUrl
                       projectApprovedPushUrl = binding.ApprovedPushUrl
                       projectId = binding.ProjectId.ToString("D")
                       projectLedgerId = binding.LedgerId.ToString("D")
                       projectEpoch = binding.Epoch
                       accountRepositoryPath = accountBinding.RepositoryPath
                       accountRemoteName = accountBinding.RemoteName
                       accountApprovedFetchUrl = accountBinding.ApprovedFetchUrl
                       accountApprovedPushUrl = accountBinding.ApprovedPushUrl
                       accountId = accountBinding.AccountId.ToString("D")
                       accountLedgerId = accountBinding.LedgerId.ToString("D")
                       accountEpoch = accountBinding.Epoch
                       expectedAccountHead = expectedAccountHead
                       expectedProjectHead = expectedProjectHead |}
                File.WriteAllText(hostPath, JsonSerializer.Serialize document)
                if not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(hostPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                Environment.SetEnvironmentVariable(
                    Client.LegacyWriterProtocolFence.LegacyWriterProtocolFence.HostBindingEnvironment,
                    hostPath)

            let account = AccountCapacityLedger.fetch accountBinding |> success
            writeHost account.Head before.Commit
            let command =
                ReadFileCommand
                    { ProjectName = "project"; FilePath = "source.txt"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let payload = JsonSerializer.Serialize command
            let invoke agentId =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    rt payload agentId None false (fun _ _ -> ())
                |> Async.AwaitTask |> Async.RunSynchronously
            let measure agentId =
                LedgerStore.measureGitReads (fun () ->
                    LedgerStore.withGitTransportFaults (fun _ args ->
                        match args with
                        | "fetch" :: _ -> System.Threading.Thread.Sleep(10); None
                        | _ -> None) (fun () -> invoke agentId))

            let first, firstIo = measure "Owner"
            Assert.That(first.Error.IsSome, Is.False, sprintf "%A" first.Error)
            Assert.That(firstIo.RemoteFetches, Is.GreaterThan(3),
                "A Ready agent must still consult the configured legacy fence before activation.")
            let activated = ReviewWorkflow.getAgentInbox binding "Owner" |> success
            Assert.That(activated.Agent.State, Is.EqualTo("Working"))

            let warm, warmIo = measure "Owner"
            Assert.That(warm.Error, Is.EqualTo(first.Error))
            Assert.That(warm.Result, Is.EqualTo(first.Result))
            Assert.That(warmIo.ProcessStarts, Is.EqualTo(0),
                "An already-Working agent has no implicit lifecycle write for the migration fence to protect.")
            Assert.That(warmIo.RemoteFetches, Is.EqualTo(0))

            // A second Ready agent remains protected: once the account epoch is
            // no longer LegacyActive, the same ordinary read must not activate it.
            let accountBeforeFreeze = AccountCapacityLedger.fetch accountBinding |> success
            git accountCheckout [ "checkout"; "-B"; "freeze-fixture"; accountBeforeFreeze.Head ] |> ignore
            let statePath = Path.Combine(accountCheckout, "state.json")
            let frozen =
                { accountBeforeFreeze.State with
                    MigrationPhase = "Frozen"
                    StateVersion = accountBeforeFreeze.State.StateVersion + 1L
                    NextSequence = accountBeforeFreeze.State.NextSequence + 1L }
            File.WriteAllText(statePath, JsonSerializer.Serialize frozen)
            git accountCheckout [ "add"; "state.json" ] |> ignore
            git accountCheckout [ "commit"; "-m"; "fixture frozen migration phase" ] |> ignore
            git accountCheckout [ "push"; "origin"; "HEAD:" + AccountCapacityLedger.AccountRef ] |> ignore
            let frozenAccount = AccountCapacityLedger.fetch accountBinding |> success
            let currentProject = LedgerStore.fetch binding |> success
            writeHost frozenAccount.Head currentProject.Commit
            let beforeBlockedRead = LedgerStore.fetch binding |> success
            let blocked, blockedIo = measure "BlockedOwner"
            Assert.That(blocked.Error.IsSome, Is.False, sprintf "%A" blocked.Error)
            Assert.That(blocked.Result.IsSome, Is.True)
            Assert.That(blockedIo.RemoteFetches, Is.GreaterThan(0),
                "A Ready agent still requires a fresh fence check before implicit activation.")
            let stillReady = ReviewWorkflow.getAgentInbox binding "BlockedOwner" |> success
            Assert.That(stillReady.Agent.State, Is.EqualTo("Ready"))
            let afterBlockedRead = LedgerStore.fetch binding |> success
            Assert.That(afterBlockedRead.Commit, Is.EqualTo(beforeBlockedRead.Commit),
                "A fenced read must not create a lifecycle transition.")
        finally
            Environment.SetEnvironmentVariable(
                Client.LegacyWriterProtocolFence.LegacyWriterProtocolFence.HostBindingEnvironment,
                oldHost)
            if File.Exists config then File.Delete config
            if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)


    [<Test; NonParallelizable>]
    let OrdinaryInstructionDispatchActivationGitBudget () =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-instruction-dispatch-git-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let remote = Path.Combine(root, "remote.git")
        let checkout = Path.Combine(root, "project")
        let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
        try
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; checkout ] |> ignore
            git checkout [ "config"; "user.name"; "Dispatch Instructions Test" ] |> ignore
            git checkout [ "config"; "user.email"; "dispatch-instructions@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "AGENTS.md"), "dispatch guidance")
            git checkout [ "add"; "AGENTS.md" ] |> ignore
            git checkout [ "commit"; "-m"; "add dispatch guidance" ] |> ignore
            git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            let binding: LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            LedgerStore.initialize binding true |> success |> ignore
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "Owner" |> success |> ignore
            ReviewWorkflow.createTask binding (Guid.NewGuid()) "dispatch-task" "Dispatch" |> success |> ignore
            ReviewWorkflow.assignTask binding (Guid.NewGuid()) "dispatch-task" (Guid.NewGuid()) "Owner"
            |> success |> ignore
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "DetailsOwner" |> success |> ignore
            ReviewWorkflow.createTask binding (Guid.NewGuid()) "details-task" "Details" |> success |> ignore
            ReviewWorkflow.assignTask binding (Guid.NewGuid()) "details-task" (Guid.NewGuid()) "DetailsOwner"
            |> success |> ignore
            let before = LedgerStore.fetch binding |> success
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting "project" rt
                binding.ProjectId binding.LedgerId binding.Epoch binding.RemoteName
                binding.ApprovedFetchUrl binding.ApprovedPushUrl before.Commit true
            |> success |> ignore
            let command =
                GetAgentInstructionsCommand
                    { ProjectName = "project"; AgentId = ""; Directory = ""; Cursor = None }
            let payload = JsonSerializer.Serialize command
            let invoke () =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    rt payload "Owner" None false (fun _ _ -> ())
                |> Async.AwaitTask |> Async.RunSynchronously
            let detailsCommand = GetProjectDetailsCommand { ProjectName = "project" }
            let detailsPayload = JsonSerializer.Serialize detailsCommand
            let invokeDetails () =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    rt detailsPayload "DetailsOwner" None false (fun _ _ -> ())
                |> Async.AwaitTask |> Async.RunSynchronously
            let measure label invoke =
                let sw = Stopwatch.StartNew()
                let response, io =
                    LedgerStore.measureGitReads (fun () ->
                        LedgerStore.withGitTransportFaults (fun _ args ->
                            match args with
                            | "fetch" :: _ -> System.Threading.Thread.Sleep(50); None
                            | _ -> None) invoke)
                sw.Stop()
                TestContext.Progress.WriteLine(
                    sprintf "instruction dispatch %s: git=%d fetches=%d elapsedMs=%d"
                        label io.ProcessStarts io.RemoteFetches sw.ElapsedMilliseconds)
                response, io
            let first, firstIo = measure "cold Ready" invoke
            let afterFirst = LedgerStore.fetch binding |> success
            let warm, warmIo = measure "warm Working" invoke
            Assert.That(firstIo.ProcessStarts, Is.LessThan(104))
            Assert.That(firstIo.RemoteFetches, Is.EqualTo(3),
                "Explicit instruction dispatch should reuse the confirmed activation snapshot for turn guidance.")
            Assert.That(warmIo.ProcessStarts, Is.EqualTo(0))
            Assert.That(warmIo.RemoteFetches, Is.EqualTo(0))
            Assert.That(first.Error.IsSome, Is.False, sprintf "%A" first.Error)
            Assert.That(warm.Error, Is.EqualTo(first.Error))
            Assert.That(warm.Result, Is.EqualTo(first.Result))
            Assert.That(box first.Instructions, Is.EqualTo(box warm.Instructions))
            Assert.That(first.AgentStateChanges |> List.map (fun change -> change.AgentId), Is.EqualTo(box [ "Owner" ]))
            Assert.That(warm.AgentStateChanges, Is.Empty)
            Assert.That(first.InboxActivation.IsNone, Is.True)
            Assert.That(warm.InboxActivation.IsNone, Is.True)
            use page = JsonDocument.Parse(first.Result.Value)
            Assert.That(
                page.RootElement.GetProperty("AGENTS.md").GetProperty("project").GetString(),
                Is.EqualTo("dispatch guidance"))
            let canonical = ReviewWorkflow.getAgentInbox binding "Owner" |> success
            Assert.That(canonical.Agent.State, Is.EqualTo("Working"))
            Assert.That(canonical.Focused.Value.Attempt.Phase, Is.EqualTo("Started"))
            let afterWarm = LedgerStore.fetch binding |> success
            Assert.That(afterFirst.Commit, Is.Not.EqualTo(before.Commit))
            Assert.That(afterWarm.Commit, Is.EqualTo(afterFirst.Commit),
                "Warm instruction dispatch must not create a new durable ledger head.")
            let details, detailsIo = measure "cold Ready project details" invokeDetails
            let afterDetails = LedgerStore.fetch binding |> success
            let detailsWarm, detailsWarmIo = measure "warm Working project details" invokeDetails
            Assert.That(detailsIo.ProcessStarts, Is.LessThan(104))
            Assert.That(detailsIo.RemoteFetches, Is.EqualTo(3),
                "Project details dispatch should reuse the confirmed activation snapshot for turn guidance.")
            Assert.That(detailsWarmIo.ProcessStarts, Is.EqualTo(0))
            Assert.That(detailsWarmIo.RemoteFetches, Is.EqualTo(0))
            Assert.That(details.Error.IsSome, Is.False, sprintf "%A" details.Error)
            Assert.That(detailsWarm.Error, Is.EqualTo(details.Error))
            Assert.That(detailsWarm.Result, Is.EqualTo(details.Result))
            Assert.That(box details.Instructions, Is.EqualTo(box detailsWarm.Instructions))
            Assert.That(details.AgentStateChanges |> List.map (fun change -> change.AgentId), Is.EqualTo(box [ "DetailsOwner" ]))
            Assert.That(detailsWarm.AgentStateChanges, Is.Empty)
            Assert.That(details.InboxActivation.IsNone, Is.True)
            Assert.That(detailsWarm.InboxActivation.IsNone, Is.True)
            Assert.That(details.Result.Value, Is.EqualTo("[]"))
            let detailsCanonical = ReviewWorkflow.getAgentInbox binding "DetailsOwner" |> success
            Assert.That(detailsCanonical.Agent.State, Is.EqualTo("Working"))
            let afterDetailsWarm = LedgerStore.fetch binding |> success
            Assert.That(afterDetails.Commit, Is.Not.EqualTo(afterWarm.Commit))
            Assert.That(afterDetailsWarm.Commit, Is.EqualTo(afterDetails.Commit))
        finally
            if File.Exists config then File.Delete config
            if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)


    [<Test>]
    let ReadyClosedTaskReadReusesActivationSnapshotForGuidance () =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-dispatch-closed-task-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let remote = Path.Combine(root, "remote.git")
        let checkout = Path.Combine(root, "project")
        let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
        try
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; checkout ] |> ignore
            git checkout [ "config"; "user.name"; "Dispatch Closed Task Test" ] |> ignore
            git checkout [ "config"; "user.email"; "dispatch-closed@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "source.txt"), "base")
            git checkout [ "add"; "source.txt" ] |> ignore
            git checkout [ "commit"; "-m"; "base" ] |> ignore
            git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            let binding: LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            LedgerStore.initialize binding true |> success |> ignore
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "ClosedOwner" |> success |> ignore
            ReviewWorkflow.createTask binding (Guid.NewGuid()) "closed-task" "Closed task" |> success |> ignore
            ReviewWorkflow.assignTask binding (Guid.NewGuid()) "closed-task" (Guid.NewGuid()) "ClosedOwner"
            |> success |> ignore
            let _, task, _ = ReviewWorkflow.getTask binding "closed-task" |> success
            ReviewWorkflow.closeTaskAs binding "Lead" (Guid.NewGuid()) "closed-task" task.Version
            |> success |> ignore
            let before = LedgerStore.fetch binding |> success
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting "project" rt
                binding.ProjectId binding.LedgerId binding.Epoch binding.RemoteName
                binding.ApprovedFetchUrl binding.ApprovedPushUrl before.Commit true
            |> success |> ignore
            let command =
                ReadFileCommand
                    { ProjectName = "project"; FilePath = "source.txt"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let payload = JsonSerializer.Serialize command
            let invoke () =
                Client.SignalR.Client.executeAuthenticatedCommandWithChallengeWake
                    rt payload "ClosedOwner" None false (fun _ _ -> ())
                |> Async.AwaitTask |> Async.RunSynchronously
            let sw = Stopwatch.StartNew()
            let response, io =
                LedgerStore.measureGitReads (fun () ->
                    LedgerStore.withGitTransportFaults (fun _ args ->
                        match args with
                        | "fetch" :: _ -> System.Threading.Thread.Sleep(50); None
                        | _ -> None) invoke)
            sw.Stop()
            TestContext.Progress.WriteLine(
                sprintf "ordinary dispatch cold Ready closed task: git=%d fetches=%d elapsedMs=%d"
                    io.ProcessStarts io.RemoteFetches sw.ElapsedMilliseconds)
            Assert.That(io.RemoteFetches, Is.EqualTo(1),
                "A cold closed-task read needs one canonical fetch; eligibility and guidance must reuse that snapshot without a write or another fetch.")
            Assert.That(response.Error.IsSome, Is.False, sprintf "%A" response.Error)
            Assert.That(JsonSerializer.Deserialize<string>(response.Result.Value), Is.EqualTo("base"))
            Assert.That(response.Instructions, Is.Empty)
            Assert.That(response.AgentStateChanges, Is.Empty)
            Assert.That(response.InboxActivation.IsNone, Is.True)
            let closedInbox = ReviewWorkflow.getAgentInbox binding "ClosedOwner" |> success
            Assert.That(closedInbox.Agent.State, Is.EqualTo("Ready"))
            Assert.That(closedInbox.Focused.Value.Attempt.Phase, Is.EqualTo("Queued"))
            let _, closedTask, _ = ReviewWorkflow.getTask binding "closed-task" |> success
            Assert.That(closedTask.Lifecycle, Is.EqualTo("Closed"))
            let after = LedgerStore.fetch binding |> success
            Assert.That(after.Commit, Is.EqualTo(before.Commit),
                "A read for a closed task must not create a lifecycle transition.")
            Assert.That(git checkout [ "status"; "--porcelain" ], Is.Empty)
        finally
            if File.Exists config then File.Delete config
            if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)


    [<Test>]
    let RequestSnapshotGenerationGuardsWorkingGuidanceReuse () =
        let root = Path.Combine(Path.GetTempPath(), "jarvis-dispatch-snapshot-generation-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let remote = Path.Combine(root, "remote.git")
        let checkout = Path.Combine(root, "project")
        let config = Client.OrchestrationApi.LedgerBindingConfig.pathForRoot checkout
        try
            git root [ "init"; "--bare"; "--initial-branch=main"; remote ] |> ignore
            git root [ "clone"; remote; checkout ] |> ignore
            git checkout [ "config"; "user.name"; "Dispatch Snapshot Test" ] |> ignore
            git checkout [ "config"; "user.email"; "dispatch-snapshot@example.invalid" ] |> ignore
            File.WriteAllText(Path.Combine(checkout, "source.txt"), "base")
            git checkout [ "add"; "source.txt" ] |> ignore
            git checkout [ "commit"; "-m"; "base" ] |> ignore
            git checkout [ "push"; "-u"; "origin"; "HEAD" ] |> ignore
            let binding: LedgerBinding =
                { RepositoryPath = checkout; RemoteName = "origin"
                  ApprovedFetchUrl = remote; ApprovedPushUrl = remote
                  ProjectId = Guid.NewGuid(); LedgerId = Guid.NewGuid(); Epoch = 1 }
            LedgerStore.initialize binding true |> success |> ignore
            ReviewWorkflow.registerAgent binding (Guid.NewGuid()) "Owner" |> success |> ignore
            let before = LedgerStore.fetch binding |> success
            let rt = Client.Runtime(root, Client.ConsoleTui.ConsoleTui(), Client.PartialTrust)
            Client.OrchestrationApi.LedgerBindingConfig.bindExisting "project" rt
                binding.ProjectId binding.LedgerId binding.Epoch binding.RemoteName
                binding.ApprovedFetchUrl binding.ApprovedPushUrl before.Commit true
            |> success |> ignore
            let target = Client.ProjectPaths.ProjectTarget.parse "project" |> success
            let projectId = Client.ProjectPaths.ProjectTarget.projectId target
            let command =
                ReadFileCommand
                    { ProjectName = "project"; FilePath = "source.txt"
                      StartLine = None; EndLine = None; IncludeLineNumbers = None }
            let requestGeneration =
                Client.SignalR.Client.captureWorkingGuidanceGeneration rt target "Owner"
            let requestLedgerSnapshot = LedgerStore.fetch binding |> success
            let requestSnapshot =
                Client.SignalR.Client.createRequestSnapshot target "Owner" requestGeneration (Ok(binding, requestLedgerSnapshot))
            let guidance, warmIo =
                LedgerStore.measureGitReads (fun () ->
                    Client.SignalR.Client.checkWorkingGuidance rt "Owner" (Some target) (Some requestSnapshot) command)
            Assert.That(warmIo.RemoteFetches, Is.EqualTo(0),
                "A current request snapshot should supply guidance without another remote fetch.")
            Assert.That(guidance, Is.Empty)

            // An invalidation for another agent must not discard this snapshot.
            let unrelatedSnapshot =
                Client.SignalR.Client.createRequestSnapshot target "Owner" requestGeneration (Ok(binding, requestLedgerSnapshot))
            Client.SignalR.Client.invalidateAgentStates rt "project" [| "Other" |]
            let mutable usedUnrelatedSnapshot = false
            let mutable unrelatedFallback = false
            let unrelated =
                Client.SignalR.Client.withCurrentRequestSnapshot rt projectId "Owner" (Some unrelatedSnapshot)
                    (fun _ -> usedUnrelatedSnapshot <- true; Ok "snapshot")
                    (fun () -> unrelatedFallback <- true; Ok "fallback")
            Assert.That(unrelated, Is.EqualTo(Ok "snapshot" : Result<string, Client.EffectError>))
            Assert.That(usedUnrelatedSnapshot, Is.True)
            Assert.That(unrelatedFallback, Is.False)

            // Invalidation during use must discard the result and take the fresh path.
            let mutable postUseFallback = false
            let duringUse =
                Client.SignalR.Client.withCurrentRequestSnapshot rt projectId "Owner" (Some unrelatedSnapshot)
                    (fun _ ->
                        Client.SignalR.Client.invalidateAgentStates rt "project" [| "Owner" |]
                        Ok "stale")
                    (fun () -> postUseFallback <- true; Ok "fresh")
            Assert.That(duringUse, Is.EqualTo(Ok "fresh" : Result<string, Client.EffectError>))
            Assert.That(postUseFallback, Is.True)

            let _, invalidatedIo =
                LedgerStore.measureGitReads (fun () ->
                    Client.SignalR.Client.checkWorkingGuidance rt "Owner" (Some target) (Some unrelatedSnapshot) command)
            Assert.That(invalidatedIo.RemoteFetches, Is.EqualTo(1),
                "A same-agent push must force a fresh canonical read instead of reusing the stale request snapshot.")

            // A reconnect invalidates every agent generation.
            let globalGeneration =
                Client.SignalR.Client.captureWorkingGuidanceGeneration rt target "Owner"
            let globalLedgerSnapshot = LedgerStore.fetch binding |> success
            let globalSnapshot =
                Client.SignalR.Client.createRequestSnapshot target "Owner" globalGeneration (Ok(binding, globalLedgerSnapshot))
            Client.SignalR.Client.invalidateAllAgentStates rt
            let mutable globalFallback = false
            let globalResult =
                Client.SignalR.Client.withCurrentRequestSnapshot rt projectId "Owner" (Some globalSnapshot)
                    (fun _ -> Ok "stale")
                    (fun () -> globalFallback <- true; Ok "fresh")
            Assert.That(globalResult, Is.EqualTo(Ok "fresh" : Result<string, Client.EffectError>))
            Assert.That(globalFallback, Is.True)

            let after = LedgerStore.fetch binding |> success
            Assert.That(after.Commit, Is.EqualTo(before.Commit),
                "Cache generation changes must not mutate durable ledger state.")
        finally
            if File.Exists config then File.Delete config
            if File.Exists(config + ".lock") then File.Delete(config + ".lock")
            Directory.Delete(root, true)
