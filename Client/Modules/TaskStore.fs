module Client.TaskStore

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Common
open Client.Effect
open Client.IO
open FsToolkit.ErrorHandling
open Kehlet.FSharp.IO
open Kehlet.FSharp.IO.Effect.Operators

type TaskSummary =
    { Id: string
      Title: string
      Status: string
      DescriptionPath: string
      StatePath: string
      HasState: bool
      Owner: string option
      Dependencies: string list
      Revision: string option
      Diagnostics: string list }

type TaskDetails =
    { Task: TaskSummary
      Html: string
      StateJson: string option }

let private validId (id: string) =
    not (String.IsNullOrWhiteSpace id)
    && Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,120}$")
    && id <> "." && id <> ".."

let private hasLink (path: string) =
    let rec walk (dir: DirectoryInfo) =
        if isNull dir then false
        elif dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint) then true
        else walk dir.Parent
    walk (DirectoryInfo(path))

let private getString (json: JsonObject) (key: string) =
    let mutable value = Unchecked.defaultof<JsonNode>
    if json.TryGetPropertyValue(key, &value) && not (isNull value) then
        try Some(value.GetValue<string>()) with _ -> None
    else None

let private validateJson (id: string) (json: string) =
    match JsonNode.Parse(json) with
    | :? JsonObject as obj ->
        let stateId = getString obj "id"
        let status = getString obj "status"
        let version =
            try obj["schemaVersion"].GetValue<int>() with _ -> -1
        if version <> 1 then Error "Unsupported task state schema version."
        elif stateId <> Some id then Error "Task state id does not match its filename."
        elif not (List.contains (defaultArg status "") [ "open"; "active"; "blocked"; "completed" ]) then
            Error "Task state status is invalid."
        else Ok obj
    | _ -> Error "Task state must be a JSON object."

let private revision (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private directory (root: string) =
    try
        let config = Path.Combine(root, ".jarvis.json")
        let configured =
            if File.Exists config then
                if (File.GetAttributes config).HasFlag(FileAttributes.ReparsePoint) then
                    failwith "Task configuration is a symbolic link."
                match JsonNode.Parse(File.ReadAllText config) with
                | :? JsonObject as obj -> getString obj "taskDirectory" |> Option.defaultValue "docs/tasks"
                | _ -> failwith "Invalid .jarvis.json."
            else "docs/tasks"
        if Path.IsPathRooted configured || configured.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries) |> Array.exists ((=) "..") then
            Error(Client.ValidationError "Task directory must be project-relative.")
        else
            let path = Path.GetFullPath(Path.Combine(root, configured))
            if not (Client.ProjectPaths.isPathInRoot root path) || hasLink path then
                Error(Client.PermissionDenied "Task directory is not safe.")
            else Ok(configured.Replace('\\', '/'), path)
    with ex -> Error(Client.ValidationError($"Task directory configuration: {ex.Message}"))

let private canonicalTaskId (stem: string) =
    let matchResult = Regex.Match(stem, "^([0-9]+)-")
    if matchResult.Success then matchResult.Groups[1].Value else stem

let private descriptionCandidates (folder: string) (id: string) =
    let exact = Path.Combine(folder, id + ".html")
    if File.Exists exact then [ exact ]
    elif id |> Seq.forall Char.IsDigit then
        if Directory.Exists folder then
            Directory.GetFiles(folder, id + "-*.html")
            |> Array.filter (fun path -> canonicalTaskId(Path.GetFileNameWithoutExtension path) = id)
            |> Array.toList
        else []
    else []

let private descriptionPath (folder: string) (id: string) =
    match descriptionCandidates folder id with
    | [ path ] -> Ok path
    | [] -> Error "Missing HTML task description."
    | _ -> Error "Multiple HTML task descriptions share this task ID."

