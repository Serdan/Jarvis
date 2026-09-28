namespace Client.SignalR

open System
open System.Runtime.CompilerServices
open System.Threading
open Microsoft.AspNetCore.SignalR.Client

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
