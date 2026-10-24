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
      MaxImportBytes: int64 option }

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
      Overrides: Set<string> }

module ClientConfig =
    let empty =
        { MachineName = None
          WorkspacePath = None
          TrustLevel = None
          AllowedEnvironmentVariables = None
          MaxImportBytes = None }

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
              MaxImportBytes = numberProp "maxImportBytes" }
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
        | _ -> None

    let keys = [ "machineName"; "workspacePath"; "trustLevel"; "allowedEnvironmentVariables"; "maxImportBytes" ]

    let save (snapshot: ClientConfigSnapshot) (stored: ClientStoredConfig) =
        match validate stored with
        | Error message -> Error message
        | Ok validated ->
            let path = snapshot.Path
            let mutable temporary = ""
            try
                let original = readRaw path
                if revision original <> snapshot.Revision then
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
                    let directory = Path.GetDirectoryName(path)
                    let directoryWasMissing = not (Directory.Exists directory)
                    Directory.CreateDirectory(directory) |> ignore
                    if directoryWasMissing && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
                        File.SetUnixFileMode(directory, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                    temporary <- Path.Combine(directory, $".config-{Guid.NewGuid():N}.tmp")
                    File.WriteAllText(temporary, encoded, UTF8Encoding(false))
                    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                        File.SetUnixFileMode(temporary, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    File.Move(temporary, path, true)
                    temporary <- ""
                    Ok { Path = path; Revision = revision encoded; Stored = validated }
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
        let allowed = (stored.AllowedEnvironmentVariables |> Option.defaultValue []) @ environmentNames()
        let rec loop config overrides remaining =
            match remaining with
            | [] ->
                let allowed = distinctVariables config.AllowedEnvironmentVariables
                match validate { empty with MachineName = Some config.MachineName; WorkspacePath = (if String.IsNullOrWhiteSpace config.WorkspacePath then None else Some config.WorkspacePath); TrustLevel = Some config.TrustLevel; MaxImportBytes = Some config.MaxImportBytes; AllowedEnvironmentVariables = Some allowed } with
                | Ok _ -> Ok { config with AllowedEnvironmentVariables = allowed; Overrides = overrides }
                | Error e -> Error e
            | "--path" :: value :: tail -> loop { config with WorkspacePath = value } (overrides.Add "workspacePath") tail
            | "--machine-name" :: value :: tail -> loop { config with MachineName = value } (overrides.Add "machineName") tail
            | "--trust" :: value :: tail -> loop { config with TrustLevel = value } (overrides.Add "trustLevel") tail
            | "--max-import-bytes" :: value :: tail ->
                match Int64.TryParse value with
                | true, n when n >= 0L -> loop { config with MaxImportBytes = n } (overrides.Add "maxImportBytes") tail
                | _ -> Error "max-import-bytes must be a non-negative integer."
            | "--allow-env" :: value :: tail ->
                loop { config with AllowedEnvironmentVariables = config.AllowedEnvironmentVariables @ [ value ] } (overrides.Add "allowedEnvironmentVariables") tail
            | unknown :: _ -> Error $"Unknown or incomplete argument: {unknown}"
        let effective = 
            { MachineName = named; WorkspacePath = workspace; TrustLevel = trust
              MaxImportBytes = maxBytes; AllowedEnvironmentVariables = allowed; Overrides = Set.empty }
        let environmentOverrides =
            if environmentNames() |> List.isEmpty then Set.empty else Set.singleton "allowedEnvironmentVariables"
        loop effective environmentOverrides arguments

    let updateKey key value (stored: ClientStoredConfig) =
        match key with
        | "machine-name" -> { stored with MachineName = Some value } |> validate
        | "workspace-path" -> { stored with WorkspacePath = Some value } |> validate
        | "trust-level" -> { stored with TrustLevel = Some value } |> validate
        | "max-import-bytes" ->
            match Int64.TryParse value with
            | true, n -> { stored with MaxImportBytes = Some n } |> validate
            | _ -> Error "max-import-bytes requires an integer."
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
        | "allowed-environment-variables" -> Ok { stored with AllowedEnvironmentVariables = None }
        | _ -> Error $"Unknown setting: {key}"
