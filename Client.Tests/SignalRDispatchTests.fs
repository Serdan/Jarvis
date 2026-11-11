namespace Client.Tests.SignalR

open Microsoft.AspNetCore.SignalR

type DispatchProbeHub() =
    inherit Hub()
    member _.AcceptReply(_correlationId: string) = System.Threading.Tasks.Task.CompletedTask

module DispatchProbeTests =
    open System
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
