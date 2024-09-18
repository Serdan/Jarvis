module Program

open System.Text.Json
open Client
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection

[<EntryPoint>]
let main args =
    let clientOptions =
        { ClientOptions.Path =
            match args with
            | [| "--path"; path |] -> path
            | _ -> "" }

    let context = ProjectBrowser.
    
    task {
        let connection =
            HubConnectionBuilder()
                .AddJsonProtocol()
                .WithUrl("https://jarvis.kehlet.dev/client")
                .Build()

        let receiveCommandHandler =
            connection.On<string, AgentCommand>("receiveCommand", UserClient.receiveCommand connection 1)


        do! connection.DisposeAsync()

        return 0
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