let private summary (relativeDir: string) (folder: string) (id: string) =
    let resolvedHtml = descriptionPath folder id
    let json = Path.Combine(folder, id + ".json")
    let stateExists = File.Exists json
    let docExists = resolvedHtml.IsOk
    let descriptionPath =
        match resolvedHtml with
        | Ok path -> relativeDir + "/" + Path.GetFileName(path)
        | Error _ -> relativeDir + "/" + id + ".html"
    let statePath = relativeDir + "/" + id + ".json"
    let mutable diagnostics = []
    let mutable title = id
    let mutable status = "open"
    let mutable owner = None
    let mutable dependencies = []
    let mutable hash = None
    match resolvedHtml with
    | Error message -> diagnostics <- message :: diagnostics
    | Ok path when (File.GetAttributes path).HasFlag(FileAttributes.ReparsePoint) ->
        diagnostics <- "Task HTML is a symbolic link." :: diagnostics
    | Ok _ -> ()
    if stateExists then
        if (File.GetAttributes json).HasFlag(FileAttributes.ReparsePoint) then
            diagnostics <- "Task JSON is a symbolic link." :: diagnostics
        else
            hash <- Some(revision json)
            try
                match validateJson id (File.ReadAllText json) with
                | Ok obj ->
                    title <- getString obj "title" |> Option.defaultValue id
                    status <- getString obj "status" |> Option.defaultValue "open"
                    owner <-
                        match obj["owner"] with
                        | :? JsonObject as ownerObj -> getString ownerObj "agent"
                        | _ -> None
                    dependencies <-
                        match obj["dependencies"] with
                        | :? JsonArray as values ->
                            values |> Seq.choose (fun value ->
                                if isNull value then None
                                else try Some(value.GetValue<string>()) with _ -> None) |> Seq.toList
                        | _ -> []
                | Error err -> diagnostics <- err :: diagnostics
            with ex -> diagnostics <- ex.Message :: diagnostics
    { Id = id
      Title = title
      Status = status
      DescriptionPath = descriptionPath
      StatePath = statePath
      HasState = stateExists
      Owner = owner
      Dependencies = dependencies
      Revision = hash
      Diagnostics = List.rev diagnostics }

let listTasks (cmd: ListTasksCommand) =
    effect {
        let! (FolderPath root) = ProjectPaths.resolveProjectRoot cmd.ProjectName
        let! (relative, folder) = fun _ -> directory root
        if not (Directory.Exists folder) then return []
        else
            let names =
                [ "*.html"; "*.json" ]
                |> List.collect (fun pattern -> Directory.GetFiles(folder, pattern) |> Array.toList)
                |> List.map (fun path ->
                    let stem = Path.GetFileNameWithoutExtension path
                    if Path.GetExtension path = ".html" then canonicalTaskId stem else stem)
                |> List.distinct
                |> List.sort
            return
                names
                |> List.map (fun id ->
                    if validId id then summary relative folder id
                    else
                        { Id = id; Title = id; Status = "invalid"; DescriptionPath = ""; StatePath = ""
                          HasState = false; Owner = None; Dependencies = []; Revision = None; Diagnostics = [ "Invalid task identifier." ] })
                |> List.filter (fun item ->
                    (cmd.Status.IsNone || Some item.Status = cmd.Status)
                    && (cmd.Owner.IsNone || item.Owner = cmd.Owner)
                    && (cmd.DependsOn.IsNone || item.Dependencies |> List.contains cmd.DependsOn.Value))
    }

let getTask (cmd: GetTaskCommand) =
    effect {
        if not (validId cmd.TaskId) then
            return! Client.ValidationError "Invalid task identifier." |> Effect.ofError
        else
            let! (FolderPath root) = ProjectPaths.resolveProjectRoot cmd.ProjectName
            let! (relative, folder) = fun _ -> directory root
            let id = canonicalTaskId cmd.TaskId
            let html = descriptionPath folder id
            let json = Path.Combine(folder, id + ".json")
            if html.IsError && not (File.Exists json) then
                return! Client.NotFoundError "Task does not exist." |> Effect.ofError
            elif (match html with
                  | Ok path -> (File.GetAttributes path).HasFlag(FileAttributes.ReparsePoint)
                  | Error _ -> false) then
                return! Client.PermissionDenied "Task description is a symbolic link." |> Effect.ofError
            elif html.IsError && (descriptionCandidates folder id).Length > 1 then
                return! Client.ValidationError "Multiple HTML descriptions share this task ID." |> Effect.ofError
            else
                return
                    { Task = summary relative folder id
                      Html = match html with Ok path -> File.ReadAllText path | Error _ -> ""
                      StateJson = if File.Exists json && not ((File.GetAttributes json).HasFlag(FileAttributes.ReparsePoint)) then Some(File.ReadAllText json) else None }
    }

