namespace Client.SignalR

open System
open System.Diagnostics
open System.Threading.Channels
open System.Threading.Tasks

// A command must leave SignalR's receive callback before any local work
// starts. Even Task-returning callbacks serialize subsequent notifications.
type CommandDispatchTiming =
    { CorrelationId: string
      QueueMilliseconds: float
      ExecutionMilliseconds: float
      FailureType: string option }

type private QueuedCommand =
    { CorrelationId: string
      EnqueuedAt: int64
      Execute: unit -> Task }

// Dedicated bounded worker pool. The pending channel does not own durable
// command state: on overload its caller must send an explicit rejection.
type CommandDispatchQueue(maxParallel: int, maxQueued: int, observe: CommandDispatchTiming -> unit) =
    do
        if maxParallel < 1 then invalidArg (nameof maxParallel) "At least one worker is required."
        if maxQueued < 1 then invalidArg (nameof maxQueued) "The pending capacity must be positive."

    let settings = BoundedChannelOptions(maxQueued)
    do
        settings.FullMode <- BoundedChannelFullMode.Wait
        settings.SingleReader <- false
        settings.SingleWriter <- false
        settings.AllowSynchronousContinuations <- false

    let channel = Channel.CreateBounded<QueuedCommand>(settings)

    let executeCommand (command: QueuedCommand) = task {
        let queueDuration = Stopwatch.GetElapsedTime(command.EnqueuedAt)
        let started = Stopwatch.GetTimestamp()
        let mutable failure = None
        try
            do! command.Execute()
        with error ->
            // Never let a failed command kill a worker or disappear without
            // a correlated error observation. The caller's handler is
            // responsible for encoding an error reply when possible.
            failure <- Some(error.GetType().Name)
        let elapsed = Stopwatch.GetElapsedTime(started)
        observe
            { CorrelationId = command.CorrelationId
              QueueMilliseconds = queueDuration.TotalMilliseconds
              ExecutionMilliseconds = elapsed.TotalMilliseconds
              FailureType = failure }
    }

    let worker () = task {
        let mutable reading = true
        while reading do
            let! available = channel.Reader.WaitToReadAsync().AsTask()
            if not available then
                reading <- false
            else
                let mutable command = Unchecked.defaultof<QueuedCommand>
                while channel.Reader.TryRead(&command) do
                    do! executeCommand command
    }

    // Start workers off the SignalR thread. The first Execute can synchronously
    // enter Git or an OS permission prompt before it reaches its first await.
    let workers =
        Array.init maxParallel (fun _ ->
            Task.Run(Func<Task>(fun () -> worker() :> Task)))

    member _.TrySubmit(correlationId: string, execute: unit -> Task) =
        channel.Writer.TryWrite
            { CorrelationId = correlationId
              EnqueuedAt = Stopwatch.GetTimestamp()
              Execute = execute }

    // Completion is graceful: stop admitting new work, run every admitted
    // command to completion, then wait until all workers terminate.
    member _.Complete() : Task =
        channel.Writer.TryComplete() |> ignore
        Task.WhenAll workers
