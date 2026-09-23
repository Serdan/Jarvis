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

    let rec loop path permissionMode allowedEnvironmentVariables remaining =
        match remaining with
        | [] -> Ok(path, permissionMode, List.rev allowedEnvironmentVariables)
        | "--path" :: value :: tail -> loop value permissionMode allowedEnvironmentVariables tail
        | "--permission-mode" :: value :: tail -> loop path value allowedEnvironmentVariables tail
        | "--allow-env" :: value :: tail ->
            loop path permissionMode (value :: allowedEnvironmentVariables) tail
        | unknown :: _ -> Error $"Unknown or incomplete argument: {unknown}"

    loop "" (Environment.GetEnvironmentVariable "JARVIS_PERMISSION_MODE") (List.rev configuredEnvironmentVariables) (args |> Array.toList)

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
            tui.Log $"Connected and registered as generation {registered.Generation}. {registered.RegistrationReason}."
            return true
        | Error err ->
            tui.Log $"Connection registration failed: {err.Message}. Retrying..."
            return false
    }

let connect (tui: ConsoleTui) (connection: HubConnection) =
    task {
        tui.Log $"Connecting to {BuildInfo.ServerUrl}..."
        let! startResult = connection.startAsync()

        match startResult with
        | Ok _ -> return true
        | Error err ->
            tui.Log $"Connection start failed: {err.Message}. Retrying..."
            return false
    }

[<EntryPoint>]
let main args =
    let tui = ConsoleTui()

    let dir, permissionMode, allowedEnvironmentVariables =
        match parseArgs args with
        | Ok(path, modeValue, allowedEnvironmentVariables) ->
            match PermissionMode.parse modeValue with
            | Ok mode -> path, mode, allowedEnvironmentVariables
            | Error message ->
                eprintfn $"%s{message}"
                exit 2
        | Error message ->
            eprintfn $"%s{message}"
            eprintfn "Usage: JarvisClient [--path <workspace>] [--permission-mode confirm|workspace-write|trust-except-run-command|trust-session] [--allow-env NAME]..."
            exit 2

    ProcessEnvironment.configureAllowedEnvironmentVariables allowedEnvironmentVariables
    let rt = Runtime(getDir dir, tui, permissionMode)
    tui.Log $"Permission mode: {PermissionMode.toDisplayName permissionMode}"
    tui.Log $"Allowed sensitive environment variables: {allowedEnvironmentVariables.Length}"

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
        let deviceId = DeviceIdentity.getOrCreate ()
        let mutable registered = false

        Console.CancelKeyPress.AddHandler(ConsoleCancelEventHandler(fun _ args ->
            args.Cancel <- true
            tui.Log "Closing..."
            tui.RequestQuit()
            cts.Cancel()))

        while not tui.ShouldQuit do
            if connection.State = HubConnectionState.Disconnected then
                registered <- false
                let! connected = connect tui connection

                if connected then
                    let! isRegistered = register tui connection deviceId
                    registered <- isRegistered

            elif connection.State = HubConnectionState.Connected && not registered then
                let! isRegistered = register tui connection deviceId
                registered <- isRegistered

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