type TaskMutationResult =
    { Task: TaskDetails
      CommitHash: string
      ChangedPaths: string list }

let private serialize (state: JsonObject) =
    state.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n"

let private initialState (id: string) (title: string) (dependencies: string list) =
    let obj = JsonObject()
    obj["schemaVersion"] <- JsonValue.Create(1)
    obj["id"] <- JsonValue.Create(id)
    obj["title"] <- JsonValue.Create(title)
    obj["status"] <- JsonValue.Create("open")
    obj["dependencies"] <- System.Text.Json.JsonSerializer.SerializeToNode(dependencies)
    obj["owner"] <- null
    obj["updatedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
    obj

let private atomicWrite (path: string) (text: string) =
    let temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
    try
        File.WriteAllText(temp, text, Encoding.UTF8)
        File.Move(temp, path, true)
    finally
        if File.Exists temp then File.Delete temp

let private git projectName args =
    ClientShell.runCommand
        { ProjectName = projectName
          Executable = "git"
          Args = args
          Reason = None
          WorkingDirectory = None
          TimeoutSeconds = Some 30
          MaxOutputBytes = Some(160 * 1024) }

let private taskLocks = ConcurrentDictionary<string, obj>(StringComparer.Ordinal)

let private serializeMutations projectName (operation: IO<'rt, 'a>) : IO<'rt, 'a> =
    fun rt ->
        let gate = taskLocks.GetOrAdd(projectName, fun _ -> obj())
        lock gate (fun () -> operation rt)

let private ensureUnstaged projectName (paths: string list) =
    effect {
        let! staged = git projectName ([ "diff"; "--cached"; "--name-only"; "--" ] @ paths)
        if staged.ExitCode <> 0 || staged.TimedOut || staged.Truncated then
            return! Client.ValidationError "Cannot inspect staged task paths." |> Effect.ofError
        elif not (String.IsNullOrWhiteSpace staged.StdOut) then
            return! Client.ValidationError "Task path has pre-existing staged changes." |> Effect.ofError
        else return ()
    }

let private ensureUnmodifiedTaskPath projectName path =
    effect {
        let! result = git projectName [ "status"; "--porcelain"; "--"; path ]
        if result.ExitCode <> 0 || result.TimedOut || result.Truncated then
            return! Client.ValidationError "Cannot verify task file cleanliness." |> Effect.ofError
        elif not (String.IsNullOrWhiteSpace result.StdOut) then
            return! Client.ValidationError "Task JSON has existing uncommitted changes; commit or reconcile them before updating." |> Effect.ofError
        else return ()
    }

let private commitScope projectName (paths: string list) message =
    effect {
        do! ensureUnstaged projectName paths
        return!
            ClientShell.gitCommit
                { ProjectName = projectName
                  Message = message
                  Body = None
                  Paths = paths
                  AllowEmpty = false }
    }

let private readMutableState root taskId expectedRevision =
    match directory root with
    | Error err -> Error err
    | Ok(relative, folder) ->
        if not (validId taskId) then Error(Client.ValidationError "Invalid task identifier.")
        else
            let htmlResult = descriptionPath folder taskId
            let path = Path.Combine(folder, taskId + ".json")
            if htmlResult.IsError then Error(Client.NotFoundError "Task HTML description does not exist or is ambiguous.")
            elif hasLink folder || (match htmlResult with Ok path -> (File.GetAttributes path).HasFlag(FileAttributes.ReparsePoint) | Error _ -> false) then
                Error(Client.PermissionDenied "Task description path is unsafe.")
            elif File.Exists path && (File.GetAttributes path).HasFlag(FileAttributes.ReparsePoint) then
                Error(Client.PermissionDenied "Task state path is unsafe.")
            else
                try
                    let obj =
                        if File.Exists path then
                            match validateJson taskId (File.ReadAllText path) with
                            | Ok obj -> obj
                            | Error message -> failwith message
                        else initialState taskId taskId []
                    match expectedRevision with
                    | Some expected when (if File.Exists path then revision path <> expected else expected <> "") ->
                        Error(Client.ValidationError "Task revision is stale.")
                    | _ -> Ok(relative, folder, path, obj)
                with ex -> Error(Client.ValidationError ex.Message)

let private updateState projectName taskId expectedRevision message mutate =
    effect {
        let! (FolderPath root) = ProjectPaths.resolveProjectRoot projectName
        let! (relative, folder, statePath, obj) = fun _ -> readMutableState root taskId expectedRevision
        let path = relative + "/" + taskId + ".json"
        do! ensureUnmodifiedTaskPath projectName path
        let! () = fun _ -> mutate obj
        obj["updatedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
        let! () =
            fun _ ->
                try
                    atomicWrite statePath (serialize obj)
                    Ok()
                with ex -> Error(Client.ExceptionError ex)
        let! commit = commitScope projectName [ path ] message
        let! details = getTask { ProjectName = projectName; TaskId = taskId }
        return { Task = details; CommitHash = commit.CommitHash; ChangedPaths = [ path ] }
    } |> serializeMutations projectName

let createTask (cmd: CreateTaskCommand) =
    effect {
        let id = cmd.TaskId |> Option.defaultValue (Guid.NewGuid().ToString("N"))
        if not (validId id) || cmd.Dependencies |> List.exists (validId >> not) then
            return! Client.ValidationError "Task IDs and dependency IDs must be safe filename identifiers." |> Effect.ofError
        elif String.IsNullOrWhiteSpace cmd.Title || String.IsNullOrWhiteSpace cmd.Html then
            return! Client.ValidationError "Task title and HTML are required." |> Effect.ofError
        else
            let! (FolderPath root) = ProjectPaths.resolveProjectRoot cmd.ProjectName
            let! (relative, folder) = fun _ -> directory root
            let html = Path.Combine(folder, id + ".html")
            let json = Path.Combine(folder, id + ".json")
            let hasExistingHtml = File.Exists html
            let canRecover =
                hasExistingHtml
                && not (File.Exists json)
                && not ((File.GetAttributes html).HasFlag(FileAttributes.ReparsePoint))
                && File.ReadAllText(html) = cmd.Html
            let collision =
                (descriptionCandidates folder id).Length > (if canRecover then 1 else 0)
                || (hasExistingHtml && not canRecover)
                || File.Exists json
                || hasLink folder
            if collision then
                return! Client.ValidationError "Task already exists or its directory is unsafe." |> Effect.ofError
            else
                let state = initialState id cmd.Title cmd.Dependencies
                let paths = [ relative + "/" + id + ".html"; relative + "/" + id + ".json" ]
                do! ensureUnstaged cmd.ProjectName paths
                let! () =
                    fun _ ->
                        try
                            Directory.CreateDirectory folder |> ignore
                            if not canRecover then atomicWrite html cmd.Html
                            atomicWrite json (serialize state)
                            Ok()
                        with ex ->
                            if File.Exists json then File.Delete json
                            if not canRecover && File.Exists html then File.Delete html
                            Error(Client.ExceptionError ex)
                let! commit = commitScope cmd.ProjectName paths $"Create task {id}: {cmd.Title}"
                let! details = getTask { ProjectName = cmd.ProjectName; TaskId = id }
                return { Task = details; CommitHash = commit.CommitHash; ChangedPaths = paths }
    } |> serializeMutations cmd.ProjectName

let private validStatus status =
    List.contains status [ "open"; "active"; "blocked"; "completed" ]

let updateTask (cmd: UpdateTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Update task {cmd.TaskId}"
        (fun obj ->
            match cmd.Status with
            | Some status when not (validStatus status) -> Error(Client.ValidationError "Unknown task status.")
            | _ when cmd.Dependencies |> Option.exists (List.exists (validId >> not)) ->
                Error(Client.ValidationError "Invalid dependency ID.")
            | _ ->
                cmd.Status |> Option.iter (fun status -> obj["status"] <- JsonValue.Create(status))
                cmd.Title |> Option.iter (fun title -> obj["title"] <- JsonValue.Create(title))
                cmd.Dependencies |> Option.iter (fun deps -> obj["dependencies"] <- System.Text.Json.JsonSerializer.SerializeToNode(deps))
                cmd.DescriptionCommit |> Option.iter (fun commit -> obj["descriptionCommit"] <- JsonValue.Create(commit))
                Ok())

let claimTask (cmd: ClaimTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Claim task {cmd.TaskId}"
        (fun obj ->
            if String.IsNullOrWhiteSpace cmd.Owner then
                Error(Client.ValidationError "Owner is required.")
            else
                let existing =
                    match obj["owner"] with
                    | :? JsonObject as owner -> getString owner "agent"
                    | _ -> None
                match existing with
                | Some other when other <> cmd.Owner && not cmd.AllowReassign ->
                    Error(Client.ValidationError "Task is claimed by another agent.")
                | _ ->
                    let owner = JsonObject()
                    owner["agent"] <- JsonValue.Create(cmd.Owner)
                    owner["worktree"] <- JsonValue.Create(cmd.ProjectName)
                    obj["owner"] <- owner
                    obj["status"] <- JsonValue.Create("active")
                    Ok())

let checkpointTask (cmd: CheckpointTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Checkpoint task {cmd.TaskId}"
        (fun obj ->
            if not (validStatus cmd.Status) then
                Error(Client.ValidationError "Unknown task status.")
            else
                let checkpoint = JsonObject()
                checkpoint["summary"] <- JsonValue.Create(cmd.Summary)
                checkpoint["nextAction"] <- JsonValue.Create(cmd.NextAction)
                checkpoint["verified"] <- System.Text.Json.JsonSerializer.SerializeToNode(cmd.Verified)
                checkpoint["blocker"] <- cmd.Blocker |> Option.map JsonValue.Create |> Option.defaultValue null
                checkpoint["lastCommit"] <- cmd.LastCommit |> Option.map JsonValue.Create |> Option.defaultValue null
                obj["checkpoint"] <- checkpoint
                cmd.DescriptionCommit |> Option.iter (fun commit -> obj["descriptionCommit"] <- JsonValue.Create(commit))
                obj["status"] <- JsonValue.Create(cmd.Status)
                Ok())

let completeTask (cmd: CompleteTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Complete task {cmd.TaskId}"
        (fun obj ->
            let completion = JsonObject()
            completion["verified"] <- System.Text.Json.JsonSerializer.SerializeToNode(cmd.Verified)
            completion["finalCommit"] <- cmd.FinalCommit |> Option.map JsonValue.Create |> Option.defaultValue null
            completion["completedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
            obj["completion"] <- completion
            obj["status"] <- JsonValue.Create("completed")
            Ok())

let getTaskHistory (cmd: GetTaskHistoryCommand) =
    effect {
        if not (validId cmd.TaskId) then
            return! Client.ValidationError "Invalid task identifier." |> Effect.ofError
        else
            let! (FolderPath root) = ProjectPaths.resolveProjectRoot cmd.ProjectName
            let! (relative, _) = fun _ -> directory root
            let limit = cmd.Limit |> Option.defaultValue 20 |> max 1 |> min 100
            let! task = getTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId }
            let! result =
                git cmd.ProjectName
                    [ "log"; $"-n{limit}"; "--format=%H%x09%s"; "--patch"; "--"
                      task.Task.DescriptionPath
                      task.Task.StatePath ]
            if result.ExitCode <> 0 || result.TimedOut then
                return! Client.ValidationError "Cannot read task Git history." |> Effect.ofError
            elif result.Truncated then
                return result.StdOut + "
[Git history output truncated]
"
            else
                return result.StdOut
    }

type WorktreeTaskGroup =
    { Worktree: Worktrees.WorktreeInfo
      Tasks: TaskSummary list
      Error: string option }

let compareWorktreeTasks (cmd: CompareWorktreeTasksCommand) =
    fun rt ->
        let projectName =
            let separator = cmd.ProjectName.IndexOf("::", StringComparison.Ordinal)
            if separator >= 0 then cmd.ProjectName.Substring(0, separator)
            else cmd.ProjectName
        match Worktrees.listWorktrees { ProjectName = projectName } rt with
        | Error error -> Error error
        | Ok worktrees ->
            worktrees
            |> List.map (fun worktree ->
                let selector =
                    if worktree.IsMain then projectName
                    else projectName + "::" + Path.GetFileName(worktree.Path)
                match listTasks
                    { ProjectName = selector
                      Status = None
                      Owner = None
                      DependsOn = None } rt with
                | Ok tasks -> { Worktree = worktree; Tasks = tasks; Error = None }
                | Error error -> { Worktree = worktree; Tasks = []; Error = Some(string error) })
            |> Ok
