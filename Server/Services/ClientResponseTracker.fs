namespace Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

type ClientResponseTracker() =
    let messages = ConcurrentDictionary<string, TaskCompletionSource<string>>()

    member this.Register() =
        let correlationId = Guid.NewGuid().ToString()
        let tcs = TaskCompletionSource<string>()

        let cts = new CancellationTokenSource(60000)

        cts.Token.Register(fun () ->
            match messages.TryRemove(correlationId) with
            | true, source -> source.TrySetCanceled() |> ignore
            | false, _ -> ())
        |> ignore

        messages.TryAdd(correlationId, tcs) |> ignore
        (correlationId, tcs.Task)

    member this.Complete(correlationId: string, result) =
        match messages.TryRemove(correlationId) with
        | true, source -> source.SetResult result
        | _ -> ()
