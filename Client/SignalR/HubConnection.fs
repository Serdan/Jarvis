namespace Client.SignalR

open System
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR.Client

module ResponseDelivery =
    let private inactiveConnectionMessage = "connection is not active"

    let isRetryable (state: HubConnectionState) (ex: exn) =
        state <> HubConnectionState.Connected
        ||
        match ex with
        | :? InvalidOperationException as invalid ->
            invalid.Message.Contains(inactiveConnectionMessage, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let sendWithReconnectRetry
        (timeout: TimeSpan)
        (retryDelay: TimeSpan)
        (connectionState: unit -> HubConnectionState)
        (send: unit -> Task<Result<unit, exn>>)
        =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable result: Result<unit, exn> option = None
            let mutable lastError: exn option = None

            while result.IsNone && stopwatch.Elapsed < timeout do
                if connectionState() = HubConnectionState.Connected then
                    let! attempt = send()

                    match attempt with
                    | Ok() ->
                        result <- Some(Ok())
                    | Error ex when isRetryable (connectionState()) ex ->
                        lastError <- Some ex
                    | Error ex ->
                        result <- Some(Error ex)

                if result.IsNone then
                    let remaining = timeout - stopwatch.Elapsed

                    if remaining > TimeSpan.Zero then
                        let delay =
                            if retryDelay < remaining then retryDelay else remaining

                        do! Task.Delay delay

            match result with
            | Some value -> return value
            | None ->
                let timeoutSeconds = timeout.TotalSeconds.ToString("0.#")
                let detail =
                    lastError
                    |> Option.map (fun ex -> $" Last error: {ex.Message}")
                    |> Option.defaultValue ""

                return
                    Error(
                        TimeoutException(
                            $"Timed out after {timeoutSeconds} seconds waiting to send a command response over an active connection.{detail}"
                        )
                    )
        }

type PersistentRetryPolicy() =
    interface IRetryPolicy with
        member _.NextRetryDelay(context: RetryContext) =
            let delay =
                match context.PreviousRetryCount with
                | 0L -> TimeSpan.Zero
                | 1L -> TimeSpan.FromSeconds 2.0
                | 2L -> TimeSpan.FromSeconds 10.0
                | _ -> TimeSpan.FromSeconds 30.0

            Nullable delay

type HubConnectionE =
    [<Extension>]
    static member startAsync(connection: HubConnection) =
        task {
            try
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
                do! connection.StartAsync(timeout.Token)
                return Ok()
            with ex ->
                return Error ex
        }

    [<Extension>]
    static member invokeAsync(connection: HubConnection, methodName: string, arg1) =
        task {
            try
                do! HubConnectionExtensions.InvokeAsync(connection, methodName, arg1, CancellationToken.None)
                return Ok()
            with ex ->
                return Error ex
        }

    [<Extension>]
    static member invokeAsync(connection: HubConnection, methodName: string, arg1, arg2) =
        task {
            try
                do! HubConnectionExtensions.InvokeAsync(connection, methodName, arg1, arg2, CancellationToken.None)
                return Ok()
            with ex ->
                return Error ex
        }

    [<Extension>]
    static member invokeResultAsync<'result>(connection: HubConnection, methodName: string, arg1) =
        task {
            try
                let! result =
                    HubConnectionExtensions.InvokeCoreAsync<'result>(
                        connection,
                        methodName,
                        [| box arg1 |],
                        CancellationToken.None
                    )

                return Ok result
            with ex ->
                return Error ex
        }
