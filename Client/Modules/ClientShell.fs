module Client.ClientShell

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Common
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

module private Core =
    let private defaultTimeoutSeconds = 60
    let private defaultMaxOutputBytes = 64 * 1024

    let private resolveWorkingDirectory projectName workingDirectory =
        match workingDirectory with
        | None -> ProjectPaths.resolveProjectRoot projectName
        | Some relativePath -> ProjectPaths.resolveProjectFolder projectName relativePath

    let private validateRelativePath = ProjectPaths.validateProjectRelativePath

    let private deniedExecutables =
        set [ "bash"; "sh"; "zsh"; "fish"; "cmd"; "cmd.exe"; "powershell"; "powershell.exe"; "pwsh"; "pwsh.exe" ]

    let private validateExecutable executable =
        if String.IsNullOrWhiteSpace executable then
            Error(Client.ValidationError "Executable cannot be empty.")
        elif deniedExecutables.Contains(executable.Trim().ToLowerInvariant()) then
            Error(Client.PermissionDenied $"Shell executable is denied by default: {executable}")
        else
            Ok executable

    let private takeUtf8Prefix maxBytes (value: string) =
        let output = Text.StringBuilder()
        let mutable bytes = 0
        let mutable accepting = true

        for rune in value.EnumerateRunes() do
            if accepting then
                let runeBytes = rune.Utf8SequenceLength

                if bytes + runeBytes <= maxBytes then
                    output.Append(rune.ToString()) |> ignore
                    bytes <- bytes + runeBytes
                else
                    accepting <- false

        output.ToString()

    let private readBounded maxBytes (reader: StreamReader) =
        task {
            let buffer = Array.zeroCreate<char> 4096
            let output = Text.StringBuilder()
            let mutable bytes = 0
            let mutable truncated = false
            let mutable reading = true

            while reading do
                let! count = reader.ReadAsync(buffer, 0, buffer.Length)

                if count = 0 then
                    reading <- false
                else
                    let chunk = String(buffer, 0, count)
                    let chunkBytes = Text.Encoding.UTF8.GetByteCount chunk
                    let remaining = max 0 (maxBytes - bytes)

                    if remaining > 0 then
                        let fragment = takeUtf8Prefix remaining chunk
                        let addedBytes = Text.Encoding.UTF8.GetByteCount fragment
                        output.Append(fragment) |> ignore
                        bytes <- bytes + addedBytes
                        truncated <- truncated || addedBytes < chunkBytes
                    else
                        truncated <- true

            return output.ToString(), truncated
        }

    let private runProcess workingDirectory executable args timeoutSeconds maxOutputBytes : Client.Result<RunCommandResult> =
        try
            match validateExecutable executable with
            | Error error -> Error error
            | Ok executable ->
                use proc = new Process()

                proc.StartInfo.FileName <- executable
                proc.StartInfo.WorkingDirectory <- workingDirectory
                proc.StartInfo.RedirectStandardOutput <- true
                proc.StartInfo.RedirectStandardError <- true
                proc.StartInfo.UseShellExecute <- false
                proc.StartInfo.CreateNoWindow <- true

                ProcessEnvironment.apply proc.StartInfo
                args |> List.iter proc.StartInfo.ArgumentList.Add

                if not (proc.Start()) then
                    Error(Client.ContextError "Failed to start process.")
                else
                    let stdoutTask = readBounded maxOutputBytes proc.StandardOutput
                    let stderrTask = readBounded maxOutputBytes proc.StandardError
                    let timeoutMs = timeoutSeconds * 1000
                    let exited = proc.WaitForExit timeoutMs

                    if not exited then
                        try
                            proc.Kill(entireProcessTree = true)
                            proc.WaitForExit()
                        with _ -> ()

                    let stdout, stdoutTruncated = stdoutTask.Result
                    let stderr, stderrTruncated = stderrTask.Result

                    Ok
                        { ExitCode = if exited then proc.ExitCode else -1
                          TimedOut = not exited
                          StdOut = stdout
                          StdErr = stderr
                          Truncated = stdoutTruncated || stderrTruncated }
        with ex ->
            Error(ExceptionError ex)

    let private run projectName workingDirectory executable args timeoutSeconds maxOutputBytes =
        effect {
            let timeoutSeconds = timeoutSeconds |> Option.defaultValue defaultTimeoutSeconds
            let maxOutputBytes = maxOutputBytes |> Option.defaultValue defaultMaxOutputBytes

            if timeoutSeconds <= 0 then
                return! Client.ValidationError "TimeoutSeconds must be greater than zero." |> Effect.ofError
            elif maxOutputBytes <= 0 then
                return! Client.ValidationError "MaxOutputBytes must be greater than zero." |> Effect.ofError
            else
                let! (FolderPath workingDirectory) = resolveWorkingDirectory projectName workingDirectory
                let! result = fun _ -> runProcess workingDirectory executable args timeoutSeconds maxOutputBytes

                return
                    { ExitCode = result.ExitCode
                      TimedOut = result.TimedOut
                      StdOut = result.StdOut
                      StdErr = result.StdErr
                      Truncated = result.Truncated }
        }

    let runCommand (cmd: RunCommandCommand) =
        run cmd.ProjectName cmd.WorkingDirectory cmd.Executable cmd.Args cmd.TimeoutSeconds cmd.MaxOutputBytes

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private optionalString name (element: JsonElement) =
        match tryProperty name element with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() with
            | null -> Ok None
            | text when String.IsNullOrWhiteSpace text -> Ok None
            | text -> Ok(Some text)
        | _ -> Error(Client.ValidationError $".jarvis.json task property '{name}' must be a string or null.")

    let private optionalInt name (element: JsonElement) =
        match tryProperty name element with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Null -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then Ok(Some parsed)
            else Error(Client.ValidationError $".jarvis.json task property '{name}' must be a 32-bit integer.")
        | _ -> Error(Client.ValidationError $".jarvis.json task property '{name}' must be an integer or null.")

    let private args (element: JsonElement) =
        match tryProperty "args" element with
        | None -> Ok []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.fold
                (fun state item ->
                    match state with
                    | Error error -> Error error
                    | Ok values when item.ValueKind = JsonValueKind.String ->
                        Ok(item.GetString() :: values)
                    | Ok _ ->
                        Error(Client.ValidationError ".jarvis.json task args must contain only strings."))
                (Ok [])
            |> Result.map (List.rev >> List.map (Option.ofObj >> Option.defaultValue ""))
        | _ -> Error(Client.ValidationError ".jarvis.json task property 'args' must be an array of strings.")

    let private parseTask (property: JsonProperty) =
        let name = property.Name
        let element = property.Value

        if String.IsNullOrWhiteSpace name then
            Error(Client.ValidationError ".jarvis.json task names cannot be empty.")
        elif element.ValueKind <> JsonValueKind.Object then
            Error(Client.ValidationError $".jarvis.json task '{name}' must be an object.")
        else
            match tryProperty "executable" element with
            | Some executable when executable.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(executable.GetString())) ->
                match optionalString "description" element, args element, optionalString "workingDirectory" element, optionalInt "timeoutSeconds" element, optionalInt "maxOutputBytes" element with
                | Ok description, Ok args, Ok workingDirectory, Ok timeoutSeconds, Ok maxOutputBytes ->
                    Ok
                        { Name = name
                          Description = description
                          Executable = executable.GetString()
                          Args = args
                          WorkingDirectory = workingDirectory
                          TimeoutSeconds = timeoutSeconds
                          MaxOutputBytes = maxOutputBytes }
                | Error error, _, _, _, _
                | _, Error error, _, _, _
                | _, _, Error error, _, _
                | _, _, _, Error error, _
                | _, _, _, _, Error error -> Error error
            | _ ->
                Error(Client.ValidationError $".jarvis.json task '{name}' requires a non-empty string 'executable'.")

    let private parseProjectTasks (json: string) =
        try
            use document = JsonDocument.Parse(json)

            match tryProperty "tasks" document.RootElement with
            | None -> Ok { Tasks = [] }
            | Some tasks when tasks.ValueKind = JsonValueKind.Object ->
                tasks.EnumerateObject()
                |> Seq.fold
                    (fun state property ->
                        match state, parseTask property with
                        | Ok values, Ok value -> Ok(value :: values)
                        | Error error, _ -> Error error
                        | _, Error error -> Error error)
                    (Ok [])
                |> Result.map (List.rev >> fun tasks -> { Tasks = tasks })
            | _ ->
                Error(Client.ValidationError ".jarvis.json property 'tasks' must be an object.")
        with
        | :? JsonException as ex ->
            Error(Client.ValidationError $"Invalid .jarvis.json: {ex.Message}")
        | ex ->
            Error(ExceptionError ex)

    let listProjectTasks (cmd: ListProjectTasksCommand) =
        fun rt ->
            match ProjectPaths.resolveProjectFile cmd.ProjectName ".jarvis.json" rt with
            | Error(NotFoundError _) -> Ok { Tasks = [] }
            | Error error -> Error error
            | Ok configPath ->
                match FileIO.readAllText configPath rt with
                | Error(ExceptionError (:? FileNotFoundException)) -> Ok { Tasks = [] }
                | Error error -> Error error
                | Ok(Content json) -> parseProjectTasks json

    let resolveProjectTask (cmd: RunProjectTaskCommand) =
        fun rt ->
            match listProjectTasks { ProjectName = cmd.ProjectName } rt with
            | Error error -> Error error
            | Ok result ->
                result.Tasks
                |> List.tryFind (fun task -> String.Equals(task.Name, cmd.TaskName, StringComparison.Ordinal))
                |> Result.requireSome (NotFoundError $"Unknown project task: {cmd.TaskName}")

    let runProjectTask projectName (task: ProjectTaskDefinition) =
        run projectName task.WorkingDirectory task.Executable task.Args task.TimeoutSeconds task.MaxOutputBytes

    let private git projectName args maxOutputBytes =
        run projectName None "git" args (Some defaultTimeoutSeconds) maxOutputBytes

    let getGitStatus (cmd: GitStatusCommand) =
        git cmd.ProjectName [ "status"; "--short"; "--branch" ] None

    let getGitDiff (cmd: GitDiffCommand) =
        match cmd.Path with
        | None -> git cmd.ProjectName [ "diff"; "--" ] cmd.MaxOutputBytes
        | Some path ->
            effect {
                let! safePath = fun _ -> validateRelativePath path
                return! git cmd.ProjectName [ "diff"; "--"; safePath ] cmd.MaxOutputBytes
            }

    let gitCommit (cmd: GitCommitCommand) =
        effect {
            if String.IsNullOrWhiteSpace cmd.Message then
                return! Client.ValidationError "Commit message cannot be empty." |> Effect.ofError
            elif cmd.Paths.IsEmpty && not cmd.AllowEmpty then
                return! Client.ValidationError "GitCommit requires at least one path unless AllowEmpty = true." |> Effect.ofError
            else
                let! safePaths =
                    fun _ ->
                        cmd.Paths
                        |> List.map validateRelativePath
                        |> List.fold
                            (fun state item ->
                                match state, item with
                                | Ok values, Ok value -> Ok(value :: values)
                                | Error error, _ -> Error error
                                | _, Error error -> Error error)
                            (Ok [])
                        |> Result.map List.rev

                let! addResult =
                    if safePaths.IsEmpty then
                        fun _ -> Ok None
                    else
                        git cmd.ProjectName ([ "add"; "--" ] @ safePaths) None |>> Some

                let! stagedResult =
                    if safePaths.IsEmpty then
                        git cmd.ProjectName [ "diff"; "--cached"; "--quiet"; "--" ] None |>> Some
                    else
                        fun _ -> Ok None

                do!
                    match stagedResult with
                    | Some result when result.ExitCode = 1 ->
                        Client.ValidationError "Cannot create an empty commit while staged changes already exist." |> Effect.ofError
                    | Some result when result.ExitCode <> 0 ->
                        Client.GenericError result.StdErr |> Effect.ofError
                    | _ -> fun _ -> Ok()

                match addResult with
                | Some result when result.ExitCode <> 0 ->
                    return! Client.GenericError result.StdErr |> Effect.ofError
                | _ ->
                    let commitArgs =
                        [ yield "commit"
                          if cmd.AllowEmpty then yield "--allow-empty"
                          yield "-m"
                          yield cmd.Message
                          match cmd.Body with
                          | Some body when not (String.IsNullOrWhiteSpace body) ->
                              yield "-m"
                              yield body
                          | _ -> ()
                          if not safePaths.IsEmpty then
                              yield "--only"
                              yield "--"
                              yield! safePaths ]

                    let! commitResult = git cmd.ProjectName commitArgs None

                    if commitResult.ExitCode <> 0 then
                        return! Client.GenericError commitResult.StdErr |> Effect.ofError
                    else
                        let! revParse = git cmd.ProjectName [ "rev-parse"; "HEAD" ] None
                        let hash = revParse.StdOut.Trim()

                        return
                            { CommitHash = hash
                              Summary = commitResult.StdOut.Trim()
                              StdOut = commitResult.StdOut
                              StdErr = commitResult.StdErr }
        }

let runCommand cmd = Core.runCommand cmd
let listProjectTasks cmd = Core.listProjectTasks cmd
let resolveProjectTask cmd = Core.resolveProjectTask cmd

let runProjectTask projectName (task: ProjectTaskDefinition) =
    Core.runProjectTask projectName task

let projectTaskAsRunCommand projectName (task: ProjectTaskDefinition) =
    { ProjectName = projectName
      Executable = task.Executable
      Args = task.Args
      WorkingDirectory = task.WorkingDirectory
      TimeoutSeconds = task.TimeoutSeconds
      MaxOutputBytes = task.MaxOutputBytes }

let getGitStatus cmd = Core.getGitStatus cmd
let getGitDiff cmd = Core.getGitDiff cmd
let gitCommit cmd = Core.gitCommit cmd
