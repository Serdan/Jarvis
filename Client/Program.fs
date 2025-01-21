module Program

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Client
open Client.SignalR
open Common.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Core

[<TailCall>]
let rec getDir path =
    if Directory.Exists path then
        path
    else
        Console.Write "Workspace directory: "
        getDir (Console.ReadLine())

// Connect to Jarvis Server
let connect (connection: HubConnection) key =
    task {
        let! startResult = connection.startAsync()
        
        // let! result = connection.invokeAsync<IHubService> _.Connect(key)
        let! result = connection.invokeAsync("Connect", key)

        match result with
        | Ok _ -> printfn "Connected."
        | Error err -> printfn $"Connection failed.\n{err.Message}\nRetrying..."
    }

[<EntryPoint>]
let main args =
    let dir =
        match args with
        | [| "--path"; path |] -> path
        | _ -> ""

    let rt = Runtime(getDir dir)

    let connection =
        HubConnectionBuilder()
            .AddJsonProtocol()
            // .WithUrl("https://jarvis.kehlet.dev/client")
            .WithUrl("http://127.0.0.1:5095")
            .Build()

    ignoreAll {
        connection.On<_>("ReceiveMessage", Client.receiveMessage)
        connection.On<_, Result<_, _>>("ReceiveCommand", Client.receiveCommand rt)
    }

    task {
        let key = RandomNumberGenerator.GetBytes 18 |> Convert.ToBase64String

        printfn "Provide this key to the agent:"
        printfn $"{key}"
        printfn ""

        let mutable loop = true

        Console.CancelKeyPress.AddHandler(fun _ _ ->
            printfn "Closing..."
            loop <- false)

        while loop do
            if connection.State = HubConnectionState.Disconnected then
                do! connect connection key

            do! Task.Delay 1000

        do! connection.DisposeAsync()

        return 0
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
