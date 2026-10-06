module Client.SignalR.Client

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
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

let private toAgentResponse command response =
    match response with
    | Error error ->
        { Result = None
          Error = Some(EffectError.toAgentError error) }
    | Ok (payload: string) ->
        let bytes = Encoding.UTF8.GetByteCount payload
        let maxBytes =
            match command with
            | ReadImageCommand _ -> (AgentProtocol.maxImageBytes * 4 / 3) + (64 * 1024)
            | _ -> AgentProtocol.maxResponseBytes

        if bytes <= maxBytes then
            { Result = Some payload
              Error = None }
        else
            { Result = None
              Error = Some(OutputTruncated $"Command response was {bytes} bytes and exceeds the safe response size of {maxBytes} bytes.") }

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
    | ImportFileCommand cmd -> Some("ImportFile", Some cmd.ProjectName, [ WorkspaceWrite; NetworkAccess ], [ cmd.FilePath ], None, [])
    | WriteFileCommand cmd -> Some("WriteFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | PatchFileCommand cmd -> Some("PatchFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | RunCommandCommand cmd -> Some("RunCommand", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | RunProjectTaskCommand cmd -> Some("RunProjectTask", Some cmd.ProjectName, [ ProcessExecution ], [], None, [ cmd.TaskName ])
    | CreateSkillCommand cmd -> Some("CreateSkill", Some cmd.ProjectName, [ WorkspaceWrite ], [ $".jarvis/skills/{cmd.SkillName}/SKILL.md" ], None, [])
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

let private importFile (cmd: ImportFileCommand) (rt: Runtime) =
    match cmd.MaxBytes with
    | Some value when value <= 0L ->
        Error(Client.ValidationError "MaxBytes must be greater than zero when supplied.")
    | requestedLimit ->
        match Uri.TryCreate(cmd.DownloadUrl, UriKind.Absolute) with
        | false, _ ->
            Error(Client.ValidationError "DownloadUrl must be an absolute URL.")
        | true, uri when uri.Scheme <> Uri.UriSchemeHttps ->
            Error(Client.ValidationError "DownloadUrl must use HTTPS.")
        | true, uri ->
            match ProjectPaths.resolveWritableProjectFile cmd.ProjectName cmd.FilePath rt with
            | Error error -> Error error
            | Ok(FilePath destinationPath) ->
                let localLimit =
                    if rt.MaxImportBytes = 0L then Int64.MaxValue
                    else rt.MaxImportBytes

                let effectiveLimit =
                    requestedLimit
                    |> Option.map (fun requested -> min requested localLimit)
                    |> Option.defaultValue localLimit

                let parentPath = Path.GetDirectoryName destinationPath
                let tempPath =
                    Path.Combine(parentPath, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.jarvis-import")

                try
                    if File.Exists destinationPath && not cmd.Overwrite then
                        Error(Client.ValidationError $"Destination file already exists: {cmd.FilePath}")
                    else
                        if cmd.CreateParents then
                            Directory.CreateDirectory(parentPath) |> ignore
                        elif not (Directory.Exists parentPath) then
                            raise (DirectoryNotFoundException $"Parent directory does not exist: {Path.GetDirectoryName(cmd.FilePath)}")

                        use request = new HttpRequestMessage(HttpMethod.Get, uri)
                        use response = rt.httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead)

                        if response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.MultipleChoices then
                            Error(Client.ValidationError $"Download failed with HTTP status {int response.StatusCode}.")
                        else
                            let contentLength = response.Content.Headers.ContentLength

                            if contentLength.HasValue && contentLength.Value > effectiveLimit then
                                Error(Client.ValidationError $"File is {contentLength.Value} bytes and exceeds the {effectiveLimit} byte import limit.")
                            else
                                use source = response.Content.ReadAsStream()
                                use destination =
                                    new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan)
                                use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                                let buffer = Array.zeroCreate<byte> 81920
                                let mutable total = 0L
                                let mutable finished = false

                                while not finished do
                                    let read = source.Read(buffer, 0, buffer.Length)
                                    if read = 0 then
                                        finished <- true
                                    else
                                        total <- total + int64 read
                                        if total > effectiveLimit then
                                            raise (InvalidDataException $"Downloaded file exceeds the {effectiveLimit} byte import limit.")

                                        destination.Write(buffer, 0, read)
                                        hash.AppendData(buffer, 0, read)

                                destination.Flush(true)
                                let digest =
                                    hash.GetHashAndReset()
                                    |> Convert.ToHexString
                                    |> fun value -> $"sha256:{value.ToLowerInvariant()}"

                                File.Move(tempPath, destinationPath, cmd.Overwrite)

                                Ok
                                    { FilePath = cmd.FilePath
                                      FileId = cmd.FileId
                                      BytesWritten = total
                                      MimeType = cmd.MimeType
                                      Sha256 = digest }
                with ex ->
                    if File.Exists tempPath then
                        try File.Delete tempPath with _ -> ()

                    Error(Client.ExceptionError ex)

let private dispatch (rt: Runtime) (command: AgentCommand) =
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
    | ReadImageCommand cmd ->
        rt
        |> ProjectBrowser.readImage cmd
        |> serialize'
    | ImportFileCommand cmd -> rt |> importFile cmd |> serialize'
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
    | ListSkillsCommand cmd -> rt |> ProjectBrowser.listSkills cmd |> serialize'
    | GetSkillCommand cmd -> rt |> ProjectBrowser.getSkill cmd |> serialize'
    | CreateSkillCommand cmd -> rt |> ProjectBrowser.createSkill cmd |> serialize'
    | GetGitStatusCommand cmd -> rt |> ClientShell.getGitStatus cmd |> serialize'
    | GetGitDiffCommand cmd -> rt |> ClientShell.getGitDiff cmd |> serialize'
    | GitCommitCommand cmd -> rt |> ClientShell.gitCommit cmd |> serialize'
    | StartJobCommand cmd -> rt |> JobManager.startJob cmd |> serialize'
    | ListJobsCommand cmd -> rt |> JobManager.listJobs cmd |> serialize'
    | GetJobResultCommand cmd -> rt |> JobManager.getJobResult cmd |> serialize'
    | CancelJobCommand cmd -> rt |> JobManager.cancelJob cmd |> serialize'
    | MessageCommand cmd ->
        if String.IsNullOrWhiteSpace cmd.Message then
            Error(Client.ValidationError "Message cannot be empty.") |> serialize'
        elif cmd.Message.Length > 1000 then
            Error(Client.ValidationError "Message cannot exceed 1000 characters.") |> serialize'
        else
            rt.Tui.Message(cmd.ProjectName, cmd.Message)
            Ok() |> serialize'
    | GetClientActivityCommand cmd ->
        rt.Tui.GetActivitySnapshot(cmd.ProjectName, cmd.Limit)
        |> Ok
        |> serialize'

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    task {
        let activityId =
            match command with
            | GetClientActivityCommand _
            | MessageCommand _ -> None
            | _ -> Some(rt.Tui.StartActivity(command))
        let stopwatch = Stopwatch.StartNew()

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
                    let promptWithActivityState promptCommand request =
                        task {
                            activityId
                            |> Option.iter rt.Tui.MarkActivityAwaitingPermission
                            let! approval = permission.PromptPermission promptCommand request
                            activityId
                            |> Option.iter rt.Tui.MarkActivityRunning
                            return approval
                        }

                    let! authorization =
                        PermissionPolicy.authorizeWithTrust permission.TrustLevel promptWithActivityState authorizationCommand

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

        stopwatch.Stop()

        match response with
        | Ok payload ->
            let outcome = ActivityPresentation.tryResultSummary command payload
            activityId
            |> Option.iter (fun id ->
                rt.Tui.CompleteActivity(id, stopwatch.ElapsedMilliseconds, outcome))
        | Error err ->
            activityId
            |> Option.iter (fun id ->
                rt.Tui.FailActivity(id, stopwatch.ElapsedMilliseconds, ActivityPresentation.shortError err, ActivityPresentation.fullError err))

        audit command response

        return response
    }

let receiveCommandAndReply (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) : Task =
    task {
        let mutable parsedCommand = None
        let! response =
            task {
                try
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    parsedCommand <- Some command
                    return! receiveCommand rt command
                with ex ->
                    return Error(ExceptionError ex)
            }

        let agentResponse =
            match parsedCommand with
            | Some command -> toAgentResponse command response
            | None -> toAgentResponse ListCommandsCommand response
        let! sendResult =
            ResponseDelivery.sendWithReconnectRetry
                (TimeSpan.FromMinutes 2.0)
                (TimeSpan.FromMilliseconds 250.0)
                (fun () -> connection.State)
                (fun () -> connection.invokeAsync("SendClientResponse", correlationId, agentResponse))

        match sendResult with
        | Ok() -> ()
        | Error ex ->
            rt.Tui.Log $"Failed to deliver command response after reconnect retries: {ex.Message}"
    }
