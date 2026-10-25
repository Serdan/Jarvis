module Client.WorkTaskCompat

open System
open System.Text.Json.Nodes
open Common
open Client.Effect
open Client.IO

let private tryCanonical projectName taskId rt =
    match TaskStore.getTask { ProjectName = projectName; TaskId = taskId } rt with
    | Ok task when task.Task.HasState && not (String.IsNullOrWhiteSpace task.Html) ->
        Some task
    | _ -> None

let private getString (obj: JsonObject) (key: string) =
    match obj[key] with
    | null -> None
    | value -> try Some(value.GetValue<string>()) with _ -> None

let private getNested (obj: JsonObject) (key: string) =
    match obj[key] with
    | :? JsonObject as nested -> Some nested
    | _ -> None

let private getArray (obj: JsonObject) (key: string) =
    match obj[key] with
    | :? JsonArray as values ->
        values |> Seq.choose (fun value ->
            if isNull value then None else
            try Some(value.GetValue<string>()) with _ -> None) |> Seq.toList
    | _ -> []

let private git projectName arguments rt =
    ClientShell.runCommand
        { ProjectName = projectName
          Executable = "git"
          Args = arguments
          Reason = None
          WorkingDirectory = None
          TimeoutSeconds = Some 20
          MaxOutputBytes = Some 8192 } rt

let private toWorkTask projectName (details: TaskStore.TaskDetails) rt =
    try
        let state =
            details.StateJson
            |> Option.map JsonNode.Parse
            |> function
                | Some (:? JsonObject as obj) -> obj
                | _ -> JsonObject()
        let checkpoint = getNested state "checkpoint" |> Option.defaultValue (JsonObject())
        let summary = getString checkpoint "summary"
        let nextAction = getString checkpoint "nextAction"
        let verified = getArray checkpoint "verified"
        let lastCommit = getString checkpoint "lastCommit"
        let blocker = getString checkpoint "blocker"
        let descriptionCommit = getString state "descriptionCommit"
        let updated =
            match getString state "updatedAt" with
            | Some timestamp ->
                match DateTimeOffset.TryParse(timestamp: string) with
                | true, value -> value
                | _ -> DateTimeOffset.UtcNow
            | None -> DateTimeOffset.UtcNow
        match git projectName [ "log"; "-1"; "--format=%H"; "--"; details.Task.DescriptionPath ] rt,
              git projectName [ "status"; "--porcelain"; "--"; details.Task.DescriptionPath ] rt with
        | Ok commit, Ok status when commit.ExitCode = 0 && status.ExitCode = 0 ->
            let revision = commit.StdOut.Trim()
            let baseline = descriptionCommit |> Option.defaultValue revision
            Ok
                { TaskId = details.Task.Id
                  TaskPath = details.Task.DescriptionPath
                  TaskCommit = baseline
                  CurrentTaskCommit = if String.IsNullOrWhiteSpace revision then None else Some revision
                  TaskChanged = (baseline <> revision)
                  TaskDirty = not (String.IsNullOrWhiteSpace status.StdOut)
                  Status = details.Task.Status
                  Summary = summary
                  NextAction = nextAction
                  Verified = verified
                  Blocker = blocker
                  CreatedAt = updated
                  UpdatedAt = updated
                  LastCommit = lastCommit
                  TaskContent = Some details.Html }
        | Error error, _ | _, Error error -> Error error
        | _ -> Error(Client.ValidationError "Cannot inspect task description Git history.")
    with ex -> Error(Client.ValidationError($"Invalid Git-backed task state: {ex.Message}"))

let attachWorkTask (cmd: AttachWorkTaskCommand) rt =
    let candidate = TaskStore.getTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt
    match candidate with
    | Ok details when details.Task.DescriptionPath = cmd.TaskPath && not (String.IsNullOrWhiteSpace details.Html) ->
        if details.Task.HasState then
            Error(Client.ValidationError "Task is already attached.")
        else
            match git cmd.ProjectName [ "status"; "--porcelain"; "--"; cmd.TaskPath ] rt,
                  git cmd.ProjectName [ "log"; "-1"; "--format=%H"; "--"; cmd.TaskPath ] rt with
            | Ok status, Ok commit when status.ExitCode = 0 && commit.ExitCode = 0
                                        && String.IsNullOrWhiteSpace status.StdOut
                                        && not (String.IsNullOrWhiteSpace commit.StdOut) ->
                match TaskStore.updateTask
                    { ProjectName = cmd.ProjectName
                      TaskId = cmd.TaskId
                      ExpectedRevision = None
                      Title = None
                      Status = Some "active"
                      Dependencies = None
                      DescriptionCommit = Some(commit.StdOut.Trim()) } rt with
                | Ok result -> toWorkTask cmd.ProjectName result.Task rt
                | Error error -> Error error
            | _ -> Error(Client.ValidationError "Commit a clean HTML description before attaching task state.")
    | _ -> ClientShell.attachWorkTask cmd rt

let getWorkTask (cmd: GetWorkTaskCommand) rt =
    match tryCanonical cmd.ProjectName cmd.TaskId rt with
    | Some details -> toWorkTask cmd.ProjectName details rt
    | None -> ClientShell.getWorkTask cmd rt

