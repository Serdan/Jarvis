namespace Client.SignalR

open System
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR.Client

/// SignalR callback dispatch is non-blocking even while a command awaits
/// approval, a remote ledger fetch or eventual response delivery. The bounded
/// registry observes all worker failures and prevents unbounded Task.Run use.
type BoundedResponseIngress(maxConcurrent: int, onError: exn -> unit) =
    let gate = obj()
    let active = System.Collections.Generic.Dictionary<string, TaskCompletionSource<unit>>(StringComparer.Ordinal)

    do
        if maxConcurrent <= 0 then invalidArg (nameof maxConcurrent) "At least one callback slot is required."

    member _.ActiveCount =
        lock gate (fun () -> active.Count)

    member _.TryDispatch(correlationId: string, work: unit -> Task) : Result<Task, string> =
        lock gate (fun () ->
            if String.IsNullOrWhiteSpace correlationId then
                Error "Missing SignalR correlation ID."
            elif active.ContainsKey correlationId then
                Error "Duplicate SignalR correlation ID: command was not dispatched again."
            elif active.Count >= maxConcurrent then
                Error $"Client command-ingress capacity reached ({maxConcurrent} callbacks); retry the original request."
            else
                let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                active.Add(correlationId, completion)
                try
                    Task.Run(Func<Task>(fun () ->
                        task {
                            try
                                try
                                    do! work()
                                with ex ->
                                    try onError ex with _ -> ()
                            finally
                                lock gate (fun () -> active.Remove(correlationId) |> ignore)
                                completion.TrySetResult(()) |> ignore
                        } :> Task))
                    |> ignore
                    Ok(completion.Task :> Task)
                with ex ->
                    active.Remove(correlationId) |> ignore
                    Error("Client command-ingress scheduling failed: " + ex.Message))

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
                    let remaining = timeout - stopwatch.Elapsed
                    if remaining > TimeSpan.Zero then
                        // A single InvokeAsync can hang while the connection
                        // appears healthy. Bound the invocation itself, not
                        // just the retry loop. A timed-out invocation may
                        // still complete later, so NEVER retry it.
                        let! attempt = task {
                            try
                                return! (send()).WaitAsync(remaining)
                            with ex ->
                                return Error ex
                        }

                        match attempt with
                        | Ok() ->
                            result <- Some(Ok())
                        | Error (:? TimeoutException as ex) ->
                            result <- Some(Error ex)
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

module ReconnectSchedule =
    let delayForFailureCount failureCount =
        match max 0 failureCount with
        | 0 -> TimeSpan.Zero
        | 1 -> TimeSpan.FromSeconds 2.0
        | 2 -> TimeSpan.FromSeconds 5.0
        | 3 -> TimeSpan.FromSeconds 10.0
        | _ -> TimeSpan.FromSeconds 30.0

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
