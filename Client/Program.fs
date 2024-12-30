module Program

open System
open System.IO
open System.Security.Cryptography
open Client
open Client.Lib.Misc
open Client.SignalR
open Common
open Common.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection

let rec getDir path =
    if Directory.Exists path then
        path
    else
        Console.Write "Workspace directory: "
        let path = Console.ReadLine()
        getDir path

// Connect to Jarvis Server
let connect (connection: HubConnection) key =
    task {
        let! result = connection.invokeAsync<IHubService> _.Connect(key)

        Result.printError result
    }

[<EntryPoint>]
let main args =
    let dir =
        match args with
        | [| "--path"; path |] -> path
        | _ -> ""

    let rt = ProjectBrowser.Runtime(dir |> getDir)

    let connection =
        HubConnectionBuilder()
            .AddJsonProtocol()
            .WithUrl("https://jarvis.kehlet.dev/client")
            .Build()

    ignoreAll {
        connection.On<string>("ReceiveMessage", UserClient.receiveMessage)
        connection.On<string, AgentCommand>("ReceiveCommand", UserClient.receiveCommand connection rt)
    }

    task {
        do! connection.StartAsync()

        let key = RandomNumberGenerator.GetBytes 18 |> Convert.ToBase64String

        printfn "Provide this key to the agent:"
        printfn $"{key}"
        printfn ""

        do! connect connection key

        let mutable loop = true

        Console.CancelKeyPress.AddHandler(fun _ _ -> loop <- false)

        while loop do
            Console.ReadLine() |> ignore
            printfn $"{connection.State}"

            if connection.State = HubConnectionState.Disconnected then
                do! connect connection key

        let! _ = connection.invokeAsync<IHubService> _.Disconnect()

        do! connection.DisposeAsync()

        return 0
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
