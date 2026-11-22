namespace Client

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

type ClientStoredConfig =
    { MachineName: string option
      WorkspacePath: string option
      TrustLevel: string option
      AllowedEnvironmentVariables: string list option
      MaxImportBytes: int64 option
      BrowserPort: int option
      NativeHealthEnabled: bool option
      NativeHealthTimeoutSeconds: int option }

type ClientConfigSnapshot =
    { Path: string
      Revision: string
      Stored: ClientStoredConfig }

type ClientEffectiveConfig =
    { MachineName: string
      WorkspacePath: string
      TrustLevel: string
      AllowedEnvironmentVariables: string list
      MaxImportBytes: int64
      BrowserPort: int option
      NativeHealthEnabled: bool
      NativeHealthTimeoutSeconds: int
      Overrides: Set<string> }

// The caller is fixed by the local entrypoint; it is not deserialized from agent wire.
type SettingsCaller = LocalTuiUnconfirmed | LocalTuiConfirmed | LocalCli | RemoteAgent
type SettingsMutation = Replace of ClientStoredConfig | Set of string * string | Unset of string
type SettingsApplyError =
    | SettingsValidationError of string
    | SettingsRevisionConflict of string
    | SettingsConfirmationRequired
    | SettingsStorageError of string
type SettingsApplyResult =
    { Before: ClientConfigSnapshot
      After: ClientConfigSnapshot
      CurrentRuntime: ClientEffectiveConfig
      BeforeEffective: ClientEffectiveConfig
      AfterEffective: ClientEffectiveConfig
      Source: SettingsCaller
      Overrides: Set<string>
      AppliedNow: bool
      RestartRequired: bool
      ReconnectRequired: bool
      RegistryRefreshPending: bool
      NativeHealthRefreshPending: bool }

