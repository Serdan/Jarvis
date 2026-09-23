namespace Client.SignalR

open System.Runtime.CompilerServices
open System.Threading
open Microsoft.AspNetCore.SignalR.Client

type HubConnectionE =
    [<Extension>]
    static member startAsync(connection: HubConnection) =
        task {
            try
                do! connection.StartAsync()
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
