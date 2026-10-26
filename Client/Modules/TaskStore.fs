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
      Diagnostics: string list
      NextActor: string option
      LastUpdatedBy: string option
      AssignmentRevision: string option
      UpdatedAt: DateTimeOffset option
      CheckpointSummary: string option
      CheckpointNextAction: string option
      CheckpointLastCommit: string option
      CheckpointBlocker: string option
      CompletionFinalCommit: string option }

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

let private normalizeStatus value =
    match value with
    | "in_progress" -> "active"
    | other -> other

let private validateJson (id: string) (json: string) =
    match JsonNode.Parse(json) with
    | :? JsonObject as obj ->
        let stateId = getString obj "id"
        let status = getString obj "status"
        let version =
            try obj["schemaVersion"].GetValue<int>() with _ -> -1
        if version <> 1 then Error "Unsupported task state schema version."
        elif stateId <> Some id then Error "Task state id does not match its filename."
        elif not (List.contains (defaultArg status "" |> normalizeStatus) [ "open"; "active"; "blocked"; "completed" ]) then
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
    // Legacy numeric task IDs use a numeric prefix followed by a word slug.
    // Date-prefixed IDs such as 2026-10-24-task must retain their full name.
    let matchResult = Regex.Match(stem, "^([0-9]+)-(?=[A-Za-z])")
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
    let mutable nextActor = None
    let mutable lastUpdatedBy = None
    let mutable assignmentRevision = None
    let mutable updatedAt = None
    let mutable checkpointSummary = None
    let mutable checkpointNextAction = None
    let mutable checkpointLastCommit = None
    let mutable checkpointBlocker = None
    let mutable completionFinalCommit = None
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
                    status <-
                        getString obj "status"
                        |> Option.defaultValue "open"
                        |> normalizeStatus
                    owner <-
                        match obj["owner"] with
                        | :? JsonObject as ownerObj -> getString ownerObj "agent"
                        | _ -> None
                    nextActor <- getString obj "nextActor"
                    lastUpdatedBy <-
                        match obj["lastUpdatedBy"] with
                        | :? JsonObject as actor ->
                            match getString actor "role", getString actor "id" with
                            | Some role, Some id -> Some(role + ":" + id)
                            | _ -> None
                        | _ -> None
                    assignmentRevision <-
                        match obj["assignment"] with
                        | :? JsonObject as assignment -> getString assignment "revision"
                        | _ -> None
                    updatedAt <-
                        getString obj "updatedAt"
                        |> Option.bind (fun value ->
                            match DateTimeOffset.TryParse value with
                            | true, parsed -> Some parsed
                            | _ -> None)
                    match obj["checkpoint"] with
                    | :? JsonObject as checkpoint ->
                        checkpointSummary <- getString checkpoint "summary"
                        checkpointNextAction <- getString checkpoint "nextAction"
                        checkpointLastCommit <- getString checkpoint "lastCommit"
                        checkpointBlocker <- getString checkpoint "blocker"
                    | _ -> ()
                    match obj["completion"] with
                    | :? JsonObject as completion ->
                        completionFinalCommit <- getString completion "finalCommit"
                    | _ -> ()
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
      Diagnostics = List.rev diagnostics
      NextActor = nextActor
      LastUpdatedBy = lastUpdatedBy
      AssignmentRevision = assignmentRevision
      UpdatedAt = updatedAt
      CheckpointSummary = checkpointSummary
      CheckpointNextAction = checkpointNextAction
      CheckpointLastCommit = checkpointLastCommit
      CheckpointBlocker = checkpointBlocker
      CompletionFinalCommit = completionFinalCommit }

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
                          HasState = false; Owner = None; Dependencies = []; Revision = None; Diagnostics = [ "Invalid task identifier." ]
                          NextActor = None; LastUpdatedBy = None; AssignmentRevision = None
                          UpdatedAt = None; CheckpointSummary = None; CheckpointNextAction = None
                          CheckpointLastCommit = None; CheckpointBlocker = None; CompletionFinalCommit = None })
                |> List.filter (fun item ->
                    (cmd.Status.IsNone || (cmd.Status |> Option.map normalizeStatus) = Some item.Status)
                    && (cmd.Owner.IsNone || item.Owner = cmd.Owner)
                    && (cmd.DependsOn.IsNone || item.Dependencies |> List.contains cmd.DependsOn.Value)
                    && (cmd.NextActor.IsNone || (item.Status <> "completed" && item.NextActor = cmd.NextActor)))
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

let private ensureGitReady projectName root =
    effect {
        let! result =
            fun rt ->
                match git projectName [ "rev-parse"; "--show-toplevel" ] rt with
                | Error(Client.ExceptionError (:? System.ComponentModel.Win32Exception as ex)) when ex.NativeErrorCode = 2 ->
                    Error(Client.ValidationError "Git is not installed or is not available in PATH. Install Git before modifying repository tasks.")
                | Error(Client.ExceptionError (:? FileNotFoundException)) ->
                    Error(Client.ValidationError "Git is not installed or is not available in PATH. Install Git before modifying repository tasks.")
                | other -> other
        if result.TimedOut || result.Truncated then
            return! Client.ValidationError "Could not verify the Git repository (command timed out or output was truncated)." |> Effect.ofError
        elif result.ExitCode <> 0 then
            let error = result.StdErr.Trim()
            if error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
               || error.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase) then
                return! Client.ValidationError "This project is not a Git working repository. Initialize it with git init before modifying tasks." |> Effect.ofError
            else
                return! Client.ValidationError($"Git cannot access this project's repository: {error}") |> Effect.ofError
        elif not (String.Equals(Path.GetFullPath(result.StdOut.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                                 (if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal))) then
            return! Client.ValidationError "The selected project is not the root of its Git working tree." |> Effect.ofError
        else return ()
    }

let private restoreBytes path (original: byte[] option) =
    match original with
    | None -> if File.Exists path then File.Delete path
    | Some bytes ->
        let temp = path + "." + Guid.NewGuid().ToString("N") + ".rollback"
        try
            File.WriteAllBytes(temp, bytes)
            File.Move(temp, path, true)
        finally
            if File.Exists temp then File.Delete temp

