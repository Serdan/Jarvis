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

    [<CLIMutable>]
    type PersistedWorkTask =
        { Version: int
          TaskId: string
          TaskPath: string
          TaskCommit: string
          Status: string
          Summary: string
          NextAction: string
          Verified: string array
          Blocker: string
          CreatedAt: DateTimeOffset
          UpdatedAt: DateTimeOffset
          LastCommit: string }

    let private workTaskVersion = 1
    let private workTaskDirectory = Path.Combine(".jarvis", "tasks")

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

    let private parseProjectTasks (json: string) : Result<ListProjectTasksResult> =
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

    let listProjectTasks (cmd: ListProjectTasksCommand) : IO<'rt, ListProjectTasksResult> =
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

    let private validateWorkTaskId (taskId: string) =
        let value = if isNull taskId then "" else taskId.Trim()
        if String.IsNullOrWhiteSpace value then
            Error(Client.ValidationError "TaskId cannot be empty.")
        elif value = "." || value = ".." || value.Length > 100 then
            Error(Client.ValidationError "TaskId is invalid.")
        elif value |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.')) then
            Error(Client.ValidationError "TaskId may contain only letters, digits, '-', '_' and '.'.")
        else
            Ok value

    let private optionString value =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private workTaskRelativePath taskId =
        Path.Combine(workTaskDirectory, taskId + ".json")

    let private serializeWorkTask (state: PersistedWorkTask) =
        let values = System.Collections.Generic.Dictionary<string, obj>()
        values["Version"] <- box state.Version
        values["TaskId"] <- box state.TaskId
        values["TaskPath"] <- box state.TaskPath
        values["TaskCommit"] <- box state.TaskCommit
        values["Status"] <- box state.Status
        values["Summary"] <- box state.Summary
        values["NextAction"] <- box state.NextAction
        values["Verified"] <- box state.Verified
        values["Blocker"] <- box state.Blocker
        values["CreatedAt"] <- box state.CreatedAt
        values["UpdatedAt"] <- box state.UpdatedAt
        values["LastCommit"] <- box state.LastCommit
        JsonSerializer.Serialize(values, JsonSerializerOptions(WriteIndented = true))

    let private parseWorkTask (json: string) =
        try
            use document = JsonDocument.Parse(json)
            let root = document.RootElement
            let required (name: string) : JsonElement =
                match root.TryGetProperty name with
                | true, value -> value
                | _ -> raise (JsonException($"Missing property '{name}'."))
            let nullableString (name: string) =
                let value = required name
                if value.ValueKind = JsonValueKind.Null then null else value.GetString()
            let verified =
                let value = required "Verified"
                if value.ValueKind = JsonValueKind.Null then [||]
                elif value.ValueKind = JsonValueKind.Array then
                    value.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                else raise (JsonException("Property 'Verified' must be an array."))
            let state =
                { Version = (required "Version").GetInt32()
                  TaskId = (required "TaskId").GetString()
                  TaskPath = (required "TaskPath").GetString()
                  TaskCommit = (required "TaskCommit").GetString()
                  Status = (required "Status").GetString()
                  Summary = nullableString "Summary"
                  NextAction = nullableString "NextAction"
                  Verified = verified
                  Blocker = nullableString "Blocker"
                  CreatedAt = (required "CreatedAt").GetDateTimeOffset()
                  UpdatedAt = (required "UpdatedAt").GetDateTimeOffset()
                  LastCommit = nullableString "LastCommit" }
            if state.Version <> workTaskVersion
               || String.IsNullOrWhiteSpace state.TaskId
               || String.IsNullOrWhiteSpace state.TaskPath
               || String.IsNullOrWhiteSpace state.TaskCommit then
                Error(Client.ValidationError "Unsupported or invalid persisted work-task state.")
            else
                Ok state
        with
        | :? JsonException as ex ->
            Error(Client.ValidationError $"Invalid persisted work-task state: {ex.Message}")
        | :? InvalidOperationException as ex ->
            Error(Client.ValidationError $"Invalid persisted work-task state: {ex.Message}")

    let private readWorkTask projectName taskId =
        fun rt ->
            match validateWorkTaskId taskId with
            | Error error -> Error error
            | Ok validated ->
                match ProjectPaths.resolveProjectFile projectName (workTaskRelativePath validated) rt with
                | Error error -> Error error
                | Ok path ->
                    match FileIO.readAllText path rt with
                    | Error error -> Error error
                    | Ok(Content json) -> parseWorkTask json

    let private writeWorkTask projectName (state: PersistedWorkTask) =
        fun rt ->
            match ProjectPaths.resolveWritableProjectFile projectName (workTaskRelativePath state.TaskId) rt with
            | Error error -> Error error
            | Ok(FilePath fullPath) ->
                try
                    let parent = Path.GetDirectoryName fullPath
                    Directory.CreateDirectory(parent) |> ignore
                    File.WriteAllText(fullPath, serializeWorkTask state)
                    Ok()
                with ex ->
                    Error(ExceptionError ex)

    let private deleteWorkTask projectName taskId =
        fun rt ->
            match validateWorkTaskId taskId with
            | Error error -> Error error
            | Ok validated ->
                match ProjectPaths.resolveProjectPath projectName (workTaskRelativePath validated) rt with
                | Error error -> Error error
                | Ok fullPath ->
                    try
                        if File.Exists fullPath then File.Delete fullPath
                        Ok()
                    with ex ->
                        Error(ExceptionError ex)

    let private gitOk projectName args rt =
        match git projectName args None rt with
        | Error error -> Error error
        | Ok result when result.ExitCode = 0 -> Ok result.StdOut
        | Ok result ->
            let message =
                if String.IsNullOrWhiteSpace result.StdErr then result.StdOut else result.StdErr
            Error(Client.GenericError(message.Trim()))

    let private currentTaskCommit projectName taskPath rt =
        match git projectName [ "log"; "-1"; "--format=%H"; "--"; taskPath ] None rt with
        | Error error -> Error error
        | Ok result when result.ExitCode = 0 && not (String.IsNullOrWhiteSpace result.StdOut) ->
            Ok(Some(result.StdOut.Trim()))
        | Ok _ -> Ok None

    let private taskDirty projectName taskPath rt =
        match gitOk projectName [ "status"; "--porcelain"; "--"; taskPath ] rt with
        | Error error -> Error error
        | Ok output -> Ok(not (String.IsNullOrWhiteSpace output))

    let private taskContentAt projectName commit taskPath rt =
        match git projectName [ "show"; $"{commit}:{taskPath}" ] None rt with
        | Error error -> Error error
        | Ok result when result.ExitCode = 0 -> Ok(Some result.StdOut)
        | Ok _ -> Ok None

    let private ensureWorkTaskGitExclude projectName rt =
        match ProjectPaths.resolveProjectRoot projectName rt with
        | Error error -> Error error
        | Ok(FolderPath projectRoot) ->
            match gitOk projectName [ "rev-parse"; "--git-path"; "info/exclude" ] rt with
            | Error error -> Error error
            | Ok rawPath ->
                try
                    let path = rawPath.Trim()
                    let fullPath =
                        if Path.IsPathRooted path then path
                        else Path.GetFullPath(Path.Combine(projectRoot, path))
                    let parent = Path.GetDirectoryName fullPath
                    if not (String.IsNullOrWhiteSpace parent) then
                        Directory.CreateDirectory(parent) |> ignore
                    let entry = "/.jarvis/tasks/"
                    let existing =
                        if File.Exists fullPath then File.ReadAllLines fullPath
                        else [||]
                    if existing |> Array.exists (fun line -> String.Equals(line.Trim(), entry, StringComparison.Ordinal)) |> not then
                        let prefix =
                            if File.Exists fullPath && FileInfo(fullPath).Length > 0L then
                                let text = File.ReadAllText fullPath
                                if text.EndsWith("\n", StringComparison.Ordinal) then "" else Environment.NewLine
                            else ""
                        File.AppendAllText(fullPath, prefix + entry + Environment.NewLine)
                    Ok()
                with ex ->
                    Error(ExceptionError ex)

    let private hydrateWorkTask projectName includeContent (state: PersistedWorkTask) =
        fun rt ->
            match currentTaskCommit projectName state.TaskPath rt, taskDirty projectName state.TaskPath rt with
            | Error error, _ | _, Error error -> Error error
            | Ok currentCommit, Ok dirty ->
                let contentResult =
                    if includeContent then
                        match currentCommit with
                        | Some commit -> taskContentAt projectName commit state.TaskPath rt
                        | None -> Ok None
                    else Ok None
                match contentResult with
                | Error error -> Error error
                | Ok content ->
                    Ok
                        { TaskId = state.TaskId
                          TaskPath = state.TaskPath
                          TaskCommit = state.TaskCommit
                          CurrentTaskCommit = currentCommit
                          TaskChanged = currentCommit <> Some state.TaskCommit
                          TaskDirty = dirty
                          Status = state.Status
                          Summary = optionString state.Summary
                          NextAction = optionString state.NextAction
                          Verified = state.Verified |> Option.ofObj |> Option.defaultValue [||] |> Array.toList
                          Blocker = optionString state.Blocker
                          CreatedAt = state.CreatedAt
                          UpdatedAt = state.UpdatedAt
                          LastCommit = optionString state.LastCommit
                          TaskContent = content }

    let attachWorkTask (cmd: AttachWorkTaskCommand) =
        fun rt ->
            match validateWorkTaskId cmd.TaskId, validateRelativePath cmd.TaskPath with
            | Error error, _ | _, Error error -> Error error
            | Ok taskId, Ok taskPath ->
                match ProjectPaths.resolveProjectFile cmd.ProjectName taskPath rt with
                | Error _ -> Error(Client.ValidationError $"Task description does not exist: {taskPath}")
                | Ok _ ->
                    match git cmd.ProjectName [ "ls-files"; "--error-unmatch"; "--"; taskPath ] None rt with
                    | Error error -> Error error
                    | Ok tracked when tracked.ExitCode <> 0 ->
                        Error(Client.ValidationError "Work tasks must reference a task description tracked by git.")
                    | Ok _ ->
                        match taskDirty cmd.ProjectName taskPath rt, currentTaskCommit cmd.ProjectName taskPath rt with
                        | Error error, _ | _, Error error -> Error error
                        | Ok true, _ ->
                            Error(Client.ValidationError "Commit the task description before attaching Jarvis execution state.")
                        | Ok false, Ok None ->
                            Error(Client.ValidationError "The task description has no committed revision.")
                        | Ok false, Ok(Some commit) ->
                            match ensureWorkTaskGitExclude cmd.ProjectName rt with
                            | Error error -> Error error
                            | Ok() ->
                                match readWorkTask cmd.ProjectName taskId rt with
                                | Ok _ -> Error(Client.ValidationError $"Work task '{taskId}' is already attached.")
                                | Error(NotFoundError _) ->
                                    let now = DateTimeOffset.UtcNow
                                    let state =
                                        { Version = workTaskVersion
                                          TaskId = taskId
                                          TaskPath = taskPath.Replace('\\', '/')
                                          TaskCommit = commit
                                          Status = "active"
                                          Summary = null
                                          NextAction = null
                                          Verified = [||]
                                          Blocker = null
                                          CreatedAt = now
                                          UpdatedAt = now
                                          LastCommit = null }
                                    match writeWorkTask cmd.ProjectName state rt with
                                    | Error error -> Error error
                                    | Ok() -> hydrateWorkTask cmd.ProjectName true state rt
                                | Error error -> Error error

    let getWorkTask (cmd: GetWorkTaskCommand) =
        fun rt ->
            match readWorkTask cmd.ProjectName cmd.TaskId rt with
            | Error error -> Error error
            | Ok state -> hydrateWorkTask cmd.ProjectName true state rt

    let listWorkTasks (cmd: ListWorkTasksCommand) =
        fun rt ->
            match ProjectPaths.resolveProjectPath cmd.ProjectName workTaskDirectory rt with
            | Error error -> Error error
            | Ok directory when not (Directory.Exists directory) -> Ok { Tasks = [] }
            | Ok directory ->
                let states =
                    Directory.EnumerateFiles(directory, "*.json")
                    |> Seq.choose (fun path ->
                        try
                            match parseWorkTask (File.ReadAllText path) with
                            | Ok state -> Some state
                            | Error _ -> None
                        with _ -> None)
                    |> Seq.sortBy (fun state -> state.UpdatedAt)
                    |> Seq.toList
                let folder =
                    states
                    |> List.fold (fun acc state ->
                        match acc with
                        | Error error -> Error error
                        | Ok tasks ->
                            match hydrateWorkTask cmd.ProjectName false state rt with
                            | Ok task -> Ok(task :: tasks)
                            | Error error -> Error error) (Ok [])
                folder |> Result.map (List.rev >> fun tasks -> { Tasks = tasks })

    let checkpointWorkTask (cmd: CheckpointWorkTaskCommand) =
        fun rt ->
            let status = if isNull cmd.Status then "" else cmd.Status.Trim().ToLowerInvariant()
            if status <> "active" && status <> "blocked" then
                Error(Client.ValidationError "Checkpoint status must be 'active' or 'blocked'.")
            else
                match readWorkTask cmd.ProjectName cmd.TaskId rt with
                | Error error -> Error error
                | Ok state ->
                    let updated =
                        { state with
                            Status = status
                            Summary = if String.IsNullOrWhiteSpace cmd.Summary then null else cmd.Summary.Trim()
                            NextAction = if String.IsNullOrWhiteSpace cmd.NextAction then null else cmd.NextAction.Trim()
                            Verified = cmd.Verified |> List.toArray
                            Blocker = cmd.Blocker |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue null
                            LastCommit = cmd.LastCommit |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue null
                            UpdatedAt = DateTimeOffset.UtcNow }
                    match writeWorkTask cmd.ProjectName updated rt with
                    | Error error -> Error error
                    | Ok() -> hydrateWorkTask cmd.ProjectName true updated rt

    let completeWorkTask (cmd: CompleteWorkTaskCommand) =
        fun rt ->
            match readWorkTask cmd.ProjectName cmd.TaskId rt with
            | Error error -> Error error
            | Ok state ->
                match gitOk cmd.ProjectName [ "rev-parse"; "HEAD" ] rt with
                | Error error -> Error error
                | Ok head ->
                    let completedAt = DateTimeOffset.UtcNow
                    match deleteWorkTask cmd.ProjectName state.TaskId rt with
                    | Error error -> Error error
                    | Ok() ->
                        Ok
                            { TaskId = state.TaskId
                              TaskPath = state.TaskPath
                              TaskCommit = state.TaskCommit
                              FinalCommit = head.Trim()
                              CompletedAt = completedAt }

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
let attachWorkTask cmd = Core.attachWorkTask cmd
let getWorkTask cmd = Core.getWorkTask cmd
let listWorkTasks cmd = Core.listWorkTasks cmd
let checkpointWorkTask cmd = Core.checkpointWorkTask cmd
let completeWorkTask cmd = Core.completeWorkTask cmd

let runProjectTask projectName (task: ProjectTaskDefinition) =
    Core.runProjectTask projectName task

let projectTaskAsRunCommand projectName (task: ProjectTaskDefinition) =
    { ProjectName = projectName
      Executable = task.Executable
      Args = task.Args
      Reason = None
      WorkingDirectory = task.WorkingDirectory
      TimeoutSeconds = task.TimeoutSeconds
      MaxOutputBytes = task.MaxOutputBytes }

let getGitStatus cmd = Core.getGitStatus cmd
let getGitDiff cmd = Core.getGitDiff cmd
let gitCommit cmd = Core.gitCommit cmd
