namespace Client.SignalR

open System
open System.Diagnostics
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Common

/// Reconstruct a safe, correlated fallback without claiming a partially
/// executed command made no changes. Never replay a write after this error.
module CommandDispatchRecovery =
    let unexpectedResponse (commandJson: string) : AgentCommandResponse =
        let operationId =
            try
                JsonSerializer.Deserialize<AgentCommand>(commandJson)
                |> AgentCommandInfo.recoveryOperationId
            with _ -> None
        { Result = None
          Error =
            Some(UncertainOutcome
                { Message = "Jarvis client command terminated unexpectedly; the outcome may already be committed. Check the original operation ID before retrying."
                  OperationId = operationId })
          Instructions = []
          // A zero-length list is only a known subset here, NOT confirmed zero.
          // The typed UncertainOutcome controls public state-evidence metadata.
          AgentStateChanges = []
          InboxActivation = None }

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
      Execute: unit -> Task
      OnUnhandled: exn -> Task }

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
            failure <- Some(error.GetType().Name)
            // An exception in execution must not strand the caller until
            // the server timeout. The supplied handler reports an uncertain
            // outcome using the original correlation ID.
            try
                do! command.OnUnhandled error
            with _ ->
                // Delivery may fail on disconnect; the server retains its
                // timeout/abort path, and the worker must remain live.
                ()
        let elapsed = Stopwatch.GetElapsedTime(started)
        try
            observe
                { CorrelationId = command.CorrelationId
                  QueueMilliseconds = queueDuration.TotalMilliseconds
                  ExecutionMilliseconds = elapsed.TotalMilliseconds
                  FailureType = failure }
        with _ ->
            // Observability is non-authoritative and must never kill a worker.
            ()
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

    member this.TrySubmit(correlationId: string, execute: unit -> Task) =
        this.TrySubmit(correlationId, execute, fun _ -> Task.CompletedTask)

    member _.TrySubmit(correlationId: string, execute: unit -> Task, onUnhandled: exn -> Task) =
        channel.Writer.TryWrite
            { CorrelationId = correlationId
              EnqueuedAt = Stopwatch.GetTimestamp()
              Execute = execute
              OnUnhandled = onUnhandled }

    // Completion is graceful: stop admitting new work, run every admitted
    // command to completion, then wait until all workers terminate.
    member _.Complete() : Task =
        channel.Writer.TryComplete() |> ignore
        Task.WhenAll workers