// The saved bytes are captured before writing. On a failed commit, restore only if
// the files still contain precisely our writes; never overwrite external edits.
let private commitWithRollback projectName (paths: string list) message (snapshots: (string * byte[] option) list) =
    fun rt ->
        let head args =
            match git projectName args rt with
            | Ok result when result.ExitCode = 0 && not result.TimedOut && not result.Truncated ->
                Some(result.StdOut.Trim())
            | _ -> None
        let originalHead = head [ "rev-parse"; "--verify"; "HEAD" ]
        let expected =
            snapshots
            |> List.map (fun (path, original) -> path, original, File.ReadAllBytes(path))
        match commitScope projectName paths message rt with
        | Ok committed -> Ok committed
        | Error commitError ->
            let currentHead = head [ "rev-parse"; "--verify"; "HEAD" ]
            let filesIntact =
                expected
                |> List.forall (fun (path, _, written) ->
                    File.Exists path && File.ReadAllBytes(path) = written)
            let failure = $"Git could not commit task changes: {commitError}."
            if originalHead <> currentHead then
                Error(Client.ValidationError($"{failure} Git HEAD changed; the task files were preserved. Inspect the commit before retrying."))
            elif not filesIntact then
                Error(Client.ValidationError($"{failure} Task files changed during the attempt; no rollback was performed. Reconcile the task files and Git index before retrying."))
            else
                let unstaged =
                    let args =
                        if originalHead.IsSome then [ "reset"; "-q"; "--" ] @ paths
                        else [ "rm"; "--cached"; "-q"; "--ignore-unmatch"; "--" ] @ paths
                    match git projectName args rt with
                    | Ok result -> result.ExitCode = 0 && not result.TimedOut && not result.Truncated
                    | Error _ -> false
                if not unstaged then
                    Error(Client.ValidationError($"{failure} Could not restore the task staging area; task files were preserved. Reconcile the Git index before retrying."))
                else
                    try
                        for path, original, _ in expected do
                            restoreBytes path original
                        Error(Client.ValidationError($"{failure} Task files and staging were restored to their previous state. Fix Git and retry the operation."))
                    with ex ->
                        Error(Client.ValidationError($"{failure} Rollback failed: {ex.Message}. Inspect the task files and Git index before retrying."))

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
        do! ensureGitReady projectName root
        let path = relative + "/" + taskId + ".json"
        do! ensureUnmodifiedTaskPath projectName path
        let previousState = if File.Exists statePath then Some(File.ReadAllBytes statePath) else None
        let! () = fun _ -> mutate obj
        obj["updatedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
        let! () =
            fun _ ->
                try
                    atomicWrite statePath (serialize obj)
                    Ok()
                with ex -> Error(Client.ExceptionError ex)
        let! commit = commitWithRollback projectName [ path ] message [ statePath, previousState ]
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
            do! ensureGitReady cmd.ProjectName root
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
                let snapshots =
                    [ html, (if hasExistingHtml then Some(File.ReadAllBytes html) else None)
                      json, None ]
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
                let! commit = commitWithRollback cmd.ProjectName paths $"Create task {id}: {cmd.Title}" snapshots
                let! details = getTask { ProjectName = cmd.ProjectName; TaskId = id }
                return { Task = details; CommitHash = commit.CommitHash; ChangedPaths = paths }
    } |> serializeMutations cmd.ProjectName

let private validStatus status =
    List.contains status [ "open"; "active"; "blocked"; "completed" ]

let private validNextActor value =
    List.contains value [ "agent"; "reviewer" ]

let private assignmentRevision (obj: JsonObject) =
    match obj["assignment"] with
    | :? JsonObject as assignment -> getString assignment "revision"
    | _ -> None

let private ownerId (obj: JsonObject) =
    match obj["owner"] with
    | :? JsonObject as owner -> getString owner "agent"
    | _ -> None

let private recordActor (obj: JsonObject) (role: string) (id: string) =
    let actor = JsonObject()
    actor["role"] <- JsonValue.Create(role)
    actor["id"] <- JsonValue.Create(id)
    obj["lastUpdatedBy"] <- actor

let private acknowledgeAssignment (obj: JsonObject) suppliedRevision =
    match assignmentRevision obj, suppliedRevision with
    | None, None -> Ok()
    | Some current, Some acknowledged when current = acknowledged -> Ok()
    | None, Some _ -> Error(Client.ValidationError "Task has no assignment to acknowledge.")
    | Some _, _ -> Error(Client.ValidationError "Assignment revision is stale or missing; reload the current assignment before checkpointing.")

