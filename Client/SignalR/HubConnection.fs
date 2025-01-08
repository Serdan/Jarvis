namespace Client.SignalR

open System
open System.Linq.Expressions
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR.Client
open Kehlet.SignalR

type HubConnectionE =
    [<Extension>]
    static member invokeAsync(connection: HubConnection, f: Expression<Func<'hub, Task>>) =
        task {
            try
                do! HubConnectionExtensions.InvokeAsync(connection, f)
                return Ok()
            with ex ->
                return Error ex
        }
