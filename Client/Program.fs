module Program

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Client
open Client.ConsoleTui
open Client.SignalR
open Common
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

let connect (tui: ConsoleTui) (connection: HubConnection) key =
    task {
        tui.Log $"Connecting to {BuildInfo.ServerUrl}..."
        let! startResult = connection.startAsync()

        match startResult with
        | Error err ->
            tui.Log $"Connection start failed: {err.Message}. Retrying..."
        | Ok _ ->
            let! result = connection.invokeAsync("Connect", key)

            match result with
            | Ok _ -> tui.Log "Connected."
            | Error err -> tui.Log $"Connection registration failed: {err.Message}. Retrying..."
    }

[<EntryPoint>]
let main args =
    let dir =
        match args with
        | [| "--path"; path |] -> path
        | _ -> ""

    let tui = ConsoleTui()
    let rt = Runtime(getDir dir, tui)

    let connection =
        HubConnectionBuilder()
            .AddJsonProtocol()
            .WithUrl(BuildInfo.ServerUrl)
            .Build()

    ignoreAll {
        connection.On<string>("ReceiveMessage", Func<string, Task>(Client.receiveMessage rt))
        connection.On<string, string>("ReceiveCommand", Func<string, string, Task>(Client.receiveCommandAndReply connection rt))
    }

    task {
        use cts = new CancellationTokenSource()
        let inputLoop = tui.RunInputLoop(cts.Token)
        let key = RandomNumberGenerator.GetBytes 18 |> Convert.ToBase64String

        tui.SetKey key
        tui.Log "Provide this key to the agent."

        Console.CancelKeyPress.AddHandler(ConsoleCancelEventHandler(fun _ args ->
            args.Cancel <- true
            tui.Log "Closing..."
            tui.RequestQuit()
            cts.Cancel()))

        while not tui.ShouldQuit do
            if connection.State = HubConnectionState.Disconnected then
                do! connect tui connection key

            do! Task.Delay 1000

        cts.Cancel()
        do! connection.DisposeAsync()

        try
            do! inputLoop
        with :? OperationCanceledException ->
            ()

        return 0
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