let updateTask (cmd: UpdateTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Update task {cmd.TaskId}"
        (fun obj ->
            let changesAssignment = cmd.AssignmentInstructions.IsSome || cmd.AssignmentBaseCommit.IsSome
            match cmd.Status with
            | Some status when not (validStatus status) -> Error(Client.ValidationError "Unknown task status.")
            | _ when cmd.Dependencies |> Option.exists (List.exists (validId >> not)) ->
                Error(Client.ValidationError "Invalid dependency ID.")
            | _ when cmd.NextActor |> Option.exists (validNextActor >> not) ->
                Error(Client.ValidationError "Unknown next actor.")
            | _ when (changesAssignment || cmd.NextActor.IsSome)
                     && (cmd.ActorId |> Option.forall String.IsNullOrWhiteSpace) ->
                Error(Client.ValidationError "Reviewer ID is required to update an assignment or handoff.")
            | _ when changesAssignment && (cmd.AssignmentInstructions |> Option.exists String.IsNullOrWhiteSpace) ->
                Error(Client.ValidationError "Assignment instructions cannot be blank.")
            | _ when cmd.ExpectedAssignmentRevision.IsSome
                     && cmd.ExpectedAssignmentRevision <> assignmentRevision obj ->
                Error(Client.ValidationError "Assignment revision is stale.")
            | _ ->
                let currentAssignment =
                    match obj["assignment"] with
                    | :? JsonObject as existing -> Some existing
                    | _ -> None
                if changesAssignment && currentAssignment.IsNone && cmd.AssignmentInstructions.IsNone then
                    Error(Client.ValidationError "Instructions are required for the first assignment.")
                else
                    cmd.Status |> Option.iter (fun status -> obj["status"] <- JsonValue.Create(status))
                    cmd.Title |> Option.iter (fun title -> obj["title"] <- JsonValue.Create(title))
                    cmd.Dependencies |> Option.iter (fun deps -> obj["dependencies"] <- System.Text.Json.JsonSerializer.SerializeToNode(deps))
                    cmd.DescriptionCommit |> Option.iter (fun commit -> obj["descriptionCommit"] <- JsonValue.Create(commit))
                    if changesAssignment then
                        let assignment =
                            currentAssignment |> Option.defaultWith (fun () -> JsonObject())
                        cmd.AssignmentInstructions
                        |> Option.iter (fun instructions -> assignment["instructions"] <- JsonValue.Create(instructions))
                        cmd.AssignmentBaseCommit
                        |> Option.iter (fun commit -> assignment["baseCommit"] <- JsonValue.Create(commit))
                        assignment["revision"] <- JsonValue.Create(Guid.NewGuid().ToString("N"))
                        assignment["updatedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                        obj["assignment"] <- assignment
                        obj["nextActor"] <- JsonValue.Create(cmd.NextActor |> Option.defaultValue "agent")
                        recordActor obj "reviewer" cmd.ActorId.Value
                    else
                        cmd.NextActor |> Option.iter (fun actor -> obj["nextActor"] <- JsonValue.Create(actor))
                        cmd.ActorId |> Option.iter (fun id -> recordActor obj "reviewer" id)
                    if getString obj "status" = Some "completed" then obj["nextActor"] <- null
                    Ok())


let private projectAssignmentLocks = ConcurrentDictionary<string, obj>(StringComparer.Ordinal)

let private globalAssignmentMutation (projectName: string) (operation: IO<'rt, 'a>) : IO<'rt, 'a> =
    fun rt ->
        let root =
            let separator = projectName.IndexOf("::", StringComparison.Ordinal)
            if separator < 0 then projectName else projectName.Substring(0, separator)
        let gate = projectAssignmentLocks.GetOrAdd(root, fun _ -> obj())
        lock gate (fun () -> operation rt)

// A historical owner on a parked task does not represent an outstanding handoff.
let private actionable (task: TaskSummary) =
    task.Status <> "completed"
    && (task.NextActor = Some "agent" || task.NextActor = Some "reviewer")
    && task.Owner.IsSome

let private ensureGlobalClaimAvailable (selector: string) (taskId: string) (agent: string) =
    effect {
        let rootName =
            let separator = selector.IndexOf("::", StringComparison.Ordinal)
            if separator < 0 then selector else selector.Substring(0, separator)
        let! (FolderPath selectedRoot) = ProjectPaths.resolveProjectRoot selector
        let! worktrees = Worktrees.listWorktrees { ProjectName = rootName }
        let mutable conflicts = []
        let mutable failures = []
        let! listed =
            fun rt ->
                worktrees
                |> List.map (fun worktree ->
                    let worktreeSelector =
                        if worktree.IsMain then rootName
                        else rootName + "::" + Path.GetFileName(worktree.Path)
                    worktree, listTasks
                        { ProjectName = worktreeSelector
                          Status = None; Owner = None; DependsOn = None; NextActor = None } rt)
                |> Ok
        let! () =
            fun _ ->
                for worktree, result in listed do
                    let worktreeSelector =
                        if worktree.IsMain then rootName
                        else rootName + "::" + Path.GetFileName(worktree.Path)
                    match result with
                    | Error error -> failures <- (worktree.Path + ": " + string error) :: failures
                    | Ok tasks ->
                        for task in tasks do
                            let sameTarget =
                                String.Equals(Path.GetFullPath selectedRoot, Path.GetFullPath worktree.Path,
                                              (if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal))
                            let sameTask = sameTarget && task.Id = taskId
                            if sameTask && not task.Diagnostics.IsEmpty then
                                failures <- (worktree.Path + "/" + task.Id + ": " + String.concat "; " task.Diagnostics) :: failures
                            elif not sameTask && actionable task &&
                                 (task.Owner = Some agent || task.Id = taskId) then
                                conflicts <- (worktreeSelector, task) :: conflicts
                Ok()
        if not failures.IsEmpty then
            let details =
                failures |> List.truncate 8 |> String.concat "; "
                |> fun summary -> if failures.Length > 8 then summary + $" (+{failures.Length - 8} more)" else summary
            return! Client.ValidationError("Cannot validate selected task or registered worktree: " + details)
                    |> Effect.ofError
        elif not conflicts.IsEmpty then
            let details =
                conflicts
                |> List.truncate 8
                |> List.map (fun (worktreeSelector, task) ->
                    worktreeSelector + "/" + task.Id + " claimed by " + (task.Owner |> Option.defaultValue "(unknown)"))
                |> String.concat "; "
                |> fun summary -> if conflicts.Length > 8 then summary + $" (+{conflicts.Length - 8} more)" else summary
            return! Client.ValidationError("Conflicting active global task ownership: " + details)
                    |> Effect.ofError
        else return ()
    }

let claimTask (cmd: ClaimTaskCommand) =
    effect {
        do! ensureGlobalClaimAvailable cmd.ProjectName cmd.TaskId cmd.Owner
        return!
            updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Claim task {cmd.TaskId}"
                (fun obj ->
                    if String.IsNullOrWhiteSpace cmd.Owner then
                        Error(Client.ValidationError "Owner is required.")
                    else
                        let existing = ownerId obj
                        match existing with
                        | Some other when other <> cmd.Owner && not cmd.AllowReassign ->
                            Error(Client.ValidationError "Task is claimed by another agent.")
                        | _ ->
                            let owner = JsonObject()
                            owner["agent"] <- JsonValue.Create(cmd.Owner)
                            owner["worktree"] <- JsonValue.Create(cmd.ProjectName)
                            obj["owner"] <- owner
                            obj["status"] <- JsonValue.Create("active")
                            obj["nextActor"] <- JsonValue.Create("agent")
                            recordActor obj "agent" cmd.Owner
                            Ok())

    } |> globalAssignmentMutation cmd.ProjectName
let checkpointTask (cmd: CheckpointTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Checkpoint task {cmd.TaskId}"
        (fun obj ->
            if not (validStatus cmd.Status) then
                Error(Client.ValidationError "Unknown task status.")
            else
                match acknowledgeAssignment obj cmd.AcknowledgedAssignmentRevision with
                | Error error -> Error error
                | Ok () ->
                    let checkpoint = JsonObject()
                    checkpoint["summary"] <- JsonValue.Create(cmd.Summary)
                    checkpoint["nextAction"] <- JsonValue.Create(cmd.NextAction)
                    checkpoint["verified"] <- System.Text.Json.JsonSerializer.SerializeToNode(cmd.Verified)
                    checkpoint["blocker"] <- cmd.Blocker |> Option.map JsonValue.Create |> Option.defaultValue null
                    checkpoint["lastCommit"] <- cmd.LastCommit |> Option.map JsonValue.Create |> Option.defaultValue null
                    cmd.AcknowledgedAssignmentRevision
                    |> Option.iter (fun rev -> checkpoint["acknowledgedAssignmentRevision"] <- JsonValue.Create(rev))
                    obj["checkpoint"] <- checkpoint
                    cmd.DescriptionCommit |> Option.iter (fun commit -> obj["descriptionCommit"] <- JsonValue.Create(commit))
                    obj["status"] <- JsonValue.Create(cmd.Status)
                    obj["nextActor"] <- (if cmd.Status = "completed" then null else JsonValue.Create("reviewer"))
                    recordActor obj "agent" (cmd.ActorId |> Option.orElseWith (fun () -> ownerId obj) |> Option.defaultValue "unspecified")
                    Ok())

let completeTask (cmd: CompleteTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Complete task {cmd.TaskId}"
        (fun obj ->
            match acknowledgeAssignment obj cmd.AcknowledgedAssignmentRevision with
            | Error error -> Error error
            | Ok () ->
                let completion = JsonObject()
                completion["verified"] <- System.Text.Json.JsonSerializer.SerializeToNode(cmd.Verified)
                completion["finalCommit"] <- cmd.FinalCommit |> Option.map JsonValue.Create |> Option.defaultValue null
                completion["completedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                cmd.AcknowledgedAssignmentRevision
                |> Option.iter (fun rev -> completion["acknowledgedAssignmentRevision"] <- JsonValue.Create(rev))
                obj["completion"] <- completion
                obj["status"] <- JsonValue.Create("completed")
                obj["nextActor"] <- null
                recordActor obj "agent" (cmd.ActorId |> Option.orElseWith (fun () -> ownerId obj) |> Option.defaultValue "unspecified")
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
                      DependsOn = None
                      NextActor = cmd.NextActor } rt with
                | Ok tasks -> { Worktree = worktree; Tasks = tasks; Error = None }
                | Error error -> { Worktree = worktree; Tasks = []; Error = Some(string error) })
            |> Ok

type AgentAssignment =
    { Agent: string
      ProjectName: string
      Worktree: Worktrees.WorktreeInfo
      Task: TaskSummary
      AssignmentInstructions: string option
      AssignmentBaseCommit: string option
      AssignmentRevision: string option
      AcknowledgedAssignmentRevision: string option
      Phase: string }

let private rootProjectName (projectName: string) =
    let separator = projectName.IndexOf("::", StringComparison.Ordinal)
    if separator < 0 then projectName else projectName.Substring(0, separator)

let private projectSelector projectName (worktree: Worktrees.WorktreeInfo) =
    if worktree.IsMain then projectName
    else projectName + "::" + Path.GetFileName(worktree.Path)

let private assignmentForTask agent projectName worktree (task: TaskSummary) =
    let selector = projectSelector projectName worktree
    effect {
        let! details = getTask { ProjectName = selector; TaskId = task.Id }
        let state =
            details.StateJson
            |> Option.map JsonNode.Parse
        let nested (field: string) (key: string) =
            match state with
            | Some (:? JsonObject as obj) ->
                match obj[field] with
                | :? JsonObject as child -> getString child key
                | _ -> None
            | _ -> None
        let revision = nested "assignment" "revision"
        let acknowledged =
            let receipt = nested "acknowledgement" "revision"
            let checkpointReceipt = nested "checkpoint" "acknowledgedAssignmentRevision"
            if revision.IsNone then None
            elif receipt = revision then receipt
            elif checkpointReceipt = revision then checkpointReceipt
            else None
        let phase =
            if task.NextActor = Some "reviewer" then "ReadyForReview"
            elif revision.IsSome && acknowledged = revision
                 && (nested "acknowledgement" "startedAt").IsSome then "Working"
            elif revision.IsSome && acknowledged = revision then "Acknowledged"
            elif revision.IsSome then "Assigned"
            else "Claimed"
        return
            { Agent = agent
              ProjectName = selector
              Worktree = worktree
              Task = task
              AssignmentInstructions = nested "assignment" "instructions"
              AssignmentBaseCommit = nested "assignment" "baseCommit"
              AssignmentRevision = revision
              AcknowledgedAssignmentRevision = acknowledged
              Phase = phase }
    }

let private activeAssignments projectName agent =
    effect {
        let! groups = compareWorktreeTasks { ProjectName = projectName; NextActor = None }
        let failures = groups |> List.choose (fun group -> group.Error |> Option.map (fun error -> group.Worktree.Path + ": " + error))
        if not failures.IsEmpty then
            return! Client.ValidationError("Cannot trust global agent ownership while worktree inspection fails: " + String.concat "; " failures)
                    |> Effect.ofError
        else
            return
                groups
                |> List.collect (fun group ->
                    group.Tasks
                    |> List.filter (fun task -> task.Owner = Some agent && actionable task)
                    |> List.map (fun task -> group.Worktree, task))
    }

let getAgentAssignment (cmd: GetAgentAssignmentCommand) =
    effect {
        if String.IsNullOrWhiteSpace cmd.Agent then
            return! Client.ValidationError "Agent identity is required." |> Effect.ofError
        else
            let projectName = rootProjectName cmd.ProjectName
            let! candidates = activeAssignments projectName cmd.Agent
            match candidates with
            | [] ->
                return! Client.NotFoundError($"No active assignment found for agent '{cmd.Agent}' in project '{projectName}'.")
                        |> Effect.ofError
            | [ (worktree, task) ] ->
                return! assignmentForTask cmd.Agent projectName worktree task
            | many ->
                let collisions =
                    many
                    |> List.map (fun (worktree, task) ->
                        let branch = worktree.Branch |> Option.defaultValue "detached"
                        $"{task.Id} [{branch}] {worktree.Path}")
                let conflictDetails =
                    collisions |> List.truncate 8 |> String.concat "; "
                    |> fun summary -> if collisions.Length > 8 then summary + $" (+{collisions.Length - 8} more)" else summary
                return! Client.ValidationError(
                    $"Conflicting active assignments for agent '{cmd.Agent}': {conflictDetails}. Resolve duplicate ownership before continuing.")
                    |> Effect.ofError
    }

let assignTask (cmd: AssignTaskCommand) =
    let mutable createdWorktree = None
    let operation = effect {
        if String.IsNullOrWhiteSpace cmd.Agent
           || String.IsNullOrWhiteSpace cmd.ActorId
           || String.IsNullOrWhiteSpace cmd.Instructions
           || String.IsNullOrWhiteSpace cmd.TaskId then
            return! Client.ValidationError "Agent, reviewer ID, task ID and instructions are required." |> Effect.ofError
        else
            let projectName = rootProjectName cmd.ProjectName
            let! groups = compareWorktreeTasks { ProjectName = projectName; NextActor = None }
            let failures = groups |> List.choose (fun group -> group.Error |> Option.map (fun error -> group.Worktree.Path + ": " + error))
            if not failures.IsEmpty then
                let details = String.concat "; " failures
                return! Client.ValidationError("Cannot assign while registered worktree inspection fails: " + details) |> Effect.ofError
            else
                let matches =
                    groups
                    |> List.filter (fun group -> group.Tasks |> List.exists (fun task -> task.Id = cmd.TaskId))
                let existing =
                    match cmd.WorktreeName with
                    | Some name -> groups |> List.tryFind (fun group -> Path.GetFileName(group.Worktree.Path) = name)
                    | None when matches.Length = 1 -> Some matches.Head
                    | _ -> None
                let! selector =
                    match existing, cmd.WorktreeName, cmd.Branch with
                    | Some group, _, _ ->
                        fun _ -> Ok(projectSelector projectName group.Worktree)
                    | None, Some name, Some branch ->
                        effect {
                            let! original = getTask { ProjectName = projectName; TaskId = cmd.TaskId }
                            let baseRef = cmd.BaseCommit |> Option.defaultValue "HEAD"
                            let! taskAtBase = git projectName [ "cat-file"; "-e"; baseRef + ":" + original.Task.DescriptionPath ]
                            if taskAtBase.ExitCode <> 0 || taskAtBase.TimedOut then
                                return! Client.ValidationError "Task HTML is absent from requested worktree base commit." |> Effect.ofError
                            else
                                let! created =
                                    Worktrees.createWorktree
                                        { ProjectName = projectName; Name = name; Branch = branch
                                          BaseRef = baseRef }
                                return (createdWorktree <- Some created.Path; projectSelector projectName created)
                        }
                    | None, Some _, None ->
                        Client.ValidationError "Unknown worktree. Supply branch to create it, or use a registered worktree name." |> Effect.ofError
                    | None, None, _ when matches.Length > 1 ->
                        Client.ValidationError "Task exists in multiple worktrees. Supply worktreeName to select the intended checkout." |> Effect.ofError
                    | _ ->
                        Client.NotFoundError "No registered worktree contains this task. Create its HTML description before assigning." |> Effect.ofError
                do! ensureGlobalClaimAvailable selector cmd.TaskId cmd.Agent
                let! current = getTask { ProjectName = selector; TaskId = cmd.TaskId }
                if current.Task.Status = "completed" then
                    return! Client.ValidationError "Cannot assign a completed task; reopen it explicitly before assignment." |> Effect.ofError
                elif current.Task.Owner.IsSome && current.Task.Owner <> Some cmd.Agent then
                    return! Client.ValidationError "Task is claimed by another agent; explicit reassignment is required." |> Effect.ofError
                else
                    let expected =
                        cmd.ExpectedRevision |> Option.orElse current.Task.Revision
                    return!
                        updateState selector cmd.TaskId expected $"Assign task {cmd.TaskId} to {cmd.Agent}"
                            (fun obj ->
                                if cmd.ExpectedAssignmentRevision.IsSome
                                   && cmd.ExpectedAssignmentRevision <> assignmentRevision obj then
                                    Error(Client.ValidationError "Assignment revision is stale.")
                                else
                                    let owner = JsonObject()
                                    owner["agent"] <- JsonValue.Create(cmd.Agent)
                                    owner["worktree"] <- JsonValue.Create(selector)
                                    obj["owner"] <- owner
                                    let assignment =
                                        match obj["assignment"] with
                                        | :? JsonObject as old -> old
                                        | _ -> JsonObject()
                                    assignment["instructions"] <- JsonValue.Create(cmd.Instructions)
                                    cmd.BaseCommit
                                    |> Option.iter (fun baseCommit -> assignment["baseCommit"] <- JsonValue.Create(baseCommit))
                                    assignment["revision"] <- JsonValue.Create(Guid.NewGuid().ToString("N"))
                                    assignment["updatedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                                    obj["assignment"] <- assignment
                                    obj["acknowledgement"] <- null
                                    obj["status"] <- JsonValue.Create("active")
                                    obj["nextActor"] <- JsonValue.Create("agent")
                                    recordActor obj "reviewer" cmd.ActorId
                                    Ok())
    }
    (fun rt ->
        match operation rt with
        | Ok outcome -> Ok outcome
        | Error original ->
            match createdWorktree with
            | None -> Error original
            | Some path ->
                let root = rootProjectName cmd.ProjectName
                match Worktrees.removeWorktree
                    { ProjectName = root; WorktreePath = path; TargetRef = None } rt with
                | Ok _ -> Error original
                | Error cleanup ->
                    Error(Client.ValidationError(
                        $"Assignment failed: {original}. The new worktree {path} could not be safely removed: {cleanup}. Inspect it before retrying.")))
    |> globalAssignmentMutation cmd.ProjectName

let acknowledgeTask (cmd: AcknowledgeTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Acknowledge task {cmd.TaskId}"
        (fun obj ->
            if String.IsNullOrWhiteSpace cmd.Agent
               || String.IsNullOrWhiteSpace cmd.AssignmentRevision then
                Error(Client.ValidationError "Agent identity and assignment revision are required.")
            elif ownerId obj <> Some cmd.Agent then
                Error(Client.ValidationError "Only the claimed agent may acknowledge this task.")
            elif assignmentRevision obj <> Some cmd.AssignmentRevision then
                Error(Client.ValidationError "Assignment revision is stale or missing.")
            elif getString obj "status" = Some "completed" then
                Error(Client.ValidationError "Cannot acknowledge a completed task.")
            else
                let receipt =
                    match obj["acknowledgement"] with
                    | :? JsonObject as existing
                        when getString existing "revision" = Some cmd.AssignmentRevision
                             && getString existing "agent" = Some cmd.Agent -> existing
                    | _ ->
                        let created = JsonObject()
                        created["revision"] <- JsonValue.Create(cmd.AssignmentRevision)
                        created["agent"] <- JsonValue.Create(cmd.Agent)
                        created["acknowledgedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                        created
                obj["acknowledgement"] <- receipt
                recordActor obj "agent" cmd.Agent
                Ok())

type WorktreeRetirement =
    { Worktree: Worktrees.WorktreeInfo
      TargetRef: string
      FullyMerged: bool
      SourceEquivalent: bool
      TaskOnlyCommitsPreservedOnBranch: string list
      HasActiveWork: bool
      HasIgnoredOrUntrackedArtifacts: bool
      Reasons: string list
      CanRetire: bool
      DryRun: bool
      Retired: bool
      BranchDeleted: bool }

let retireWorktree (cmd: RetireWorktreeCommand) =
    effect {
        let rootName = rootProjectName cmd.ProjectName
        let! worktree =
            Worktrees.getWorktree
                { ProjectName = rootName; WorktreePath = cmd.WorktreePath }
        let! entries = Worktrees.listWorktrees { ProjectName = rootName }
        let main = entries |> List.find (fun item -> item.IsMain)
        let target = cmd.TargetRef |> Option.defaultValue (worktree.Branch |> Option.defaultValue "main")
        let target =
            if cmd.TargetRef.IsSome then target
            else main.Branch |> Option.defaultValue "main"
        let selector = projectSelector rootName worktree
        let! taskList =
            listTasks
                { ProjectName = selector; Status = None; Owner = None
                  DependsOn = None; NextActor = None }
        let! workStatus =
            git rootName [ "-C"; worktree.Path; "status"; "--porcelain"; "--untracked-files=all" ]
        let! ignored =
            git rootName [ "-C"; worktree.Path; "ls-files"; "--others"; "--ignored"; "--exclude-standard" ]
        let! fullyMergedResult =
            git rootName [ "merge-base"; "--is-ancestor"; worktree.Head; target ]
        let fullyMerged = fullyMergedResult.ExitCode = 0 && not fullyMergedResult.TimedOut
        let! cherry =
            git rootName [ "-C"; worktree.Path; "cherry"; "-v"; target; "HEAD" ]
        let! mergeCommits =
            git rootName [ "-C"; worktree.Path; "rev-list"; "--merges"; target + "..HEAD" ]
        let! (FolderPath selectedRoot) = ProjectPaths.resolveProjectRoot selector
        let! (taskDir, _) = fun _ -> directory selectedRoot
        let! (taskOnlyCommits, changedSource) =
            fun rt ->
                if cherry.ExitCode <> 0 || cherry.TimedOut || cherry.Truncated then
                    Error(Client.ValidationError("Cannot establish patch equivalence: " + cherry.StdErr))
                elif mergeCommits.ExitCode <> 0 || mergeCommits.TimedOut || mergeCommits.Truncated then
                    Error(Client.ValidationError("Cannot inspect merge commits: " + mergeCommits.StdErr))
                else
                    let mutable metadata = []
                    let mutable source = []
                    for line in cherry.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
                        if line.StartsWith("+ ", StringComparison.Ordinal) then
                            let sha = line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                            match git rootName [ "-C"; worktree.Path; "show"; "--format="; "--name-only"; sha ] rt with
                            | Ok details when details.ExitCode = 0 && not details.TimedOut && not details.Truncated ->
                                let paths = details.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                if paths.Length > 0 && paths |> Array.forall (fun path ->
                                    path.StartsWith(taskDir + "/", StringComparison.Ordinal)) then
                                    metadata <- sha :: metadata
                                else source <- sha :: source
                            | _ -> source <- sha :: source
                    Ok(List.rev metadata, List.rev source)
        let! activeJobs = JobManager.listJobs { ProjectName = None; IncludeCompleted = false }
        let jobs =
            activeJobs.Jobs
            |> List.filter (fun job -> job.ProjectName = selector || job.ProjectName = rootName)
        let hasActiveWork =
            taskList
            |> List.exists (fun task -> task.Status <> "completed" &&
                                          (task.Owner.IsSome || task.NextActor.IsSome))
        let hasArtifacts =
            workStatus.ExitCode <> 0 || ignored.ExitCode <> 0 ||
            workStatus.TimedOut || ignored.TimedOut ||
            workStatus.Truncated || ignored.Truncated ||
            not (String.IsNullOrWhiteSpace workStatus.StdOut) ||
            not (String.IsNullOrWhiteSpace ignored.StdOut)
        let sourceEquivalent =
            fullyMerged ||
            (changedSource.IsEmpty &&
             String.IsNullOrWhiteSpace mergeCommits.StdOut &&
             not cherry.TimedOut && not cherry.Truncated)
        let reasons =
            [ if worktree.IsMain then "Cannot retire main worktree."
              if worktree.IsLocked || worktree.IsPrunable then "Worktree is locked or prunable."
              if hasArtifacts then "Worktree contains untracked, modified, staged or ignored files. Inspect and preserve them."
              if hasActiveWork then "Worktree contains active task ownership or a pending handoff."
              if not jobs.IsEmpty then "Jarvis still has running project jobs."
              if taskList |> List.exists (fun task -> not task.Diagnostics.IsEmpty) then
                  "Task records contain diagnostics and cannot be trusted for retirement."
              if not sourceEquivalent then "Source commits are not verified as merged or patch-equivalent to target."
              if cmd.DeleteBranch && not fullyMerged then
                  "Cannot delete a branch with non-ancestor task history; retain the branch as evidence." ]
        let canRetire = reasons.IsEmpty
        if cmd.DryRun || not canRetire then
            return
                { Worktree = worktree; TargetRef = target; FullyMerged = fullyMerged
                  SourceEquivalent = sourceEquivalent
                  TaskOnlyCommitsPreservedOnBranch = taskOnlyCommits
                  HasActiveWork = hasActiveWork
                  HasIgnoredOrUntrackedArtifacts = hasArtifacts
                  Reasons = reasons; CanRetire = canRetire; DryRun = cmd.DryRun
                  Retired = false; BranchDeleted = false }
        else
            let! removal = git rootName [ "worktree"; "remove"; worktree.Path ]
            if removal.ExitCode <> 0 || removal.TimedOut then
                return! Client.ValidationError("Git refused worktree removal: " + removal.StdErr)
                        |> Effect.ofError
            else
                let! deleted =
                    if cmd.DeleteBranch then
                        effect {
                            match worktree.Branch with
                            | None ->
                                return! Client.ValidationError "Detached worktree has no branch to delete."
                                        |> Effect.ofError
                            | Some branch ->
                                let! result = git rootName [ "branch"; "-d"; branch ]
                                if result.ExitCode <> 0 then
                                    return! Client.ValidationError("Checkout retired but branch deletion failed: " + result.StdErr)
                                            |> Effect.ofError
                                else return true
                        }
                    else fun _ -> Ok false
                return
                    { Worktree = worktree; TargetRef = target; FullyMerged = fullyMerged
                      SourceEquivalent = sourceEquivalent
                      TaskOnlyCommitsPreservedOnBranch = taskOnlyCommits
                      HasActiveWork = false; HasIgnoredOrUntrackedArtifacts = false
                      Reasons = []; CanRetire = true; DryRun = false
                      Retired = true; BranchDeleted = deleted }
    } |> globalAssignmentMutation cmd.ProjectName

type IntegratedTask =
    { Worktree: Worktrees.WorktreeInfo
      Task: TaskSummary
      Phase: string }

type OrchestrationSnapshot =
    { ProjectName: string
      Worktrees: WorktreeTaskGroup list
      Assignments: AgentAssignment list
      Conflicts: string list
      ReviewerTaskCount: int
      UnacknowledgedTaskCount: int
      InspectionErrors: string list
      RetirementInspections: WorktreeRetirement list
      RetirementCandidates: string list
      IntegratedTasks: IntegratedTask list
      TotalMatches: int
      Offset: int
      Limit: int
      NextOffset: int option
      TaskDiagnosticCount: int
      TaskDiagnostics: string list
      RetirementInspected: bool }

let private truncateSnapshotField limit (value: string) =
    if value.Length <= limit then value
    else value.Substring(0, limit - 3) + "..."

let private compactSnapshotTask (task: TaskSummary) =
    let trim = Option.map (truncateSnapshotField 200)
    { task with
        Title = truncateSnapshotField 180 task.Title
        DescriptionPath = truncateSnapshotField 260 task.DescriptionPath
        StatePath = truncateSnapshotField 260 task.StatePath
        CheckpointSummary = trim task.CheckpointSummary
        CheckpointNextAction = trim task.CheckpointNextAction
        CheckpointBlocker = trim task.CheckpointBlocker
        Diagnostics = task.Diagnostics |> List.truncate 2 |> List.map (truncateSnapshotField 180) }

let getOrchestrationSnapshot (cmd: GetOrchestrationSnapshotCommand) =
    effect {
        if cmd.Offset < 0 || cmd.Limit < 1 || cmd.Limit > 100 then
            return! Client.ValidationError "Snapshot offset must be nonnegative and limit must be between 1 and 100." |> Effect.ofError
        else
            let projectName = rootProjectName cmd.ProjectName
            let! groups = compareWorktreeTasks { ProjectName = projectName; NextActor = None }
            let allTasks =
                groups |> List.collect (fun group ->
                    group.Tasks |> List.map (fun task -> group.Worktree, task))
            let actionableTasks =
                allTasks |> List.filter (fun (_, task) -> actionable task)
            // Historical owners and parked tasks are never live assignment conflicts.
            let owners =
                actionableTasks
                |> List.groupBy (fun (_, task) -> task.Owner.Value)
                |> List.choose (fun (owner, items) ->
                    if items.Length < 2 then None
                    else
                        let examples =
                            items |> List.truncate 4
                            |> List.map (fun (wt, task) -> wt.Path + "/" + task.Id)
                            |> String.concat ", "
                        Some("Agent " + owner + " has " + string items.Length +
                             " actionable assignments: " + examples))
            let collisions =
                actionableTasks
                |> List.groupBy (fun (_, task) -> task.Id)
                |> List.choose (fun (taskId, items) ->
                    if items.Length < 2 then None
                    else Some("Task " + taskId + " has " + string items.Length +
                              " actionable claims across registered worktrees"))
            let diagnostics =
                allTasks
                |> List.collect (fun (worktree, task) ->
                    task.Diagnostics
                    |> List.map (fun diag -> worktree.Path + "/" + task.Id + ": " + diag))
            let inspectionErrors =
                groups |> List.choose (fun group ->
                    group.Error |> Option.map (fun error -> group.Worktree.Path + ": " + error))
            let matches (_, task: TaskSummary) =
                (cmd.Owner.IsNone || task.Owner = cmd.Owner)
                && (cmd.Status.IsNone || Some task.Status = cmd.Status)
                && (cmd.NextActor.IsNone || task.NextActor = cmd.NextActor)
                && (not cmd.ReviewerOnly || task.NextActor = Some "reviewer")
            let filtered =
                allTasks
                |> List.filter matches
                |> List.sortBy (fun (worktree, task) ->
                    (if actionable task then 0 else 1),
                    -(task.UpdatedAt |> Option.map (fun value -> value.UtcTicks) |> Option.defaultValue 0L),
                    worktree.Path, task.Id)
            let page = filtered |> List.skip (min filtered.Length cmd.Offset) |> List.truncate cmd.Limit
            let pageKeys =
                page |> List.map (fun (wt, task) -> wt.Path, task.Id) |> Set.ofList
            let compactGroups =
                groups
                |> List.map (fun group ->
                    let tasks =
                        group.Tasks
                        |> List.filter (fun task -> pageKeys.Contains(group.Worktree.Path, task.Id))
                        |> List.map compactSnapshotTask
                    { group with Tasks = tasks })
            let! allActionableDetails, detailErrors =
                fun rt ->
                    let mutable found = []
                    let mutable errors = []
                    for worktree, task in actionableTasks do
                        match assignmentForTask task.Owner.Value projectName worktree task rt with
                        | Ok assignment ->
                            found <- assignment :: found
                        | Error error ->
                            if errors.Length < 8 then
                                errors <- (worktree.Path + "/" + task.Id + ": " + string error) :: errors
                    Ok(List.rev found, List.rev errors)
            let selected =
                allActionableDetails
                |> List.filter (fun assignment ->
                    pageKeys.Contains(assignment.Worktree.Path, assignment.Task.Id))
                |> List.map (fun assignment ->
                    { assignment with
                        Task = compactSnapshotTask assignment.Task
                        // Full instructions are available from GetAgentAssignment,
                        // not repeated in every global snapshot page.
                        AssignmentInstructions = None })
            let! retirement, retirementErrors =
                if not cmd.IncludeRetirement then fun _ -> Ok([], [])
                else
                    fun rt ->
                        let mutable found = []
                        let mutable errors = []
                        for group in groups |> List.filter (fun group -> not group.Worktree.IsMain) do
                            match retireWorktree
                                { ProjectName = projectName; WorktreePath = group.Worktree.Path
                                  TargetRef = None; DeleteBranch = false; DryRun = true } rt with
                            | Ok inspection ->
                                found <- { inspection with
                                            TaskOnlyCommitsPreservedOnBranch =
                                                inspection.TaskOnlyCommitsPreservedOnBranch |> List.truncate 8
                                            Reasons = inspection.Reasons |> List.truncate 5 } :: found
                            | Error error ->
                                if errors.Length < 8 then
                                    errors <- (group.Worktree.Path + ": " + string error) :: errors
                        Ok(List.rev found, List.rev errors)
            let integrated =
                compactGroups
                |> List.collect (fun group ->
                    let verified =
                        group.Worktree.IsMain ||
                        (retirement |> List.exists (fun item ->
                            item.Worktree.Path = group.Worktree.Path && item.SourceEquivalent))
                    if not verified then []
                    else
                        group.Tasks
                        |> List.filter (fun task -> task.Status = "completed")
                        |> List.map (fun task ->
                            { Worktree = group.Worktree; Task = task; Phase = "Integrated" }))
            let count = filtered.Length
            return
                { ProjectName = projectName
                  Worktrees = compactGroups
                  Assignments = selected
                  Conflicts = (owners @ collisions) |> List.truncate 12
                  ReviewerTaskCount =
                      actionableTasks
                      |> List.filter (fun (_, task) -> task.NextActor = Some "reviewer")
                      |> List.length
                  UnacknowledgedTaskCount =
                      allActionableDetails
                      |> List.filter (fun item ->
                          item.AssignmentRevision.IsSome
                          && item.AssignmentRevision <> item.AcknowledgedAssignmentRevision
                          && item.Task.NextActor = Some "agent")
                      |> List.length
                  InspectionErrors = (inspectionErrors @ detailErrors @ retirementErrors) |> List.truncate 12
                  RetirementInspections = retirement
                  RetirementCandidates = retirement |> List.filter (fun item -> item.CanRetire) |> List.map (fun item -> item.Worktree.Path)
                  IntegratedTasks = integrated
                  TotalMatches = count
                  Offset = cmd.Offset
                  Limit = cmd.Limit
                  NextOffset = if cmd.Offset + page.Length < count then Some(cmd.Offset + page.Length) else None
                  TaskDiagnosticCount = diagnostics.Length
                  TaskDiagnostics = diagnostics |> List.truncate 12 |> List.map (truncateSnapshotField 260)
                  RetirementInspected = cmd.IncludeRetirement }
    }

let beginTask (cmd: BeginTaskCommand) =
    updateState cmd.ProjectName cmd.TaskId cmd.ExpectedRevision $"Begin task {cmd.TaskId}"
        (fun obj ->
            if ownerId obj <> Some cmd.Agent then
                Error(Client.ValidationError "Only the claimed agent may begin this task.")
            elif assignmentRevision obj <> Some cmd.AssignmentRevision then
                Error(Client.ValidationError "Assignment revision is stale or missing.")
            elif getString obj "status" = Some "completed" then
                Error(Client.ValidationError "Cannot begin a completed task.")
            else
                match obj["acknowledgement"] with
                | :? JsonObject as receipt
                    when getString receipt "revision" = Some cmd.AssignmentRevision
                         && getString receipt "agent" = Some cmd.Agent ->
                    receipt["startedAt"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"))
                    recordActor obj "agent" cmd.Agent
                    Ok()
                | _ ->
                    Error(Client.ValidationError "Acknowledge the current assignment revision before beginning work."))
