namespace Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Common

type private PendingResponse =
    { Completion: TaskCompletionSource<AgentCommandResponse>
      Timeout: CancellationTokenSource
      UserId: string
      ConnectionId: string
      Generation: int64 }

type ClientResponseTracker() =
    let messages = ConcurrentDictionary<string, PendingResponse>()

    member this.Register(userId: string, connectionId: string, generation: int64, timeout: TimeSpan) =
        let correlationId = Guid.NewGuid().ToString()
        let tcs = TaskCompletionSource<AgentCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        let cts = new CancellationTokenSource()
        let pending =
            { Completion = tcs
              Timeout = cts
              UserId = userId
              ConnectionId = connectionId
              Generation = generation }

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

    member this.Complete(correlationId: string, userId: string, connectionId: string, generation: int64, result) =
        match messages.TryGetValue(correlationId) with
        | true, source when source.UserId = userId && source.ConnectionId = connectionId && source.Generation = generation ->
            match messages.TryRemove(correlationId) with
            | true, _ ->
                source.Timeout.Dispose()
                source.Completion.TrySetResult result |> ignore
                true
            | _ -> false
        | _ -> false

    member this.Cancel(correlationId: string) =
        match messages.TryRemove(correlationId) with
        | true, source ->
            source.Timeout.Dispose()
            source.Completion.TrySetCanceled() |> ignore
        | _ -> ()
