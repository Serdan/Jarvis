module Client.CodeQualityHooks

open System
open System.IO
open System.Text.Json
open Client
open Client.IO
open Common

// Repository files may select only client-implemented adapters. No hook may
// supply an executable, arguments, shell code or a URL to execute.
type HookTrigger = AfterWrite | PreCommit
type HookPolicy = Advisory | Blocking
type Hook =
    { Name: string
      Trigger: HookTrigger
      Adapter: string
      Extensions: string list
      AutoFix: bool
      Policy: HookPolicy }

type HookReport = CodeQualityHookReport
type HookSummary = CodeQualityHookSummary

let private property (key: string) (element: JsonElement) =
    let mutable value = Unchecked.defaultof<JsonElement>
    if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(key, &value) then
        Some value
    else None

let private requiredString name (element: JsonElement) =
    match property name element with
    | Some value when value.ValueKind = JsonValueKind.String
                      && not (String.IsNullOrWhiteSpace(value.GetString())) ->
        Ok(value.GetString())
    | _ -> Error(sprintf "Configured code-quality hook needs string '%s'." name)

let private boolValue name fallback element =
    match property name element with
    | None -> Ok fallback
    | Some value when value.ValueKind = JsonValueKind.True -> Ok true
    | Some value when value.ValueKind = JsonValueKind.False -> Ok false
    | _ -> Error(sprintf "Code-quality hook '%s' must be boolean." name)

let private extensions element =
    match property "extensions" element with
    | None -> Ok [ ".cs" ]
    | Some value when value.ValueKind = JsonValueKind.Array ->
        let parts = value.EnumerateArray() |> Seq.toList
        let invalid (x: JsonElement) =
            if x.ValueKind <> JsonValueKind.String then true
            else
                let text = x.GetString()
                String.IsNullOrWhiteSpace text || not (text.StartsWith(".", StringComparison.Ordinal))
                || text.Contains("/") || text.Contains("\\") || text.Contains("*")
        if parts.IsEmpty || (parts |> List.exists invalid) then
            Error "Code-quality hook extensions must be literal file extensions (for example .cs)."
        else Ok(parts |> List.map (fun x -> x.GetString()))
    | _ -> Error "Code-quality hook 'extensions' must be an array."

let private parseHook (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then
        Error "Each code-quality hook must be an object."
    elif element.EnumerateObject() |> Seq.exists (fun item ->
        not (set [ "name"; "trigger"; "adapter"; "extensions"; "autoFix"; "failure"; "enabled" ]
             |> Set.contains item.Name)) then
        Error "Unknown code-quality hook property; executable and shell configuration are forbidden."
    else
        match boolValue "enabled" false element with
        | Error message -> Error message
        | Ok false -> Ok None
        | Ok true ->
            match requiredString "name" element,
                  requiredString "trigger" element,
                  requiredString "adapter" element,
                  extensions element,
                  boolValue "autoFix" true element with
            | Ok name, Ok triggerText, Ok adapter, Ok suffixes, Ok autoFix ->
                let trigger =
                    match triggerText with
                    | "after-write" -> Ok AfterWrite
                    | "pre-commit" -> Ok PreCommit
                    | _ -> Error(sprintf "Unsupported code-quality hook trigger '%s'." triggerText)
                let policy =
                    match property "failure" element with
                    | None -> Ok Blocking
                    | Some value when value.ValueKind = JsonValueKind.String ->
                        match value.GetString() with
                        | "advisory" -> Ok Advisory
                        | "blocking" -> Ok Blocking
                        | other -> Error(sprintf "Unsupported code-quality failure policy '%s'." other)
                    | _ -> Error "Code-quality hook 'failure' must be advisory or blocking."
                match trigger, policy with
                | Ok trigger, Ok policy when adapter = "roslyn-format" ->
                    Ok(Some
                        { Name = name
                          Trigger = trigger
                          Adapter = adapter
                          Extensions = suffixes
                          AutoFix = autoFix
                          Policy = policy })
                | Ok _, Ok _ -> Error(sprintf "Unknown or untrusted hook adapter '%s'." adapter)
                | Error message, _ | _, Error message -> Error message
            | Error message, _, _, _, _
            | _, Error message, _, _, _
            | _, _, Error message, _, _
            | _, _, _, Error message, _
            | _, _, _, _, Error message -> Error message

let load (root: string) : Result<Hook list, string> =
    try
        let configPath = Path.Combine(root, ".jarvis.json")
        if FileInfo(configPath).LinkTarget <> null then
            Error "Code-quality configuration must not be a symbolic link."
        elif not (File.Exists configPath) then Ok []
        else
            use document = JsonDocument.Parse(File.ReadAllText configPath)
            match property "codeQuality" document.RootElement with
            | None -> Ok []
            | Some config ->
                match property "hooks" config with
                | Some hooks when hooks.ValueKind = JsonValueKind.Array ->
                    (Ok [], hooks.EnumerateArray())
                    ||> Seq.fold (fun state element ->
                        match state, parseHook element with
                        | Ok values, Ok(Some hook) -> Ok(hook :: values)
                        | Ok values, Ok None -> Ok values
                        | Error message, _ | _, Error message -> Error message)
                    |> Result.map List.rev
                | _ -> Error "Enabled code-quality configuration must have a 'hooks' array."
    with ex -> Error("Invalid .jarvis.json codeQuality configuration: " + ex.Message)

// Repository files can request formatting, but cannot authorize loading
// project analyzers. Only the operator can approve a checkout through the
// JarvisClient environment. No repository or MCP write can modify this gate.
let requireApproval (root: string) : Client.Result<unit> =
    let raw = Environment.GetEnvironmentVariable("JARVIS_APPROVED_CODE_QUALITY_ROOTS")
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal
    let canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
    let trusted =
        if String.IsNullOrWhiteSpace raw then false
        else
            raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun candidate ->
                try
                    String.Equals(
                        canonical,
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Trim())),
                        comparison)
                with _ -> false)
    if trusted then Ok()
    else Error(Client.PermissionDenied(
        "Code-quality hooks require explicit operator checkout approval " +
        "(JARVIS_APPROVED_CODE_QUALITY_ROOTS); refusing untrusted project/analyzer execution."))