module ClientConfig =
    // One client process has one active startup context. It must come from
    // Program.runClient after local CLI parsing; remote commands may only read
    // this context, not forge overrides or pretend persisted settings are live.
    let private runningContextLock = obj()
    let mutable private runningContext: (ClientEffectiveConfig * string list) option = None

    let installRunningContext (runtime: ClientEffectiveConfig) (startupArgs: string list) =
        lock runningContextLock (fun () ->
            runningContext <- Some(runtime, startupArgs))

    let tryRunningContext () =
        lock runningContextLock (fun () -> runningContext)

    // The connected registration loop is the only writer of this actual
    // registry observation. On failure, the previous name remains current.
    let recordRegisteredMachineName name =
        lock runningContextLock (fun () ->
            runningContext <-
                runningContext
                |> Option.map (fun (effective, args) ->
                    ({ effective with MachineName = name }, args)))

    let empty =
        { MachineName = None
          WorkspacePath = None
          TrustLevel = None
          AllowedEnvironmentVariables = None
          MaxImportBytes = None
          BrowserPort = None
          NativeHealthEnabled = None
          NativeHealthTimeoutSeconds = None }

    let directoryFor (isLinux: bool) (isMac: bool) (home: string) (appData: string) (xdg: string) =
        if isLinux then
            let root =
                if not (String.IsNullOrWhiteSpace xdg) && Path.IsPathFullyQualified xdg then xdg
                else Path.Combine(home, ".config")
            Path.Combine(root, "Jarvis")
        elif isMac then Path.Combine(home, "Library", "Application Support", "Jarvis")
        else Path.Combine(appData, "Jarvis")

    let configPath () =
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        directoryFor (OperatingSystem.IsLinux()) (OperatingSystem.IsMacOS()) home appData (Environment.GetEnvironmentVariable "XDG_CONFIG_HOME")
        |> fun directory -> Path.Combine(directory, "config.json")

    let private validateName (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 128
        && not (value |> Seq.exists Char.IsControl)

    let private validateVariable (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && (value |> Seq.forall (fun ch -> Char.IsLetterOrDigit ch || ch = '_'))
        && (Char.IsLetter value[0] || value[0] = '_')

    let private distinctVariables (values: string list) =
        values |> List.distinctBy (fun v -> if OperatingSystem.IsWindows() then v.ToUpperInvariant() else v)

    let validate (stored: ClientStoredConfig) =
        let issue =
            if stored.MachineName |> Option.exists (validateName >> not) then Some "machineName must be 1-128 non-control characters."
            elif stored.WorkspacePath |> Option.exists (fun v -> String.IsNullOrWhiteSpace v || not (Path.IsPathFullyQualified v)) then Some "workspacePath must be an absolute directory path."
            elif stored.TrustLevel |> Option.exists (fun v -> not (List.contains v [ "none"; "partial"; "full" ])) then Some "trustLevel must be none, partial or full."
            elif stored.AllowedEnvironmentVariables |> Option.exists (List.exists (validateVariable >> not)) then Some "allowedEnvironmentVariables contains an invalid variable name."
            elif stored.MaxImportBytes |> Option.exists (fun v -> v < 0L) then Some "maxImportBytes must be non-negative."
            elif stored.BrowserPort |> Option.exists (fun port -> port < 1024 || port > 65535) then Some "browserPort must be between 1024 and 65535."
            elif stored.NativeHealthTimeoutSeconds |> Option.exists (fun seconds -> seconds < 1 || seconds > 604800) then Some "nativeHealthTimeoutSeconds must be between 1 and 604800."
            else None
        match issue with
        | Some error -> Error error
        | None -> Ok stored

    let private revision (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString

    let private readRaw path =
        if not (File.Exists path) then
            if Directory.Exists path then invalidOp "Config path is a directory."
            else ""
        else
            if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
                invalidOp "Config path must not be a symbolic link."
            use reader = new StreamReader(path, UTF8Encoding(false, true), true)
            reader.ReadToEnd()

    let private parse (text: string) =
        if String.IsNullOrWhiteSpace text then
            if text.Length > 0 then failwith "Configuration file is empty."
            empty
        else
            use json = JsonDocument.Parse(text)
            let root = json.RootElement
            if root.ValueKind <> JsonValueKind.Object then failwith "Configuration must be a JSON object."
            let prop (key: string) =
                match root.TryGetProperty key with
                | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
                | _ -> None
            match prop "schemaVersion" with
            | Some v when v.ValueKind = JsonValueKind.Number && v.GetInt32() = 1 -> ()
            | _ -> failwith "Unsupported configuration schemaVersion (expected 1)."
            let stringProp (key: string) =
                prop key |> Option.map (fun v ->
                    if v.ValueKind <> JsonValueKind.String then failwith $"Invalid {key}: expected string."
                    v.GetString())
            let boolProp (key: string) =
                prop key |> Option.map (fun v ->
                    if v.ValueKind <> JsonValueKind.True && v.ValueKind <> JsonValueKind.False then failwith $"Invalid {key}: expected boolean."
                    v.GetBoolean())
            let numberProp (key: string) =
                prop key |> Option.map (fun v ->
                    if v.ValueKind <> JsonValueKind.Number then failwith $"Invalid {key}: expected integer."
                    v.GetInt64())
            let listProp (key: string) =
                prop key |> Option.map (fun v ->
                    if v.ValueKind <> JsonValueKind.Array then failwith $"Invalid {key}: expected string array."
                    v.EnumerateArray()
                    |> Seq.map (fun e ->
                        if e.ValueKind <> JsonValueKind.String then failwith $"Invalid {key}: all items must be strings."
                        e.GetString())
                    |> Seq.toList)
            { MachineName = stringProp "machineName"
              WorkspacePath = stringProp "workspacePath"
              TrustLevel = stringProp "trustLevel"
              AllowedEnvironmentVariables = listProp "allowedEnvironmentVariables"
              MaxImportBytes = numberProp "maxImportBytes"
              BrowserPort = numberProp "browserPort" |> Option.map (fun n -> if n < 1024L || n > 65535L then failwith "Invalid browserPort." else int n)
              NativeHealthEnabled = boolProp "nativeHealthEnabled"
              NativeHealthTimeoutSeconds = numberProp "nativeHealthTimeoutSeconds" |> Option.map (fun n -> if n < 1L || n > 604800L then failwith "Invalid nativeHealthTimeoutSeconds." else int n) }
    let loadAt path =
        try
            let existingFile = File.Exists path
            let raw = readRaw path
            if existingFile && String.IsNullOrWhiteSpace raw then
                failwith "Existing configuration is empty."
            let stored = parse raw
            match validate stored with
            | Error error -> Error error
            | Ok values -> Ok { Path = path; Revision = revision raw; Stored = values }
        with ex -> Error $"Cannot read Jarvis config at {path}: {ex.Message}"

    let load () = loadAt (configPath())

    let private jsonValue (key: string) (stored: ClientStoredConfig) =
        match key with
        | "machineName" -> stored.MachineName |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "workspacePath" -> stored.WorkspacePath |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "trustLevel" -> stored.TrustLevel |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "allowedEnvironmentVariables" ->
            stored.AllowedEnvironmentVariables |> Option.map (fun values ->
                let arr = JsonArray()
                for value in values do arr.Add(value)
                arr :> JsonNode)
        | "maxImportBytes" -> stored.MaxImportBytes |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "browserPort" -> stored.BrowserPort |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "nativeHealthEnabled" -> stored.NativeHealthEnabled |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | "nativeHealthTimeoutSeconds" -> stored.NativeHealthTimeoutSeconds |> Option.map (fun value -> JsonValue.Create(value) :> JsonNode)
        | _ -> None

    let keys = [ "machineName"; "workspacePath"; "trustLevel"; "allowedEnvironmentVariables"; "maxImportBytes"; "browserPort"; "nativeHealthEnabled"; "nativeHealthTimeoutSeconds" ]

    // A stable sibling lock serializes revision checking and atomic replacement
    // across clients sharing the same user-level configuration.
    let private withSaveLock (path: string) (action: unit -> Result<ClientConfigSnapshot, string>) =
        let directory = Path.GetDirectoryName(Path.GetFullPath(path))
        let directoryWasMissing = not (Directory.Exists directory)
        Directory.CreateDirectory(directory) |> ignore
        if directoryWasMissing && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
            File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let lockPath = Path.Combine(directory, ".config.json.lock")
        if File.Exists lockPath && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) then
            invalidOp "Configuration lock must not be a symbolic link."
        let rec acquire attempts =
            try new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            with :? IOException when attempts < 40 ->
                System.Threading.Thread.Sleep(25)
                acquire (attempts + 1)
        use handle = acquire 0
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        action()

    // Existing config files may contain an obsolete root and must remain
    // readable for repair. A new save, however, must never acknowledge a
    // workspace root that will fail at the next application startup.
    let private validateForSave stored =
        validate stored
        |> Result.bind (fun values ->
            match values.WorkspacePath with
            | Some path when not (Directory.Exists path) -> Error "workspacePath must name an existing directory."
            | _ -> Ok values)

    let save (snapshot: ClientConfigSnapshot) (stored: ClientStoredConfig) =
        match validateForSave stored with
        | Error message -> Error message
        | Ok validated ->
            let path = snapshot.Path
            let mutable temporary = ""
            try
                withSaveLock path (fun () ->
                    let original = readRaw path
                    if File.Exists path && String.IsNullOrWhiteSpace original then
                        Error "Existing configuration is empty or corrupt; repair it explicitly before saving."
                    elif revision original <> snapshot.Revision then
                        Error "Config was changed by another process. Reload before saving."
                    else
                        let doc =
                            if original = "" then JsonObject()
                            else
                                match JsonNode.Parse(original) with
                                | :? JsonObject as obj -> obj
                                | _ -> failwith "Configuration is not a JSON object."
                        doc["schemaVersion"] <- JsonValue.Create(1)
                        for key in keys do
                            match jsonValue key validated with
                            | Some value -> doc[key] <- value
                            | None -> doc.Remove(key) |> ignore
                        let encoded = doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine
                        let directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        temporary <- Path.Combine(directory, $".config-{Guid.NewGuid():N}.tmp")
                        do
                            use file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                                File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                            use writer = new StreamWriter(file, UTF8Encoding(false), 1024, true)
                            writer.Write(encoded)
                            writer.Flush()
                            file.Flush(true)
                        File.Move(temporary, path, true)
                        temporary <- ""
                        // The success result comes only from reopening the destination.
                        match loadAt path with
                        | Ok readback when readback.Stored = validated && readback.Revision = revision encoded ->
                            Ok readback
                        | Ok _ -> Error "Saved config readback did not match the requested values."
                        | Error error -> Error $"Saved config could not be verified: {error}")
            with ex ->
                if temporary <> "" && File.Exists temporary then
                    try File.Delete temporary with _ -> ()
                Error $"Failed to save Jarvis configuration: {ex.Message}"

    let private environmentNames () =
        match Environment.GetEnvironmentVariable "JARVIS_ALLOWED_ENVIRONMENT_VARIABLES" with
        | null | "" -> []
        | value -> value.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList

    let resolve (stored: ClientStoredConfig) (arguments: string list) =
        let defaultDir = WorkspaceDefaults.current()
        let named = stored.MachineName |> Option.defaultValue Environment.MachineName
        let workspace = stored.WorkspacePath |> Option.defaultValue defaultDir
        let trust = stored.TrustLevel |> Option.defaultValue "partial"
        let maxBytes = stored.MaxImportBytes |> Option.defaultValue Common.AgentProtocol.defaultMaxImportBytes
        let browserPort = stored.BrowserPort
        let healthEnabled = stored.NativeHealthEnabled |> Option.defaultValue false
        let healthSeconds = stored.NativeHealthTimeoutSeconds |> Option.defaultValue 600
        let allowed = (stored.AllowedEnvironmentVariables |> Option.defaultValue []) @ environmentNames()
        let rec loop config overrides remaining =
            match remaining with
            | [] ->
                let allowed = distinctVariables config.AllowedEnvironmentVariables
                match validate { empty with MachineName = Some config.MachineName; WorkspacePath = (if String.IsNullOrWhiteSpace config.WorkspacePath then None else Some config.WorkspacePath); TrustLevel = Some config.TrustLevel; MaxImportBytes = Some config.MaxImportBytes; BrowserPort = config.BrowserPort; NativeHealthEnabled = Some config.NativeHealthEnabled; NativeHealthTimeoutSeconds = Some config.NativeHealthTimeoutSeconds; AllowedEnvironmentVariables = Some allowed } with
                | Ok _ -> Ok { config with AllowedEnvironmentVariables = allowed; Overrides = overrides }
                | Error e -> Error e
            | "--path" :: value :: tail -> loop { config with WorkspacePath = value } (overrides.Add "workspacePath") tail
            | "--machine-name" :: value :: tail -> loop { config with MachineName = value } (overrides.Add "machineName") tail
            | "--trust" :: value :: tail -> loop { config with TrustLevel = value } (overrides.Add "trustLevel") tail
            | "--max-import-bytes" :: value :: tail ->
                match Int64.TryParse value with
                | true, n when n >= 0L -> loop { config with MaxImportBytes = n } (overrides.Add "maxImportBytes") tail
                | _ -> Error "max-import-bytes must be a non-negative integer."
            | "--browser-port" :: value :: tail ->
                match Int32.TryParse(value) with
                | true, port when port >= 1024 && port <= 65535 ->
                    loop { config with BrowserPort = Some port } (overrides.Add "browserPort") tail
                | _ -> Error "browser-port must be between 1024 and 65535."
            | "--allow-env" :: value :: tail ->
                loop { config with AllowedEnvironmentVariables = config.AllowedEnvironmentVariables @ [ value ] } (overrides.Add "allowedEnvironmentVariables") tail
            | unknown :: _ -> Error $"Unknown or incomplete argument: {unknown}"
        let effective = 
            { MachineName = named; WorkspacePath = workspace; TrustLevel = trust
              MaxImportBytes = maxBytes; BrowserPort = browserPort; NativeHealthEnabled = healthEnabled; NativeHealthTimeoutSeconds = healthSeconds; AllowedEnvironmentVariables = allowed; Overrides = Set.empty }
        let environmentOverrides =
            if environmentNames() |> List.isEmpty then Set.empty else Set.singleton "allowedEnvironmentVariables"
        loop effective environmentOverrides arguments

    let updateKey key value (stored: ClientStoredConfig) =
        match key with
        | "machine-name" -> { stored with MachineName = Some value } |> validate
        | "workspace-path" ->
            if not (Path.IsPathFullyQualified value) then Error "workspace-path must be absolute."
            elif not (Directory.Exists value) then Error "workspace-path must name an existing directory."
            else { stored with WorkspacePath = Some value } |> validate
        | "trust-level" -> { stored with TrustLevel = Some value } |> validate
        | "browser-port" ->
            match Int32.TryParse value with
            | true, port -> { stored with BrowserPort = Some port } |> validate
            | _ -> Error "browser-port requires an integer."
        | "max-import-bytes" ->
            match Int64.TryParse value with
            | true, n -> { stored with MaxImportBytes = Some n } |> validate
            | _ -> Error "max-import-bytes requires an integer."
        | "native-health-enabled" ->
            match Boolean.TryParse value with
            | true, enabled -> { stored with NativeHealthEnabled = Some enabled } |> validate
            | _ -> Error "native-health-enabled requires true or false."
        | "native-health-timeout-seconds" ->
            match Int32.TryParse value with
            | true, seconds -> { stored with NativeHealthTimeoutSeconds = Some seconds } |> validate
            | _ -> Error "native-health-timeout-seconds requires a positive integer (maximum 604800)."
        | "allowed-environment-variables" ->
            let vars = value.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList
            { stored with AllowedEnvironmentVariables = Some vars } |> validate
        | _ -> Error $"Unknown setting: {key}"

    let unsetKey key (stored: ClientStoredConfig) =
        match key with
        | "machine-name" -> Ok { stored with MachineName = None }
        | "workspace-path" -> Ok { stored with WorkspacePath = None }
        | "trust-level" -> Ok { stored with TrustLevel = None }
        | "max-import-bytes" -> Ok { stored with MaxImportBytes = None }
        | "browser-port" -> Ok { stored with BrowserPort = None }
        | "native-health-enabled" -> Ok { stored with NativeHealthEnabled = None }
        | "native-health-timeout-seconds" -> Ok { stored with NativeHealthTimeoutSeconds = None }
        | "allowed-environment-variables" -> Ok { stored with AllowedEnvironmentVariables = None }
        | _ -> Error $"Unknown setting: {key}"
    // All single-setting clients (CLI and remote tool) enter the same
    // validation, cross-process CAS, replace and readback pipeline as TUI Save.
    let applyAt path (expectedRevision: string option) key (value: string option) =
        loadAt path
        |> Result.bind (fun snapshot ->
            if expectedRevision |> Option.exists (fun revision -> revision <> snapshot.Revision) then
                Error "Config revision conflict. Reload before saving."
            else
                (match value with
                 | Some text -> updateKey key text snapshot.Stored
                 | None -> unsetKey key snapshot.Stored)
                |> Result.bind (save snapshot))

    let applyKey key value = applyAt (configPath()) None key (Some value)
    let applyUnset key = applyAt (configPath()) None key None


    // Remote agents cannot authorize their own additional local privileges.
    // Local TUI confirmation and explicitly invoked CLI remain separate local
    // operator paths; the tool must call this fail-closed variant.
    let applyRemoteAt path (expectedRevision: string option) key (value: string option) =
        loadAt path
        |> Result.bind (fun before ->
            if expectedRevision |> Option.exists (fun revision -> revision <> before.Revision) then
                Error "Config revision conflict. Reload before saving."
            else
                (match value with
                 | Some text -> updateKey key text before.Stored
                 | None -> unsetKey key before.Stored)
                |> Result.bind (fun next ->
                    let trustRank = function
                        | Some "none" -> 0
                        | Some "full" -> 2
                        | _ -> 1
                    let increasedTrust = trustRank next.TrustLevel > trustRank before.Stored.TrustLevel
                    let priorVars = before.Stored.AllowedEnvironmentVariables |> Option.defaultValue []
                    let nextVars = next.AllowedEnvironmentVariables |> Option.defaultValue []
                    let expandedEnvironment =
                        nextVars |> List.exists (fun name -> not (List.contains name priorVars))
                    if increasedTrust || expandedEnvironment then
                        Error "Local operator confirmation required for trust or environment-allowlist expansion."
                    else
                        applyAt path (Some before.Revision) key value))
    let applyRemote key value expectedRevision = applyRemoteAt (configPath()) expectedRevision key value

    let settingsErrorMessage = function
        | SettingsValidationError message
        | SettingsRevisionConflict message
        | SettingsStorageError message -> message
        | SettingsConfirmationRequired ->
            "Local operator confirmation required for a trust increase or environment allowlist expansion."

    let private storedTrustRank = function
        | Some "none" -> 0
        | Some "full" -> 2
        | _ -> 1 // Unset explicitly resolves to the default partial trust.

    let settingsNeedsConfirmation (before: ClientStoredConfig) (after: ClientStoredConfig) =
        let increasedTrust = storedTrustRank after.TrustLevel > storedTrustRank before.TrustLevel
        let oldVariables = before.AllowedEnvironmentVariables |> Option.defaultValue []
        let newVariables = after.AllowedEnvironmentVariables |> Option.defaultValue []
        increasedTrust || (newVariables |> List.exists (fun name -> not (List.contains name oldVariables)))

    // Shared application boundary: existing locked CAS + atomic save + readback.
    // Runtime is observed, never silently mutated or claimed to have restarted.
    let applySettingsAt path caller (currentRuntime: ClientEffectiveConfig)
                        (startupArgs: string list) (expectedRevision: string option)
                        mutation : Result<SettingsApplyResult, SettingsApplyError> =
        loadAt path
        |> Result.mapError SettingsStorageError
        |> Result.bind (fun before ->
            if caller = RemoteAgent && expectedRevision.IsNone then
                Error(SettingsRevisionConflict "Remote setting updates require an expected config revision.")
            elif expectedRevision |> Option.exists ((<>) before.Revision) then
                Error(SettingsRevisionConflict "Config revision conflict. Reload before saving.")
            else
                let candidate =
                    match mutation with
                    | Replace stored -> validateForSave stored
                    | Set(key, value) -> updateKey key value before.Stored |> Result.bind validateForSave
                    | Unset key -> unsetKey key before.Stored |> Result.bind validateForSave
                candidate
                |> Result.mapError SettingsValidationError
                |> Result.bind (fun next ->
                    if settingsNeedsConfirmation before.Stored next
                       && (caller = RemoteAgent || caller = LocalTuiUnconfirmed) then
                        Error SettingsConfirmationRequired
                    else
                        resolve before.Stored startupArgs
                        |> Result.mapError SettingsValidationError
                        |> Result.bind (fun beforeEffective ->
                            resolve next startupArgs
                            |> Result.mapError SettingsValidationError
                            |> Result.bind (fun afterEffective ->
                                save before next
                                |> Result.mapError (fun message ->
                                    if message.Contains("changed by another process") then
                                        SettingsRevisionConflict message
                                    else SettingsStorageError message)
                                |> Result.map (fun after ->
                                    let restartRequired =
                                        currentRuntime.WorkspacePath <> afterEffective.WorkspacePath
                                        || currentRuntime.TrustLevel <> afterEffective.TrustLevel
                                        || currentRuntime.AllowedEnvironmentVariables <> afterEffective.AllowedEnvironmentVariables
                                        || currentRuntime.MaxImportBytes <> afterEffective.MaxImportBytes
                                        || currentRuntime.BrowserPort <> afterEffective.BrowserPort
                                    { Before = before
                                      After = after
                                      CurrentRuntime = currentRuntime
                                      BeforeEffective = beforeEffective
                                      AfterEffective = afterEffective
                                      Source = caller
                                      Overrides = afterEffective.Overrides
                                      // This transaction only saves and reads back configuration.
                                      // Even when the effective values equal the current runtime
                                      // (for example through a CLI override), no live effect ran.
                                      AppliedNow = false
                                      RestartRequired = restartRequired
                                      ReconnectRequired = false // Connected-loop registry refresh needs no hub reconnect.
                                      RegistryRefreshPending = currentRuntime.MachineName <> afterEffective.MachineName
                                      NativeHealthRefreshPending =
                                          currentRuntime.NativeHealthEnabled <> afterEffective.NativeHealthEnabled
                                          || currentRuntime.NativeHealthTimeoutSeconds <> afterEffective.NativeHealthTimeoutSeconds })))))
    let applySettings caller runtime startupArgs expectedRevision mutation =
        applySettingsAt (configPath()) caller runtime startupArgs expectedRevision mutation