let listWorkTasks (cmd: ListWorkTasksCommand) rt =
    match TaskStore.listTasks { ProjectName = cmd.ProjectName; Status = None; Owner = None; DependsOn = None } rt,
          ClientShell.listWorkTasks cmd rt with
    | Ok tasks, Ok legacy ->
        let active = tasks |> List.filter (fun task ->
            task.HasState && (task.Status = "active" || task.Status = "blocked"))
        let rec collect (seen: Set<string>) (result: WorkTask list) (remaining: TaskStore.TaskSummary list) =
            match remaining with
            | [] -> Ok(List.rev result, seen)
            | task :: rest ->
                match tryCanonical cmd.ProjectName task.Id rt with
                | Some details ->
                    match toWorkTask cmd.ProjectName details rt with
                    | Ok value -> collect (Set.add task.Id seen) (value :: result) rest
                    | Error error -> Error error
                | None -> collect seen result rest
        match collect Set.empty [] active with
        | Error error -> Error error
        | Ok(newTasks, seen) ->
            Ok
                { Tasks =
                    newTasks
                    @ (legacy.Tasks |> List.filter (fun task -> not (Set.contains task.TaskId seen))) }
    | Error err, _ | _, Error err -> Error err

let checkpointWorkTask (cmd: CheckpointWorkTaskCommand) rt =
    let canonical = tryCanonical cmd.ProjectName cmd.TaskId rt
    let task =
        match canonical with
        | Some _ -> true
        | None ->
            match TaskStore.getTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
            | Ok details when not (String.IsNullOrWhiteSpace details.Html) ->
                match ClientShell.getWorkTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
                | Ok _ -> true
                | _ -> false
            | _ -> false
    if task then
        let legacyState =
            match ClientShell.getWorkTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
            | Ok saved -> Some saved
            | Error _ -> None
        match TaskStore.checkpointTask
            { ProjectName = cmd.ProjectName
              TaskId = cmd.TaskId
              ExpectedRevision = None
              Status = cmd.Status
              Summary = cmd.Summary
              NextAction = cmd.NextAction
              Verified = cmd.Verified
              LastCommit = cmd.LastCommit
              Blocker = cmd.Blocker
              DescriptionCommit = legacyState |> Option.map (fun saved -> saved.TaskCommit) } rt with
        | Ok result ->
            // Retire the old local checkpoint only after the Git-backed commit succeeds.
            match ClientShell.getWorkTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
            | Ok _ ->
                match ClientShell.completeWorkTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
                | Error error -> Error error
                | Ok _ -> toWorkTask cmd.ProjectName result.Task rt
            | _ -> toWorkTask cmd.ProjectName result.Task rt
        | Error error -> Error error
    else ClientShell.checkpointWorkTask cmd rt

let completeWorkTask (cmd: CompleteWorkTaskCommand) rt =
    let canonical = tryCanonical cmd.ProjectName cmd.TaskId rt
    let legacy =
        match ClientShell.getWorkTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
        | Ok task -> Some task
        | _ -> None
    let usesCanonical =
        canonical.IsSome ||
        (legacy.IsSome &&
         (match TaskStore.getTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
          | Ok task -> not (String.IsNullOrWhiteSpace task.Html)
          | _ -> false))
    if usesCanonical then
        let prior = canonical |> Option.orElseWith (fun () ->
            match TaskStore.getTask { ProjectName = cmd.ProjectName; TaskId = cmd.TaskId } rt with
            | Ok task -> Some task
            | Error _ -> None)
        let checkpoint =
            prior |> Option.map (fun task ->
                match task.StateJson |> Option.map JsonNode.Parse with
                | Some (:? JsonObject as obj) ->
                    match getNested obj "checkpoint" with
                    | Some details -> getArray details "verified"
                    | None -> []
                | _ -> []) |> Option.defaultValue []
        let verified =
            if checkpoint.IsEmpty then legacy |> Option.map (fun t -> t.Verified) |> Option.defaultValue []
            else checkpoint
        match TaskStore.completeTask
            { ProjectName = cmd.ProjectName
              TaskId = cmd.TaskId
              ExpectedRevision = None
              Verified = verified
              FinalCommit = legacy |> Option.bind (fun task -> task.LastCommit) } rt with
        | Error error -> Error error
        | Ok result ->
            match legacy with
            | Some _ ->
                // Preserve the completed Git checkpoint first, then discard obsolete local state.
                match ClientShell.completeWorkTask cmd rt with
                | Error error -> Error error
                | Ok _ -> 
                    Ok
                        { TaskId = cmd.TaskId
                          TaskPath = result.Task.Task.DescriptionPath
                          TaskCommit = result.CommitHash
                          FinalCommit = result.CommitHash
                          CompletedAt = DateTimeOffset.UtcNow }
            | None ->
                Ok
                    { TaskId = cmd.TaskId
                      TaskPath = result.Task.Task.DescriptionPath
                      TaskCommit = result.CommitHash
                      FinalCommit = result.CommitHash
                      CompletedAt = DateTimeOffset.UtcNow }
    else ClientShell.completeWorkTask cmd rt