let private validateFile root relative =
    match ProjectPaths.validateProjectRelativePath relative with
    | Error problem -> Error(sprintf "%A" problem)
    | Ok path ->
        let fullPath = Path.GetFullPath(Path.Combine(root, path))
        if not (ProjectPaths.isPathInRoot root fullPath) then
            Error "Code-quality file is outside the selected checkout."
        else
            let relativePath = Path.GetRelativePath(root, fullPath)
            let components = relativePath.Split(
                [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries)
            let mutable current = root
            let mutable linked = false
            for component in components do
                current <- Path.Combine(current, component)
                if FileInfo(current).LinkTarget <> null || DirectoryInfo(current).LinkTarget <> null then
                    linked <- true
            if linked then Error "Code-quality hooks refuse symbolic-link source paths."
            else Ok fullPath

let private effectiveFinalNewline (root: string) (filePath: string) =
    // .editorconfig is interpreted by Roslyn for formatting. Jarvis enforces
    // insert_final_newline explicitly because LSP formatting can omit it.
    let directories =
        let rec visit (directory: string) acc =
            if not (ProjectPaths.isPathInRoot root directory) &&
               not (String.Equals(root, directory, StringComparison.Ordinal)) then acc
            elif String.Equals(root, directory, StringComparison.Ordinal) then root :: acc
            else visit (Path.GetDirectoryName directory) (directory :: acc)
        visit (Path.GetDirectoryName filePath) []
    let mutable insertNewline = false
    let mutable newline = None
    for directory in directories do
        let config = Path.Combine(directory, ".editorconfig")
        if File.Exists config && FileInfo(config).LinkTarget = null then
            let mutable active = true // Top-level settings preceding a section
            for raw in File.ReadLines config do
                let line = raw.Trim()
                if line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) then
                    let pattern = line.Substring(1, line.Length - 2).Trim()
                    active <- pattern = "*" || pattern = "*.cs" || pattern = "**/*.cs"
                elif active && not (line.StartsWith("#", StringComparison.Ordinal)) &&
                     not (line.StartsWith(";", StringComparison.Ordinal)) then
                    let separator = line.IndexOf('=')
                    if separator > 0 then
                        let key = line.Substring(0, separator).Trim().ToLowerInvariant()
                        let value = line.Substring(separator + 1).Trim().ToLowerInvariant()
                        if key = "insert_final_newline" then
                            if value = "true" then insertNewline <- true
                            elif value = "false" then insertNewline <- false
                        elif key = "end_of_line" then
                            if value = "crlf" then newline <- Some "\r\n"
                            elif value = "lf" then newline <- Some "\n"
                            elif value = "cr" then newline <- Some "\r"
    insertNewline, newline

let private ensureFinalNewline root filePath (content: string) =
    let insert, eol = effectiveFinalNewline root filePath
    if insert && not (content.EndsWith("\n", StringComparison.Ordinal) ||
                      content.EndsWith("\r", StringComparison.Ordinal)) then
        content + defaultArg eol (if content.Contains("\r\n") then "\r\n" else "\n")
    else content

