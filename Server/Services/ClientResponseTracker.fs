namespace Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Common

type private PendingResponse =
    { Completion: TaskCompletionSource<AgentCommandResponse>
      Timeout: CancellationTokenSource }

type ClientResponseTracker() =
    let messages = ConcurrentDictionary<string, PendingResponse>()

    member this.Register(timeout: TimeSpan) =
        let correlationId = Guid.NewGuid().ToString()
        let tcs = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        let cts = new CancellationTokenSource()
        let pending = { Completion = tcs; Timeout = cts }

        cts.Token.Register(fun () ->
            match messages.TryRemove(correlationId) with
            | true, source ->
                source.Completion.TrySetCanceled() |> ignore
                source.Timeout.Dispose()
            | false, _ -> ())
        |> ignore

        if not (messages.TryAdd(correlationId, pending)) then
            cts.Dispose()
            invalidOp "Failed to register client response."

        cts.CancelAfter(timeout)
        (correlationId, tcs.Task)

    member this.Complete(correlationId: string, result) =
        match messages.TryRemove(correlationId) with
        | true, source ->
            source.Timeout.Dispose()
            source.Completion.TrySetResult result |> ignore
        | _ -> ()
