module Client.SignalR.Client

open System.Text
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

let private toAgentResponse response =
    match response with
    | Error error ->
        { Result = None
          Error = Some(EffectError.toAgentError error) }
    | Ok (payload: string) ->
        let bytes = Encoding.UTF8.GetByteCount payload

        if bytes <= AgentProtocol.maxResponseBytes then
            { Result = Some payload
              Error = None }
        else
            { Result = None
              Error = Some(OutputTruncated $"Command response was {bytes} bytes and exceeds the safe SignalR response size of {AgentProtocol.maxResponseBytes} bytes. Narrow the request or read fewer files.") }

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
    | RunProjectTaskCommand cmd -> Some("RunProjectTask", Some cmd.ProjectName, [ ProcessExecution ], [], None, [ cmd.TaskName ])
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
    | ListProjectTasksCommand cmd -> rt |> ClientShell.listProjectTasks cmd |> serialize'
    | RunProjectTaskCommand cmd ->
        rt
        |> ClientShell.resolveProjectTask cmd
        |> Result.bind (fun task -> ClientShell.runProjectTask cmd.ProjectName task rt)
        |> serialize'
    | GetGitStatusCommand cmd -> rt |> ClientShell.getGitStatus cmd |> serialize'
    | GetGitDiffCommand cmd -> rt |> ClientShell.getGitDiff cmd |> serialize'
    | GitCommitCommand cmd -> rt |> ClientShell.gitCommit cmd |> serialize'
    | StartJobCommand cmd -> rt |> JobManager.startJob cmd |> serialize'
    | ListJobsCommand cmd -> rt |> JobManager.listJobs cmd |> serialize'
    | GetJobResultCommand cmd -> rt |> JobManager.getJobResult cmd |> serialize'
    | CancelJobCommand cmd -> rt |> JobManager.cancelJob cmd |> serialize'

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    task {
        let commandDescription = AgentCommandInfo.describe command
        rt.Tui.Log $"Incoming command: {commandDescription}"

        let permission = rt :> PermissionIO
        let resolution =
            match command with
            | RunProjectTaskCommand cmd ->
                ClientShell.resolveProjectTask cmd rt
                |> Result.map (fun task ->
                    let authorizationCommand =
                        ClientShell.projectTaskAsRunCommand cmd.ProjectName task
                        |> RunCommandCommand

                    authorizationCommand, Some(cmd, task))
            | _ ->
                Ok(command, None)

        let! response =
            task {
                match resolution with
                | Error error ->
                    return Error error
                | Ok(authorizationCommand, resolvedTask) ->
                    let! authorization =
                        PermissionPolicy.authorizeWithMode permission.PermissionMode permission.PromptPermission authorizationCommand

                    match authorization, resolvedTask with
                    | Error error, _ ->
                        return Error error
                    | Ok(), Some(cmd, task) ->
                        let! result = ClientShell.runProjectTask cmd.ProjectName task rt |> serialize'
                        return result
                    | Ok(), None ->
                        let! result = dispatch rt command
                        return result
            }

        match response with
        | Ok _ -> rt.Tui.Log $"Command executed: {commandDescription}. Sending response."
        | Error err -> rt.Tui.Log $"Command failed: {commandDescription}. {EffectError.toString err}"

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

        let agentResponse = toAgentResponse response
        let! sendResult = connection.invokeAsync("SendClientResponse", correlationId, agentResponse)

        match sendResult with
        | Ok() -> ()
        | Error ex -> rt.Tui.Log $"Failed to send command response: {ex.Message}"
    }
