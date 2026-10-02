module Program

open System
open System.IO
open System.Reflection
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

let private parseArgs args =
    let configuredEnvironmentVariables =
        match Environment.GetEnvironmentVariable "JARVIS_ALLOWED_ENVIRONMENT_VARIABLES" with
        | null
        | "" -> []
        | value ->
            value.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList

    let rec loop path trustLevel allowedEnvironmentVariables remaining =
        match remaining with
        | [] -> Ok(path, trustLevel, List.rev allowedEnvironmentVariables)
        | "--path" :: value :: tail -> loop value trustLevel allowedEnvironmentVariables tail
        | "--trust" :: value :: tail -> loop path value allowedEnvironmentVariables tail
        | "--allow-env" :: value :: tail ->
            loop path trustLevel (value :: allowedEnvironmentVariables) tail
        | unknown :: _ -> Error $"Unknown or incomplete argument: {unknown}"

    loop (WorkspaceDefaults.current()) "partial" (List.rev configuredEnvironmentVariables) (args |> Array.toList)

let register (tui: ConsoleTui) (connection: HubConnection) deviceId =
    task {
        let clientVersion =
            match Assembly.GetExecutingAssembly().GetName().Version with
            | null -> "unknown"
            | version -> version.ToString()

        let registration =
            { DeviceId = deviceId
              DeviceName = Environment.MachineName
              ProtocolVersion = AgentProtocol.version
              ClientVersion = clientVersion }

        let! result = connection.invokeResultAsync<ClientRegistrationResult>("Connect", registration)

        match result with
        | Ok registered ->
            tui.SetConnectionState(Connected, BuildInfo.ServerUrl)
            return true
        | Error err ->
            tui.SetConnectionState(Connecting, BuildInfo.ServerUrl)
            return false
    }

let connect (tui: ConsoleTui) (connection: HubConnection) =
    task {
        tui.SetConnectionState(Connecting, BuildInfo.ServerUrl)
        let! startResult = connection.startAsync()

        match startResult with
        | Ok _ -> return true
        | Error err ->
            tui.SetConnectionState(Disconnected, BuildInfo.ServerUrl)
            return false
    }

[<EntryPoint>]
let main args =
    let activityStore, activityStoreError =
        try
            Some(Client.ActivityLog.Store.CreateDefault()), None
        with ex ->
            None, Some ex.Message

    let tui =
        match activityStore with
        | Some store -> ConsoleTui(activityLog = store)
        | None -> ConsoleTui()

    tui.SetConnectionState(Disconnected, BuildInfo.ServerUrl)
    activityStoreError
    |> Option.iter (fun error -> tui.Log $"Activity persistence unavailable: {error}")

    let dir, trustLevel, allowedEnvironmentVariables =
        match parseArgs args with
        | Ok(path, trustValue, allowedEnvironmentVariables) ->
            match TrustLevel.parse trustValue with
            | Ok level -> path, level, allowedEnvironmentVariables
            | Error message ->
                eprintfn $"%s{message}"
                exit 2
        | Error message ->
            eprintfn $"%s{message}"
            eprintfn "Usage: JarvisClient [--path <workspace>] [--trust none|partial|full] [--allow-env NAME]..."
            exit 2

    ProcessEnvironment.configureAllowedEnvironmentVariables allowedEnvironmentVariables
    let rt = Runtime(getDir dir, tui, trustLevel)
    tui.Log $"Trust: {TrustLevel.toDisplayName trustLevel}"
    tui.Log $"Allowed sensitive environment variables: {allowedEnvironmentVariables.Length}"

    let mutable registered = false

    let oauth =
        OAuth.login tui.Log
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let connection =
        HubConnectionBuilder()
            .AddJsonProtocol()
            .WithUrl(
                BuildInfo.ServerUrl,
                Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>(fun options ->
                    options.AccessTokenProvider <-
                        Func<Task<string>>(fun () -> oauth.GetAccessTokenAsync()))
            )
            .Build()

    connection.KeepAliveInterval <- TimeSpan.FromSeconds 10.0
    connection.ServerTimeout <- TimeSpan.FromSeconds 60.0

    ignoreAll {
        connection.On<string>("ReceiveMessage", Func<string, Task>(Client.receiveMessage rt))
        connection.On<string, string>("ReceiveCommand", Func<string, string, Task>(Client.receiveCommandAndReply connection rt))
    }

    connection.add_Closed(
        Func<Exception, Task>(fun error ->
            registered <- false
            tui.SetConnectionState(Disconnected, BuildInfo.ServerUrl)
            let detail =
                if isNull error then "no exception"
                else $"{error.GetType().FullName}: {error.Message}"
            tui.Log $"SignalR connection closed: {detail}"
            Task.CompletedTask))

    task {
        use cts = new CancellationTokenSource()
        let inputLoop = tui.RunInputLoop(cts.Token)
        let deviceId = DeviceIdentity.getOrCreate ()
        let mutable connectionFailureCount = 0

        Console.CancelKeyPress.AddHandler(ConsoleCancelEventHandler(fun _ args ->
            args.Cancel <- true
            tui.SetConnectionState(Closing, BuildInfo.ServerUrl)
            tui.RequestQuit()
            cts.Cancel()))

        while not tui.ShouldQuit do
            if connection.State = HubConnectionState.Disconnected then
                registered <- false
                let retryDelay = ReconnectSchedule.delayForFailureCount connectionFailureCount

                if retryDelay > TimeSpan.Zero then
                    tui.SetConnectionState(Reconnecting, BuildInfo.ServerUrl)
                    let retrySeconds = int retryDelay.TotalSeconds
                    tui.Log $"Jarvis server unavailable; retrying in {retrySeconds} seconds."

                    try
                        do! Task.Delay(retryDelay, cts.Token)
                    with :? OperationCanceledException ->
                        ()

                if not tui.ShouldQuit && connection.State = HubConnectionState.Disconnected then
                    let! connected = connect tui connection

                    if connected then
                        connectionFailureCount <- 0
                        let! isRegistered = register tui connection deviceId
                        registered <- isRegistered
                    else
                        connectionFailureCount <- connectionFailureCount + 1

            elif connection.State = HubConnectionState.Connected && not registered then
                let! isRegistered = register tui connection deviceId
                registered <- isRegistered

            if not tui.ShouldQuit then
                try
                    do! Task.Delay(1000, cts.Token)
                with :? OperationCanceledException ->
                    ()

        cts.Cancel()
        do! connection.DisposeAsync()

        activityStore
        |> Option.iter (fun store -> (store :> IDisposable).Dispose())

        try
            do! inputLoop
        with :? OperationCanceledException ->
            ()

        return 0
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously
