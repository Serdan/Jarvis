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
    ClientConfig.load()
    |> Result.bind (fun snapshot ->
        ClientConfig.resolve snapshot.Stored (args |> Array.toList)
        |> Result.map (fun effective -> snapshot, effective))

let private runConfigCommand (args: string array) =
    match ClientConfig.load() with
    | Error error ->
        eprintfn "%s" error
        2
    | Ok snapshot ->
        let show () =
            let stored = snapshot.Stored
            let resolved = ClientConfig.resolve stored []
            match resolved with
            | Error error -> eprintfn "%s" error; 2
            | Ok config ->
                printfn "File: %s" snapshot.Path
                printfn "Machine name: %s" config.MachineName
                printfn "Workspace: %s" config.WorkspacePath
                printfn "Trust: %s" config.TrustLevel
                printfn "Allowed environment variables: %s" (String.concat ", " config.AllowedEnvironmentVariables)
                printfn "Maximum import bytes: %d" config.MaxImportBytes
                0
        match args |> Array.toList with
        | [ "config"; "path" ] -> printfn "%s" snapshot.Path; 0
        | [ "config"; "show" ] -> show ()
        | [ "config"; "set"; key; value ] ->
            match ClientConfig.updateKey key value snapshot.Stored with
            | Error error -> eprintfn "%s" error; 2
            | Ok stored ->
                match ClientConfig.save snapshot stored with
                | Ok _ -> printfn "Saved %s" key; 0
                | Error error -> eprintfn "%s" error; 2
        | [ "config"; "unset"; key ] ->
            match ClientConfig.unsetKey key snapshot.Stored with
            | Error error -> eprintfn "%s" error; 2
            | Ok stored ->
                match ClientConfig.save snapshot stored with
                | Ok _ -> printfn "Unset %s" key; 0
                | Error error -> eprintfn "%s" error; 2
        | _ ->
            eprintfn "Usage: JarvisClient config path|show|set <key> <value>|unset <key>"
            2

let register (tui: ConsoleTui) (connection: HubConnection) deviceId (machineName: unit -> string) =
    task {
        let clientVersion =
            match Assembly.GetExecutingAssembly().GetName().Version with
            | null -> "unknown"
            | version -> version.ToString()

        let registration =
            { DeviceId = deviceId
              DeviceName = machineName()
              ProtocolVersion = AgentProtocol.version
              ClientVersion = clientVersion
              SupportedCommands =
                AgentProtocol.commandDefinitions
                |> List.map (fun definition -> definition.Name) }

        let! result = connection.invokeResultAsync<ClientRegistrationResult>("Connect", registration)

        match result with
        | Ok registered ->
            tui.SetConnectionState(Connected, BuildInfo.ServerUrl)
            tui.Log $"Negotiated {registered.EnabledCommands.Length} commands with server protocol {registered.ServerProtocolVersion}."
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

let private runClient args =
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

    let _, effective =
        match parseArgs args with
        | Ok result -> result
        | Error message ->
            eprintfn "%s" message
            eprintfn "Usage: JarvisClient [--path <workspace>] [--machine-name <name>] [--trust none|partial|full] [--allow-env NAME]... [--max-import-bytes BYTES]"
            exit 2

    let dir = effective.WorkspacePath
    let allowedEnvironmentVariables = effective.AllowedEnvironmentVariables
    let maxImportBytes = effective.MaxImportBytes
    let resolveMachineName () =
        // Re-read only the display name on registration, leaving runtime settings unchanged.
        match ClientConfig.load() with
        | Error error ->
            tui.Log $"Unable to reload configured machine name: {error}"
            effective.MachineName
        | Ok latest ->
            match ClientConfig.resolve latest.Stored (args |> Array.toList) with
            | Ok current -> current.MachineName
            | Error error ->
                tui.Log $"Unable to reload configured machine name: {error}"
                effective.MachineName
    let trustLevel =
        match TrustLevel.parse effective.TrustLevel with
        | Ok level -> level
        | Error error -> eprintfn "%s" error; exit 2

    tui.SetSettingsAccess
        { Load = ClientConfig.load
          Save = ClientConfig.save
          Effective = effective }
    ProcessEnvironment.configureAllowedEnvironmentVariables allowedEnvironmentVariables
    let rt = Runtime(getDir dir, tui, trustLevel, maxImportBytes)
    let formatJobResult value = value |> Result.mapError (fun error -> sprintf "%A" error)
    tui.SetJobAccess
        { List = (fun () ->
            JobManager.listJobs { ProjectName = None; IncludeCompleted = true } rt
            |> Result.map (fun result -> result.Jobs)
            |> formatJobResult)
          Read = (fun jobId afterSequence ->
            JobManager.getJobResult { JobId = jobId; AfterSequence = Some afterSequence } rt
            |> formatJobResult)
          Cancel = (fun jobId ->
            JobManager.cancelJob { JobId = jobId } rt
            |> formatJobResult) }
    tui.SetAgentAccess { Load = (fun () -> AgentDashboard.load rt) }
    tui.Log $"Trust: {TrustLevel.toDisplayName trustLevel}"
    tui.Log $"Allowed sensitive environment variables: {allowedEnvironmentVariables.Length}"
    let importSizeDescription =
        if maxImportBytes = 0L then "unlimited" else $"{maxImportBytes} bytes"
    tui.Log $"Maximum import size: {importSizeDescription}"

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
                        let! isRegistered = register tui connection deviceId resolveMachineName
                        registered <- isRegistered
                    else
                        connectionFailureCount <- connectionFailureCount + 1

            elif connection.State = HubConnectionState.Connected && not registered then
                let! isRegistered = register tui connection deviceId resolveMachineName
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

[<EntryPoint>]
let main args =
    if args.Length = 2 && args[0] = "config" && args[1] = "path" then
        printfn "%s" (ClientConfig.configPath())
        0
    elif args.Length > 0 && args[0] = "config" then runConfigCommand args
    else runClient args
