namespace Client.SignalR

open System
open System.Linq.Expressions
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Kehlet.SignalR

type HubConnection =
    [<Extension>]
    static member invokeAsync(connection, f: Expression<Func<'hub, Task>>) =
        task {
            try
                do! HubConnectionExtensions.InvokeAsync(connection, f)
                return Ok()
            with ex ->
                return Error ex
        }

    [<Extension>]
    static member on(connection, client, handler: Expression<Func<'client, Func<'a, 'b>>>) =
        HubConnectionExtensions.On(connection, client, handler)

    [<Extension>]
    static member on(connection, client, handler: Expression<Func<'client, Func<'a, 'b, 'c>>>) =
        HubConnectionExtensions.On(connection, client, handler)

    [<Extension>]
    static member on(connection, client, handler: Expression<Func<'client, Delegate>>) =
        try
            HubConnectionExtensions.On(connection, client, handler) |> ignore
            Ok()
        with ex ->
            Error ex
