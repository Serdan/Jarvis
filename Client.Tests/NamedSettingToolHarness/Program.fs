module NamedSettingToolHarness

open System
open System.IO
open System.Text.Json
open Client
open Common

let require condition explanation =
    if not condition then failwith explanation

let dispatch (rt: Runtime) (setting: NamedClientSetting)
             (value: NamedClientSettingValue option) revision =
    Client.SignalR.Client.receiveCommand rt
        (UpdateNamedSettingCommand
            { Setting = setting; Value = value; ExpectedRevision = revision })
    |> fun pending -> pending.GetAwaiter().GetResult()

let successful rt setting value revision =
    match dispatch rt setting value revision with
    | Error issue -> failwithf "Unexpected tool failure: %A" issue
    | Ok json ->
        use parsed = JsonDocument.Parse(json)
        parsed.RootElement.Clone()

let revision (result: JsonElement) =
    result.GetProperty("Revision").GetString()

let text (result: JsonElement) (name: string) =
    let item = result.GetProperty(name)
    if item.ValueKind = JsonValueKind.Null then None else Some(item.GetString())

[<EntryPoint>]
let main args =
    try
        require (Environment.GetEnvironmentVariable("JARVIS_W25_DISPOSABLE_TEST") = "yes")
            "This harness is restricted to a disposable test process."
        require (args.Length = 2) "Expected disposable base root and device label."
        let baseRoot = Path.GetFullPath(args[0])
        let device = args[1]
        require (device = "alpha" || device = "beta") "Only known disposable fixture labels are accepted."
        let configBase = Path.Combine(baseRoot, device, "xdg")
        let workspaceA = Path.Combine(baseRoot, device, "original")
        let workspaceB = Path.Combine(baseRoot, device, "replacement")
        Directory.CreateDirectory(configBase) |> ignore
        Directory.CreateDirectory(workspaceA) |> ignore
        Directory.CreateDirectory(workspaceB) |> ignore
        let projectPath = Path.Combine(workspaceB, "sample-project")
        Directory.CreateDirectory(projectPath) |> ignore
        File.WriteAllText(Path.Combine(projectPath, ".jarvis.json"), "{}")
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configBase)
        let path = ClientConfig.configPath()
        require (path.StartsWith(configBase, StringComparison.Ordinal)) "Configuration escaped disposable fixture."

        let initial = ClientConfig.load() |> Result.defaultWith failwith
        let effective = ClientConfig.resolve initial.Stored [] |> Result.defaultWith failwith
        let rt = new Runtime(workspaceA, new Client.ConsoleTui.ConsoleTui(), FullTrust)
        ClientConfig.installRunningContext { effective with WorkspacePath = workspaceA } []

        let first = successful rt NamedClientSetting.MachineName None None
        require (revision first = initial.Revision) "Read-only discovery failed to report stored revision."
        let machine = "w25-disposable-" + device
        let renamed =
            successful rt NamedClientSetting.MachineName
                (Some(NamedClientSettingValue.Text machine)) (Some(revision first))
        require (text renamed "NewPersistedValue" = Some machine) "Machine name readback mismatch."
        require (text renamed "Activation" = Some "next-device-registration") "Machine registration status overstated."
        require (renamed.GetProperty("AppliedNow").GetBoolean() = false) "Machine mutation claimed instant registration."
        let persisted = ClientConfig.load() |> Result.defaultWith failwith
        require (persisted.Stored.MachineName = Some machine) "Machine mutation did not persist."

        match dispatch rt NamedClientSetting.BrowserPort (Some(NamedClientSettingValue.Integer 7001L))
                           (Some(revision first)) with
        | Ok _ -> failwith "Stale revision unexpectedly wrote a second setting."
        | Error _ -> ()
        require ((ClientConfig.load() |> Result.defaultWith failwith).Revision = revision renamed)
            "Rejected stale CAS changed the file."

        match dispatch rt NamedClientSetting.TrustLevel
                    (Some(NamedClientSettingValue.Text "full")) (Some(revision renamed)) with
        | Ok _ -> failwith "Remote trust escalation bypassed confirmation."
        | Error _ -> ()
        match dispatch rt NamedClientSetting.AllowedEnvironmentVariables
                    (Some(NamedClientSettingValue.TextList ["VALID;INJECTED"])) (Some(revision renamed)) with
        | Ok _ -> failwith "Malformed typed allowlist element was retokenized."
        | Error _ -> ()

        let relocated =
            successful rt NamedClientSetting.WorkspacePath
                (Some(NamedClientSettingValue.Text workspaceB)) (Some(revision renamed))
        require (relocated.GetProperty("RestartRequired").GetBoolean()) "Workspace move failed restart classification."
        require (relocated.GetProperty("AppliedNow").GetBoolean() = false) "Workspace path falsely applied live."
        require (text relocated "EffectiveRuntimeValue" = Some workspaceA)
            "Reported runtime workspace was not the observed startup root."
        let saved = ClientConfig.load() |> Result.defaultWith failwith
        require (saved.Stored.WorkspacePath = Some workspaceB) "Workspace root did not persist."
        let future = ClientConfig.resolve saved.Stored [] |> Result.defaultWith failwith
        require (future.WorkspacePath = workspaceB) "New process did not resolve persisted project root."
        let fresh = new Runtime(future.WorkspacePath)
        match Client.ProjectBrowser.listProjects fresh with
        | Error issue -> failwithf "Fresh workspace discovery failed: %A" issue
        | Ok projects ->
            require (projects |> Seq.exists (fun (ProjectName name) -> name = "sample-project"))
                "Fresh client did not discover project from new root."

        let unset =
            successful rt NamedClientSetting.MachineName None (Some(revision relocated))
        require (text unset "Operation" = Some "unset") "Unset command was misclassified."
        require ((ClientConfig.load() |> Result.defaultWith failwith).Stored.MachineName.IsNone)
            "Unset did not restore default."
        // A CLI override masks a stored edit; persistence must NOT be misreported
        // as an immediately applied runtime change.
        let cliArgs = ["--machine-name"; "cli-fixed"]
        let current = ClientConfig.load() |> Result.defaultWith failwith
        let overridden = ClientConfig.resolve current.Stored cliArgs |> Result.defaultWith failwith
        ClientConfig.installRunningContext overridden cliArgs
        let masked =
            successful rt NamedClientSetting.MachineName
                (Some(NamedClientSettingValue.Text ("stored-under-override-" + device)))
                (Some(revision unset))
        require (masked.GetProperty("AppliedNow").GetBoolean() = false)
            "Stored change under CLI override falsely claimed live application."
        require (masked.GetProperty("StartupOverride").GetBoolean())
            "Startup CLI override was not disclosed."
        require (text masked "EffectiveRuntimeValue" = Some "cli-fixed")
            "Actual runtime override was not observed."
        printfn "PASS device=%s read -> CAS -> machine -> workspace -> fresh discovery -> unset -> CLI override; trust/env negatives" device
        0
    with error ->
        eprintfn "FAIL disposable named-settings harness: %s" error.Message
        1
