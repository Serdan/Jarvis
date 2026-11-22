module NativeSettingHost

open System
open System.Text.Json
open Client
open Common

// Each invocation is a separate native client OS process with its own XDG config.
// Only disposable test homes are admitted; no real SignalR or live WSL connection.
[<EntryPoint>]
let main _ =
    if Environment.GetEnvironmentVariable("JARVIS_W25_DISPOSABLE_NATIVE_CLIENT") <> "yes" then
        eprintfn "Disposable native settings host disabled"
        2
    else
        match ClientConfig.load() with
        | Error error -> eprintfn "config: %s" error; 3
        | Ok loaded ->
            match ClientConfig.resolve loaded.Stored [] with
            | Error error -> eprintfn "resolve: %s" error; 4
            | Ok effective ->
                ClientConfig.installRunningContext effective []
                let rt = new Runtime(effective.WorkspacePath)
                let rec consume () =
                    let line = Console.ReadLine()
                    if isNull line || line = "EXIT" then 0
                    else
                        try
                            if line = "PROBE" then
                                match ProjectBrowser.listProjects rt with
                                | Ok names ->
                                    let items = names |> Seq.map (fun (ProjectName name) -> name) |> Seq.toArray
                                    Console.WriteLine(JsonSerializer.Serialize(
                                        {| root = effective.WorkspacePath; machine = effective.MachineName; projects = items |}))
                                | Error error ->
                                    Console.WriteLine(JsonSerializer.Serialize({| error = sprintf "%A" error |}))
                            else
                                let command = JsonSerializer.Deserialize<AgentCommand>(line)
                                let result =
                                    Client.SignalR.Client.receiveCommand rt command
                                    |> fun operation -> operation.GetAwaiter().GetResult()
                                let response: AgentCommandResponse =
                                    match result with
                                    | Ok body ->
                                        { Result = Some body; Error = None; Instructions = []
                                          AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                                    | Error issue ->
                                        { Result = None; Error = Some(ExecutionFailed(sprintf "%A" issue)); Instructions = []
                                          AgentStateChanges = []; InboxActivation = None; PendingJob = None }
                                Console.WriteLine(JsonSerializer.Serialize(response))
                            Console.Out.Flush()
                            consume ()
                        with error ->
                            eprintfn "host: %s" error.Message
                            5
                consume ()
