module Program

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
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
                printfn "Browser bridge port: %s" (config.BrowserPort |> Option.map string |> Option.defaultValue "disabled")
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

// Identity is derived from the successful local OAuth login, not from a
// remotely supplied agentId or a TUI text field. Hash the subject so ledger
// evidence distinguishes operators without copying Auth0 account identifiers.
let private operatorActorFromAccessToken (token: string) =
    try
        let pieces = token.Split('.')
        if pieces.Length <> 3 then None
        else
            let data = pieces[1].Replace('-', '+').Replace('_', '/')
            let bytes = Convert.FromBase64String(data.PadRight(((data.Length + 3) / 4) * 4, '='))
            use json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes))
            let subject = json.RootElement.GetProperty("sub").GetString()
            if String.IsNullOrWhiteSpace subject then None
            else
                SHA256.HashData(Encoding.UTF8.GetBytes subject)
                |> Convert.ToHexString
                |> fun hash -> Some("operator:" + hash.ToLowerInvariant())
    with _ -> None

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
            eprintfn "Usage: JarvisClient [--path <workspace>] [--machine-name <name>] [--trust none|partial|full] [--allow-env NAME]... [--max-import-bytes BYTES] [--browser-port PORT]"
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
    // Opt-in local browser bridge. The extension must authenticate and register a tab.
    let browserPort = effective.BrowserPort
    let browser =
        browserPort |> Option.map (fun port ->
            let bridge = BrowserBridge.Service(port, BrowserBridge.getOrCreateSecret())
            bridge.Start()
            rt.Browser <- Some bridge
            tui.Log $"Browser bridge listening on 127.0.0.1:{port}; registered windows and explicit tab registrations required."
            bridge)

    // One persistent execution scheduler for CLI and browser actions.
    // The browser bridge is optional; CLI scheduling works without an extension.
    let executeScheduledAction (action: BrowserSchedules.ScheduledAction) = task {
        let permission = rt :> PermissionIO
        let prompt (cmd: AgentCommand) request = permission.PromptPermission cmd request
        if action.ActionKind = "RunCommand" then
            let cmd : RunCommandCommand =
                { ProjectName = action.ProjectName
                  Executable = action.Executable
                  Args = action.CommandArgs
                  Reason = Some("Scheduled execution " + action.Id)
                  WorkingDirectory = if String.IsNullOrWhiteSpace(action.WorkingDirectory) then None
                                     else Some action.WorkingDirectory
                  TimeoutSeconds = Some action.TimeoutSeconds
                  MaxOutputBytes = Some action.MaxOutputBytes }
            // Preserve normal run_command permission prompts, TUI activity,
            // source-path checks and audit rather than executing an untracked process.
            let! commandResult = Client.receiveCommand rt (RunCommandCommand cmd)
            match commandResult with
            | Error error -> return Error("", sprintf "%A" error)
            | Ok responseJson ->
                try
                    let result = JsonSerializer.Deserialize<RunCommandResult>(responseJson)
                    let summary = JsonSerializer.Serialize(
                        {| exitCode = result.ExitCode; timedOut = result.TimedOut
                           truncated = result.Truncated |})
                    if result.ExitCode = 0 && not result.TimedOut then
                        return Ok summary
                    else return Error(summary, "CommandFailed")
                with :? JsonException ->
                    // Process may have executed; no automatic replay.
                    return Error("", "OutcomeUnknown: malformed run_command response")
        else
            let authCommand =
                BrowserRunInNewTabCommand
                    { ProjectName = action.ProjectName; WindowRegistration = action.WindowRegistration
                      Url = action.Url; ScriptPath = action.ScriptPath
                      ArgsJson = Some action.ArgsJson; Active = Some action.Active
                      CloseTabAfterExecution = Some action.CloseTabAfterExecution
                      TimeoutMs = Some 30000 }
            let! allowed = PermissionPolicy.authorizeWithTrust permission.TrustLevel prompt authCommand
            match allowed with
            | Error error -> return Error("", "AuthorizationDenied: " + sprintf "%A" error)
            | Ok() ->
                let script =
                    match ProjectPaths.validateProjectRelativePath action.ScriptPath with
                    | Error e -> Error(sprintf "%A" e)
                    | Ok _ when not (action.ScriptPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) ->
                        Error "InvalidScriptPath"
                    | Ok _ ->
                        match ProjectPaths.resolveProjectFile action.ProjectName action.ScriptPath rt with
                        | Error e -> Error(sprintf "%A" e)
                        | Ok(FilePath path) ->
                            try
                                if FileInfo(path).Length > int64 BrowserBridge.MaxScriptBytes then Error "InputTooLarge"
                                else Ok(File.ReadAllText(path, UTF8Encoding(false, true)))
                            with e -> Error("ScriptUnavailable:" + e.GetType().Name)
                match script, browser with
                | Error e, _ -> return Error("", e)
                | _, None -> return Error("", "BridgeDisabled")
                | Ok source, Some bridge ->
                    let mutable connected = false
                    let mutable tries = 0
                    while not connected && tries < 60 do
                        connected <- bridge.ListWindows().IsOk
                        if not connected then
                            tries <- tries + 1
                            do! Task.Delay(500)
                    if not connected then return Error("", "ExtensionDisconnected")
                    else
                        let! result = BrowserBridge.runInNewTab bridge action.WindowRegistration
                                            action.Url action.Alias action.Active source action.ArgsJson
                                            30000 action.CloseTabAfterExecution
                        match result with
                        | Error (registrationId, reason) ->
                            if reason.StartsWith("UiIncompatible:", StringComparison.Ordinal) then
                                tui.Log $"Browser schedule '{action.Id}' suspended: {reason}; inspect tab {registrationId} and update provider script."
                            return Error(registrationId, reason)
                        | Ok json -> return Ok json
    }
    let scheduler = new BrowserSchedules.Service(BrowserSchedules.defaultPath(), executeScheduledAction,
        agentLastSeen=(fun project agent ->
            // Agent activity is canonical per project, not per checkout.
            let checkout = project.IndexOf("::", StringComparison.Ordinal)
            let canonical = if checkout < 0 then project else project.Substring(0, checkout)
            tui.GetAgentLastSeen(canonical, agent)))
    rt.BrowserScheduler <- Some scheduler
    scheduler.Start()
    tui.Log "Persistent execution scheduler enabled (CLI/browser; no replay of uncertain occurrences)."

    let formatJobResult value = value |> Result.mapError (fun error -> sprintf "%A" error)
    tui.SetJobAccess
        { List = (fun () ->
            JobManager.listJobs { ProjectName = None; IncludeCompleted = true } rt
            |> Result.map (fun result -> result.Jobs)
            |> formatJobResult)
          Read = (fun jobId afterSequence ->
            JobManager.getJobResult { ProjectName = ""; JobId = jobId; AfterSequence = Some afterSequence } rt
            |> formatJobResult)
          Cancel = (fun jobId ->
            JobManager.cancelJob { ProjectName = ""; JobId = jobId } rt
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

    let authenticatedOperator =
        try
            oauth.GetAccessTokenAsync().GetAwaiter().GetResult()
            |> operatorActorFromAccessToken
        with _ -> None
    let toUiError result = result |> Result.mapError (fun error -> sprintf "%A" error)
    let toReplyError = function
        | LedgerFailureError failure when failure.Code = "StaleEntityVersion" ->
            StaleOperatorReply failure.Message
        // Every other failed/uncertain write retains the exact retry operation ID.
        // Only a definitive CAS stale response can authorize a new intent.
        | error -> OtherOperatorReplyFailure(sprintf "%A" error)
    match authenticatedOperator with
    | Some actor ->
        tui.SetChallengeAccess
            { Load = (fun () -> OrchestrationApi.OperatorChallengeApi.list rt |> toUiError)
              Reply = (fun request ->
                  OrchestrationApi.OperatorChallengeApi.reply actor request rt
                  |> Result.map ignore |> Result.mapError toReplyError) }
    | None ->
        tui.Log "Operator challenge replies unavailable: authenticated OAuth subject missing."

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

    let dispatch =
        CommandDispatchQueue(
            8,
            128,
            fun timing ->
                if timing.QueueMilliseconds >= 200.0
                   || timing.ExecutionMilliseconds >= 2000.0
                   || timing.FailureType.IsSome then
                    let failure = timing.FailureType |> Option.defaultValue "none"
                    tui.Log $"MCP command {timing.CorrelationId}: queueMs={timing.QueueMilliseconds:F0} executionAndReplyMs={timing.ExecutionMilliseconds:F0} failureType={failure}")

    let replyUnhandled (correlationId: string) (commandJson: string) (error: exn) : Task =
        task {
            // The exception might follow a completed mutation. Never claim
            // that execution failed atomically or suggest blind replay.
            let uncertain = CommandDispatchRecovery.unexpectedResponse commandJson
            let! send =
                ResponseDelivery.sendWithReconnectRetry
                    (TimeSpan.FromMinutes 2.0)
                    (TimeSpan.FromMilliseconds 250.0)
                    (fun () -> connection.State)
                    (fun () -> connection.invokeAsync("SendClientResponse", correlationId, uncertain))
            match send with
            | Ok () -> ()
            | Error deliveryError ->
                tui.Log $"MCP command {correlationId}: unhandledType={error.GetType().Name} recoveryReplyType={deliveryError.GetType().Name}"
        }

    let receiveQueued correlationId commandJson agentId : Task =
        if dispatch.TrySubmit(
            correlationId,
            (fun () ->
                Client.receiveCommandAndReplyWithTiming connection rt correlationId commandJson agentId
                    (fun id commandMs replyMs delivered ->
                        if commandMs >= 200.0 || replyMs >= 1000.0 || not delivered then
                            tui.Log
                                $"MCP stage {id}: commandMs={commandMs:F0} replyMs={replyMs:F0} replyAck={delivered}")),
            (fun error -> replyUnhandled correlationId commandJson error)) then
            Task.CompletedTask
        else
            // No command was executed. Reply with an explicit overload error;
            // SendCoreAsync does not await a hub invocation completion on
            // SignalR's single receive callback.
            let rejected : AgentCommandResponse =
                { Result = None
                  Error = Some(ExecutionFailed "Jarvis client dispatch capacity exhausted; this command was not executed.")
                  Instructions = []; AgentStateChanges = []; InboxActivation = None; PendingJob = None }
            task {
                try
                    do! connection.SendCoreAsync("SendClientResponse", [| box correlationId; box rejected |], CancellationToken.None)
                with error ->
                    tui.Log $"MCP overload response {correlationId} delivery failed ({error.GetType().Name})."
            } :> Task

    ignoreAll {
        connection.On<string>("ReceiveMessage", Func<string, Task>(Client.receiveMessage rt))
        connection.On<string, string, string>("ReceiveCommand", Func<string, string, string, Task>(receiveQueued))
        connection.On<string, string array>("InvalidateAgentStates", Func<string, string array, Task>(fun project agentIds ->
            Client.invalidateAgentStates rt project agentIds
            Task.CompletedTask))
        connection.On("InvalidateAllAgentStates", Func<Task>(fun () ->
            Client.invalidateAllAgentStates rt
            Task.CompletedTask))
        connection.On<string, string, DateTimeOffset>("RecordAgentActivity",
            Func<string, string, DateTimeOffset, Task>(fun project agent seenAtUtc ->
                tui.RecordAgentSeenAt(project, agent, seenAtUtc)
                Task.CompletedTask))
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
        // Own terminal modes for the entire session. SignalR disposal can still
        // redraw via its Closed callback after the input loop stops.
        use tracking = Client.TerminalMouse.Tracking.enable()
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
                        if isRegistered then Client.invalidateAllAgentStates rt
                    else
                        connectionFailureCount <- connectionFailureCount + 1

            elif connection.State = HubConnectionState.Connected && not registered then
                let! isRegistered = register tui connection deviceId resolveMachineName
                registered <- isRegistered
                if isRegistered then Client.invalidateAllAgentStates rt

            if not tui.ShouldQuit then
                try
                    do! Task.Delay(1000, cts.Token)
                with :? OperationCanceledException ->
                    ()

        cts.Cancel()
        // Keep the connection and browser services alive while accepted MCP
        // commands and their replies drain. Stop future schedules afterward.
        try
            do! dispatch.Complete().WaitAsync(TimeSpan.FromMinutes 2.0)
        with :? TimeoutException ->
            tui.Log "MCP dispatch shutdown exceeded two minutes; some command outcomes may be uncertain."
        (scheduler :> IDisposable).Dispose()
        browser |> Option.iter (fun bridge -> (bridge :> IDisposable).Dispose())
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
    if args.Length > 0 && args[0] = BoundedProcess.groupHostFlag then
        BoundedProcess.runInternalGroupHost (args |> Array.skip 1)
    elif args.Length = 2 && args[0] = "browser" && args[1] = "pair" then
        printfn "Pairing secret (keep private): %s" (BrowserBridge.getOrCreateSecret())
        0
    elif args.Length = 2 && args[0] = "browser" && args[1] = "revoke" then
        let path = BrowserBridge.secretFile()
        if File.Exists(path) then File.Delete(path)
        printfn "Previous browser extension pairing revoked. Restart JarvisClient to apply."
        0

    elif args.Length = 2 && args[0] = "config" && args[1] = "path" then
        printfn "%s" (ClientConfig.configPath())
        0
    elif args.Length > 0 && args[0] = "config" then runConfigCommand args
    else runClient args
