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