let private oneFile (root: string) (hook: Hook) (path: string) =
    try
        match validateFile root path with
        | Error message -> Error message
        | Ok absolute ->
            if not (File.Exists absolute) then
                Error "Selected source file disappeared before the hook ran."
            else
                let initial = File.ReadAllText absolute
                if initial.Contains('\u0000') then Error "Roslyn formatting rejects binary source."
                elif initial.Length > 2 * 1024 * 1024 then
                    Error "Roslyn formatting rejects source files over 2 MiB."
                else
                    match RoslynLsp.formatDocument root absolute initial with
                    | Error message -> Error message
                    | Ok formatted ->
                        let updated = ensureFinalNewline root absolute formatted.Content
                        let changed = not (String.Equals(initial, updated, StringComparison.Ordinal))
                        if changed && hook.AutoFix then
                            let options =
                                { SourceRoot = FolderPath root; CreateParents = false; DryRun = false }
                            match AtomicTextFiles.edit (FilePath absolute) options (fun before ->
                                match before with
                                | Some current when String.Equals(initial, current, StringComparison.Ordinal) ->
                                    Ok(ReplaceText updated, ())
                                | _ ->
                                    Error(Client.ValidationError
                                        "Formatting conflict: source changed while Roslyn was preparing edits.")) with
                            | Ok () -> Ok(changed, formatted.EditCount, formatted.Diagnostics, "Formatting changes applied atomically.")
                            | Error problem -> Error(sprintf "%A" problem)
                        elif changed then
                            Error "Formatting differs from the source and automatic fixes are disabled."
                        else
                            Ok(false, formatted.EditCount, formatted.Diagnostics, "Already formatted.")
    with ex -> Error ex.Message

let private matchesPath (hook: Hook) (path: string) =
    hook.Extensions
    |> List.exists (fun extension -> path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))

let hasMatchingHook (configured: Hook list) trigger paths =
    configured
    |> List.exists (fun hook ->
        hook.Trigger = trigger && (paths |> List.exists (matchesPath hook)))

let private evaluate (root: string) (trigger: HookTrigger) (paths: string list) : Client.Result<HookSummary option> =
    match load root with
    | Error message -> Error(Client.ValidationError message)
    | Ok configured ->
        let matching =
            configured
            |> List.filter (fun hook ->
                hook.Trigger = trigger && (paths |> List.exists (matchesPath hook)))
        match requireApproval root with
        | Error _ when matching.IsEmpty -> Ok None
        | Error problem -> Error problem
        | Ok () when matching.IsEmpty -> Ok None
        | Ok () ->
            let reports = ResizeArray<HookReport>()
            let failures = ResizeArray<string>()
            for hook in matching do
                for path in paths |> List.distinct do
                    if matchesPath hook path then
                        let report =
                            match oneFile root hook path with
                            | Ok(modified, edits, diagnostics, message) ->
                                { Hook = hook.Name; Path = path; Status = "ok"
                                  Message = message; Modified = modified
                                  FormattingEdits = edits; Diagnostics = diagnostics }
                            | Error message ->
                                if hook.Policy = Blocking then
                                    failures.Add(sprintf "%s on %s: %s" hook.Name path message)
                                { Hook = hook.Name; Path = path
                                  Status = if hook.Policy = Blocking then "blocking-failure" else "advisory-failure"
                                  Message = message; Modified = false
                                  FormattingEdits = 0; Diagnostics = [] }
                        reports.Add report
            Ok(Some
                { Trigger = if trigger = AfterWrite then "after-write" else "pre-commit"
                  Reports = List.ofSeq reports
                  ModifiedFiles =
                    reports
                    |> Seq.filter (fun report -> report.Modified)
                    |> Seq.map (fun report -> report.Path)
                    |> Seq.distinct
                    |> Seq.toList
                  WritePersisted = None
                  PersistedAfterHash = None
                  BlockingFailures = List.ofSeq failures })

let run root trigger paths =
    evaluate root trigger paths
    |> Result.bind (function
        | Some report when not report.BlockingFailures.IsEmpty ->
            Error(Client.ValidationError(
                "Code-quality hook blocked operation: " + String.Join("; ", report.BlockingFailures)))
        | report -> Ok report)

let private observedHash root path =
    try
        match validateFile root path with
        | Error _ -> None
        | Ok absolute ->
            use stream = File.OpenRead absolute
            let digest = System.Security.Cryptography.SHA256.HashData(stream)
            Some("sha256:" + Convert.ToHexString(digest).ToLowerInvariant())
    with _ -> None

// The original write has already persisted when this is called. Return a
// structured partial-success result, even when the formatter failed.
let runAfterWrite root path : Client.Result<HookSummary option> =
    match evaluate root AfterWrite [ path ] with
    | Ok None -> Ok None
    | Ok(Some report) ->
        Ok(Some { report with WritePersisted = Some true
                              PersistedAfterHash = observedHash root path })
    | Error problem ->
        let message = sprintf "%A" problem
        Ok(Some
            { Trigger = "after-write"
              Reports = [ { Hook = "code-quality"; Path = path; Status = "blocking-failure"
                            Message = message; Modified = false
                            FormattingEdits = 0; Diagnostics = [] } ]
              ModifiedFiles = []
              WritePersisted = Some true
              PersistedAfterHash = observedHash root path
              BlockingFailures = [ message ] })

let prewarm root =
    match load root with
    | Ok hooks when (hooks |> List.exists (fun hook -> hook.Adapter = "roslyn-format"))
                    && (requireApproval root |> Result.isOk) ->
        RoslynLsp.prewarm root
    | _ -> ()
