module Client.TaskStore

open System
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

let private summary (relativeDir: string) (folder: string) (id: string) =
    let html = Path.Combine(folder, id + ".html")
    let json = Path.Combine(folder, id + ".json")
    let stateExists = File.Exists json
    let docExists = File.Exists html
    let descriptionPath = relativeDir + "/" + id + ".html"
    let statePath = relativeDir + "/" + id + ".json"
    let mutable diagnostics = []
    let mutable title = id
    let mutable status = "open"
    let mutable hash = None
    if not docExists then diagnostics <- "Missing HTML task description." :: diagnostics
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
                | Error err -> diagnostics <- err :: diagnostics
            with ex -> diagnostics <- ex.Message :: diagnostics
    { Id = id
      Title = title
      Status = status
      DescriptionPath = descriptionPath
      StatePath = statePath
      HasState = stateExists
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
                |> List.map (Path.GetFileNameWithoutExtension)
                |> List.distinct
                |> List.sort
            return
                names
                |> List.map (fun id ->
                    if validId id then summary relative folder id
                    else
                        { Id = id; Title = id; Status = "invalid"; DescriptionPath = ""; StatePath = ""
                          HasState = false; Revision = None; Diagnostics = [ "Invalid task identifier." ] })
                |> List.filter (fun item -> cmd.Status.IsNone || Some item.Status = cmd.Status)
    }

let getTask (cmd: GetTaskCommand) =
    effect {
        if not (validId cmd.TaskId) then
            return! Client.ValidationError "Invalid task identifier." |> Effect.ofError
        else
            let! (FolderPath root) = ProjectPaths.resolveProjectRoot cmd.ProjectName
            let! (relative, folder) = fun _ -> directory root
            let html = Path.Combine(folder, cmd.TaskId + ".html")
            let json = Path.Combine(folder, cmd.TaskId + ".json")
            if not (File.Exists html || File.Exists json) then
                return! Client.NotFoundError "Task does not exist." |> Effect.ofError
            elif (File.Exists html && (File.GetAttributes html).HasFlag(FileAttributes.ReparsePoint)) then
                return! Client.PermissionDenied "Task description is a symbolic link." |> Effect.ofError
            else
                return
                    { Task = summary relative folder cmd.TaskId
                      Html = if File.Exists html then File.ReadAllText html else ""
                      StateJson = if File.Exists json && not ((File.GetAttributes json).HasFlag(FileAttributes.ReparsePoint)) then Some(File.ReadAllText json) else None }
    }
