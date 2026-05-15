module Client.SignalR.Client

open System.Text.Json
open System.Threading.Tasks
open Client
open Client.SignalR
open Microsoft.AspNetCore.SignalR.Client
open Client.Effect
open Common
open FsToolkit.ErrorHandling

let receiveMessage (rt: Runtime) message =
    rt.Tui.Log message
    Task.CompletedTask

let private serialize<'a> (value: 'a) =
    try
        JsonSerializer.Serialize value |> Ok
    with e ->
        e |> ExceptionError |> Error

let private serialize'<'a> = Result.bind serialize<'a> >> ValueTask<_>

let private unwrapProjectName (ProjectName name) = name
let private unwrapContent (Content content) = content

let private readFileResultToDto path value =
    match value with
    | Content'.Text text ->
        {| filePath = path
           content = text
           error = null |}
    | Content'.Error error ->
        {| filePath = path
           content = null
           error = EffectError.toString error |}

let private auditDetails command =
    match command with
    | WriteFileCommand cmd -> Some("WriteFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | PatchFileCommand cmd -> Some("PatchFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | RunCommandCommand cmd -> Some("RunCommand", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | GitCommitCommand cmd -> Some("GitCommit", Some cmd.ProjectName, [ VersionControlWrite ], cmd.Paths, Some "git", [ cmd.Message ])
    | StartJobCommand cmd -> Some("StartJob", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | CancelJobCommand cmd -> Some("CancelJob", None, [ ProcessExecution ], [], None, [ cmd.JobId ])
    | _ -> None

let private audit command response =
    match auditDetails command with
    | None -> ()
    | Some(commandName, projectName, permissions, paths, executable, args) ->
        let summary =
            match response with
            | Ok _ -> "Ok"
            | Error error -> EffectError.toString error

        AuditLog.recordCommand commandName projectName permissions paths executable args summary

let private dispatch rt command =
    match command with
    | ListCommandsCommand -> rt |> ProjectBrowser.listCommands |> serialize'
    | ListProjectsCommand ->
        rt
        |> ProjectBrowser.listProjects
        |> Result.map (Seq.map unwrapProjectName >> Seq.toList)
        |> serialize'
    | GetProjectDetailsCommand cmd ->
        rt
        |> ProjectBrowser.getProjectDetails cmd
        |> Result.map (List.map (fun (name, content) -> {| fileName = name; content = unwrapContent content |}))
        |> serialize'
    | ListDirectoryCommand cmd -> rt |> ProjectBrowser.listDirectory cmd |> serialize'
    | SearchFilesCommand cmd -> rt |> ProjectBrowser.searchFiles cmd |> serialize'
    | SearchTextCommand cmd -> rt |> ProjectBrowser.searchText cmd |> serialize'
    | ReadFileCommand cmd ->
        rt
        |> ProjectBrowser.readFile cmd
        |> Result.map unwrapContent
        |> serialize'
    | ReadFilesCommand cmd ->
        rt
        |> ProjectBrowser.readFiles cmd
        |> Result.map (Seq.zip cmd.FilePaths >> Seq.map (fun (path, result) -> readFileResultToDto path result) >> Seq.toList)
        |> serialize'
    | WriteFileCommand cmd -> rt |> ProjectBrowser.writeFile cmd |> serialize'
    | PatchFileCommand cmd ->
        rt
        |> ProjectBrowser.patchFile cmd
        |> serialize'
    | RunCommandCommand cmd -> rt |> ClientShell.runCommand cmd |> serialize'
    | GetGitStatusCommand cmd -> rt |> ClientShell.getGitStatus cmd |> serialize'
    | GetGitDiffCommand cmd -> rt |> ClientShell.getGitDiff cmd |> serialize'
    | GitCommitCommand cmd -> rt |> ClientShell.gitCommit cmd |> serialize'
    | StartJobCommand cmd -> rt |> JobManager.startJob cmd |> serialize'
    | ListJobsCommand cmd -> rt |> JobManager.listJobs cmd |> serialize'
    | GetJobResultCommand cmd -> rt |> JobManager.getJobResult cmd |> serialize'
    | CancelJobCommand cmd -> rt |> JobManager.cancelJob cmd |> serialize'

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    task {
        rt.Tui.Log $"Incoming command: {command.GetType().Name}"

        let permission = rt :> PermissionIO
        let! authorization = PermissionPolicy.authorizeWithMode permission.PermissionMode permission.PromptPermission command

        let! response =
            match authorization with
            | Error error -> Error error |> ValueTask<_>
            | Ok() -> dispatch rt command

        match response with
        | Ok _ -> rt.Tui.Log "Command executed. Sending response."
        | Error err -> rt.Tui.Log $"Command failed: {EffectError.toString err}"

        audit command response

        return response
    }

let receiveCommandAndReply (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) : Task =
    task {
        let! response =
            task {
                try
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    return! receiveCommand rt command
                with ex ->
                    return Error(ExceptionError ex)
            }

        let payload =
            match response with
            | Ok value -> value
            | Error error -> EffectError.toString error

        let! sendResult = connection.invokeAsync("SendClientResponse", correlationId, payload)

        match sendResult with
        | Ok() -> ()
        | Error ex -> rt.Tui.Log $"Failed to send command response: {ex.Message}"
    }
