module Client.SignalR.Client

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.CompilerServices
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

// Explicit agent labels identify the current work slot. Cache canonical
// Working state for ordinary calls to avoid one remote git fetch per tool.
let private workingGuidanceCaches = ConditionalWeakTable<Runtime, WorkingTurnGuidance.AgentCache>()
let private instructionDeliveryCaches = ConditionalWeakTable<Runtime, AgentInstructions.DeliveryCache>()
// Only commands addressing repository source are checkout selectors.
// Message/activity project fields are literal history labels, not paths.
type private CommandProject =
    | Unscoped
    | ActivityLabel of string option
    | Checkout of ProjectPaths.ProjectTarget

let private projectTarget = function
    | Checkout target -> Some target
    | Unscoped | ActivityLabel _ -> None

let private classifyProject command =
    match command with
    | MessageCommand cmd -> Ok(ActivityLabel cmd.ProjectName)
    | GetClientActivityCommand cmd -> Ok(ActivityLabel cmd.ProjectName)
    | ListSkillsCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | GetSkillCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | CreateSkillCommand cmd when cmd.ProjectName = "@global" -> Ok Unscoped
    | GetJobResultCommand cmd when String.IsNullOrWhiteSpace cmd.ProjectName -> Ok Unscoped
    | CancelJobCommand cmd when String.IsNullOrWhiteSpace cmd.ProjectName -> Ok Unscoped
    | _ ->
        match AgentCommandInfo.projectName command with
        | None -> Ok Unscoped
        | Some raw -> ProjectPaths.ProjectTarget.parse raw |> Result.map Checkout


let private checkWorkingGuidance (rt: Runtime) agentId (target: ProjectPaths.ProjectTarget option) command =
    if String.IsNullOrWhiteSpace agentId then []
    else
        match target with
        | None -> []
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let now = DateTimeOffset.UtcNow
            let cache = workingGuidanceCaches.GetValue(rt, fun _ -> WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            match command with
            | TaskWriteCommand _ -> cache.Invalidate(projectId, agentId)
            | _ -> ()
            let agent =
                cache.Get(now, projectId, agentId, fun () ->
                    match OrchestrationApi.LedgerBindingConfig.load project rt with
                    | Error _ -> None
                    | Ok binding ->
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Error _ -> None
                        | Ok found -> Some found)
            match agent |> Option.bind (WorkingTurnGuidance.instructionAt now) with
            | Some content ->
                [ { Scope = "working-turn"; Path = "jarvis/working-turn"
                    Sha256 = ""; Content = content } ]
            | None -> []

// An agent's first ordinary tool call starts its Ready assignment.
// No new session identifier or explicit resume tool is needed. Never
// auto-activate a different agent merely because an administrative operation
// names that agent as its target.
let private activatesAgent (agentId: string) command =
    match command with
    | TaskWriteCommand cmd ->
        cmd.ActorId = agentId
        && List.contains cmd.Action
            [ "CheckpointAttempt"; "YieldAgent"; "CompleteAgentWork"
              "AcknowledgeFeedback"; "AcknowledgeNotification"; "NotifyAgent"
              "BindAttemptSource"; "RebindAttemptSource"; "BindReviewBase"
              "SetAttemptSource"; "SubmitIntegration"; "CompleteIntegration" ]
    | TaskReadCommand cmd ->
        String.IsNullOrWhiteSpace cmd.AgentId || cmd.AgentId = agentId
    | LedgerWriteCommand _ | ReviewWriteCommand _ -> false
    | _ -> true

let private activateReadyOnActivity (rt: Runtime) (agentId: string)
                                (target: ProjectPaths.ProjectTarget option) command =
    if String.IsNullOrWhiteSpace agentId || not (activatesAgent agentId command) then Ok ()
    else
        match target with
        | None -> Ok ()
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let cache = workingGuidanceCaches.GetValue(rt, fun _ -> WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            // Assignment transitions can be written by another client. A cached
            // Working value cannot prove that this call is still in the same
            // turn, nor that Ready activation is unnecessary. Recheck before
            // every activatable operation; reuse the fresh snapshot for guidance.
            cache.Invalidate(projectId, agentId)
            let current =
                cache.Get(DateTimeOffset.UtcNow, projectId, agentId, fun () ->
                    match OrchestrationApi.LedgerBindingConfig.load project rt with
                    | Error _ -> None
                    | Ok binding ->
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Ok agent -> Some agent
                        | Error _ -> None)
            match current with
            | Some agent when agent.State = "Ready"
                              && not (isNull (box agent.CurrentAssignment))
                              && not (String.IsNullOrWhiteSpace agent.CurrentAssignment.TaskId) ->
                match OrchestrationApi.LedgerBindingConfig.load project rt with
                | Error error -> Error(Client.ValidationError($"Cannot activate Ready agent: {error}"))
                | Ok binding ->
                    let result =
                        ReviewWorkflow.ReviewWorkflow.activateReadyAgent binding (Guid.NewGuid())
                            agent.CurrentAssignment.TaskId agentId
                    cache.Invalidate(projectId, agentId)
                    match result with
                    | Ok _ -> Ok ()
                    | Error(Client.LedgerStore.StaleEntityVersion _) ->
                        // Another concurrent tool may already have started this
                        // assignment. Do not reset its Working clock.
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Ok latest when latest.State = "Working" -> Ok ()
                        | _ -> Error(Client.ValidationError "Agent assignment changed before automatic activation.")
                    | Error error -> Error(Client.ValidationError($"Automatic agent activation failed: {error}"))
            | _ -> Ok ()

// Only the Working turn identifies a stable delivery scope. Ledger calls bootstrap
// root guidance, while normal source operations discover deeper instructions.
let private incrementalInstructions (rt: Runtime) (agentId: string)
                                    (target: ProjectPaths.ProjectTarget option)
                                    provenance documents =
    if String.IsNullOrWhiteSpace agentId then documents
    else
        match target with
        | None -> documents
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            let now = DateTimeOffset.UtcNow
            let agent =
                cache.Get(now, projectId, agentId, fun () ->
                    match OrchestrationApi.LedgerBindingConfig.load project rt with
                    | Error _ -> None
                    | Ok binding ->
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Ok found -> Some found
                        | Error _ -> None)
            match agent with
            | Some active when active.State = "Working" ->
                let turn = string active.StateSequence + ":" + active.WorkingTurnStartedAtUtc
                let delivery = instructionDeliveryCaches.GetValue(rt, fun _ -> AgentInstructions.DeliveryCache())
                delivery.Filter(target, agentId, turn, provenance, documents, fun () ->
                    // Ledger status reads remain usable; failed instruction
                    // resolution is surfaced as a retryable warning.
                    AgentInstructions.rootsForTarget rt target)
            | _ -> documents

let private serialize<'a> (value: 'a) =
    try
        JsonSerializer.Serialize value |> Ok
    with e ->
        e |> ExceptionError |> Error

let private serialize'<'a> = Result.bind serialize<'a> >> ValueTask<_>

let private toAgentResponse command instructions response =
    match response with
    | Error error ->
        { Result = None
          Error = Some(EffectError.toAgentError error)
          Instructions = instructions }
    | Ok (payload: string) ->
        let bytes = Encoding.UTF8.GetByteCount payload +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize instructions)
        let maxBytes =
            match command with
            | ReadImageCommand _ -> (AgentProtocol.maxImageBytes * 4 / 3) + (64 * 1024)
            | ExportFileCommand _ -> (AgentProtocol.maxExportBytes * 4 / 3) + (64 * 1024)
            | _ -> AgentProtocol.maxResponseBytes

        if bytes <= maxBytes then
            { Result = Some payload
              Error = None
              Instructions = instructions }
        else
            { Result = None
              Error = Some(OutputTruncated $"Command response was {bytes} bytes and exceeds the safe response size of {maxBytes} bytes.")
              Instructions = instructions }

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
    | UpdateClientCommand -> Some("UpdateClient", None, [ ProcessExecution; NetworkAccess; Destructive ], [], None, [])
    | WriteFileCommand cmd -> Some("WriteFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | PatchFileCommand cmd -> Some("PatchFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | RunCommandCommand cmd -> Some("RunCommand", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | RunProjectTaskCommand cmd -> Some("RunProjectTask", Some cmd.ProjectName, [ ProcessExecution ], [], None, [ cmd.TaskName ])
    | CreateSkillCommand cmd -> Some("CreateSkill", Some cmd.ProjectName, [ WorkspaceWrite ], [ $".agents/skills/{cmd.SkillName}/SKILL.md" ], None, [])
    | RemoveWorktreeCommand cmd -> Some("RemoveWorktree", Some cmd.ProjectName, [ VersionControlWrite; Destructive ], [], Some "git", [ cmd.WorktreePath ])
    | TaskWriteCommand cmd -> Some("Task" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.TaskId; cmd.AttemptId; cmd.OperationId ])
    | LedgerWriteCommand cmd -> Some("Ledger" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.RemoteName; cmd.ExpectedTrustedHead; cmd.OperationId ])
    | ReviewWriteCommand cmd -> Some("Review" + cmd.Action, Some cmd.ProjectName, [ WorkspaceWrite; VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.TaskId; cmd.RequestId; cmd.OperationId ])
    | CreateProjectCommand cmd ->
        Some("CreateProject", None, [ WorkspaceWrite ] @ (if cmd.InitializeGit then [ VersionControlWrite ] else []) @ (if cmd.InitializeLedger then [ NetworkAccess ] else []),
             [ cmd.Name ], (if cmd.InitializeGit then Some "git" else None), [ cmd.RemoteUrl ])
    | CreateWorktreeCommand cmd -> Some("CreateWorktree", Some cmd.ProjectName, [ VersionControlWrite ], [], Some "git", [ cmd.Branch; cmd.Name ])
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

let private importFile (resolved: ProjectPaths.ResolvedProject option) (cmd: ImportFileCommand) (rt: Runtime) =
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
            let destination =
                match resolved with
                | Some project -> fun rt -> ProjectPaths.resolveSourcePath project cmd.FilePath rt |> Result.map FilePath
                | None -> ProjectPaths.resolveWritableProjectFile cmd.ProjectName cmd.FilePath
            match destination rt with
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

let private sourceOperation resolved withContext legacy =
    match resolved with
    | Some project -> withContext project
    | None -> legacy()

let private dispatch (rt: Runtime) (target: ProjectPaths.ProjectTarget option)
                     (resolved: ProjectPaths.ResolvedProject option) (command: AgentCommand) =
    match command with
    | ListCommandsCommand -> rt |> ProjectBrowser.listCommands |> serialize'
    | ListWorktreesCommand cmd -> rt |> Worktrees.listWorktrees cmd |> serialize'
    | GetWorktreeCommand cmd -> rt |> Worktrees.getWorktree cmd |> serialize'
    | CreateWorktreeCommand cmd -> rt |> Worktrees.createWorktree cmd |> serialize'
    | InspectWorktreeRemovalCommand cmd -> rt |> Worktrees.inspectRemoval cmd |> serialize'
    | RemoveWorktreeCommand cmd -> rt |> Worktrees.removeWorktree cmd |> serialize'
    | TaskReadCommand cmd -> rt |> OrchestrationApi.TaskApi.read cmd |> serialize'
    | TaskWriteCommand cmd when cmd.Action = "ExportTaskSpec" || cmd.Action = "ImportTaskSpec" ->
        match resolved with
        | Some context -> rt |> OrchestrationApi.TaskApi.writeForContext context cmd |> serialize'
        | None -> Error(Client.ValidationError "Task-spec source checkout was not resolved.") |> serialize'
    | TaskWriteCommand cmd -> rt |> OrchestrationApi.TaskApi.write cmd |> serialize'
    | LedgerReadCommand cmd -> rt |> OrchestrationApi.LedgerApi.read cmd |> serialize'
    | LedgerWriteCommand cmd -> rt |> OrchestrationApi.LedgerApi.write cmd |> serialize'
    | ReviewReadCommand cmd -> rt |> OrchestrationApi.ReviewApi.read cmd |> serialize'
    | ReviewWriteCommand cmd -> rt |> OrchestrationApi.ReviewApi.write cmd |> serialize'
    | CreateProjectCommand cmd -> rt |> ProjectCreation.create cmd |> serialize'
    | ListProjectsCommand ->
        rt
        |> ProjectBrowser.listProjects
        |> Result.map (Seq.map unwrapProjectName >> Seq.toList)
        |> serialize'
    | GetProjectDetailsCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.getProjectDetailsIn project cmd) (fun () -> ProjectBrowser.getProjectDetails cmd)
        |> Result.map (List.map (fun (name, content) -> {| fileName = name; content = unwrapContent content |}))
        |> serialize'
    | ListDirectoryCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.listDirectoryIn project cmd) (fun () -> ProjectBrowser.listDirectory cmd) |> serialize'
    | SearchFilesCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.searchFilesIn project cmd) (fun () -> ProjectBrowser.searchFiles cmd) |> serialize'
    | SearchTextCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.searchTextIn project cmd) (fun () -> ProjectBrowser.searchText cmd) |> serialize'
    | ReadFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readFileIn project cmd) (fun () -> ProjectBrowser.readFile cmd)
        |> Result.map unwrapContent
        |> serialize'
    | ReadFilesCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readFilesIn project cmd) (fun () -> ProjectBrowser.readFiles cmd)
        |> Result.map (Seq.zip cmd.FilePaths >> Seq.map (fun (path, result) -> readFileResultToDto path result) >> Seq.toList)
        |> serialize'
    | GetAgentInstructionsCommand cmd ->
        match target with
        | Some selected -> rt |> AgentInstructions.getExplicit selected cmd |> serialize'
        | None -> Error(Client.ValidationError "A validated project checkout is required.") |> serialize'
    | ReadImageCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.readImageIn project cmd) (fun () -> ProjectBrowser.readImage cmd)
        |> serialize'
    | ExportFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.exportFileIn project cmd) (fun () -> ProjectBrowser.exportFile cmd)
        |> serialize'
    | ImportFileCommand cmd -> rt |> importFile resolved cmd |> serialize'
    | UpdateClientCommand -> SelfUpdate.prepare () |> Async.AwaitTask |> Async.RunSynchronously |> serialize'
    | WriteFileCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.writeFileIn project cmd) (fun () -> ProjectBrowser.writeFile cmd) |> serialize'
    | PatchFileCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ProjectBrowser.patchFileIn project cmd) (fun () -> ProjectBrowser.patchFile cmd)
        |> serialize'
    | RunCommandCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.runCommandIn project cmd) (fun () -> ClientShell.runCommand cmd) |> serialize'
    | ListConfiguredTasksCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.listConfiguredTasksIn project cmd) (fun () -> ClientShell.listConfiguredTasks cmd) |> serialize'
    | RunProjectTaskCommand cmd ->
        rt
        |> sourceOperation resolved (fun project -> ClientShell.resolveProjectTaskIn project cmd) (fun () -> ClientShell.resolveProjectTask cmd)
        |> Result.bind (fun task -> (sourceOperation resolved (fun project -> ClientShell.runProjectTaskIn project task) (fun () -> ClientShell.runProjectTask cmd.ProjectName task)) rt)
        |> serialize'
    | ListSkillsCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.listSkillsIn project cmd) (fun () -> ProjectBrowser.listSkills cmd) |> serialize'
    | GetSkillCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.getSkillIn project cmd) (fun () -> ProjectBrowser.getSkill cmd) |> serialize'
    | CreateSkillCommand cmd -> rt |> sourceOperation resolved (fun project -> ProjectBrowser.createSkillIn project cmd) (fun () -> ProjectBrowser.createSkill cmd) |> serialize'
    | GetGitStatusCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.getGitStatusIn project cmd) (fun () -> ClientShell.getGitStatus cmd) |> serialize'
    | GetGitDiffCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.getGitDiffIn project cmd) (fun () -> ClientShell.getGitDiff cmd) |> serialize'
    | GitCommitCommand cmd -> rt |> sourceOperation resolved (fun project -> ClientShell.gitCommitIn project cmd) (fun () -> ClientShell.gitCommit cmd) |> serialize'
    | StartJobCommand cmd ->
        match resolved, target with
        | Some project, _ -> rt |> JobManager.startJobIn project cmd |> serialize'
        | None, Some validated -> rt |> JobManager.startJobForTarget validated cmd |> serialize'
        | None, None -> rt |> JobManager.startJob cmd |> serialize'
    | ListJobsCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.listJobsForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.listJobs cmd |> serialize'
    | GetJobResultCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.getJobResultForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.getJobResult cmd |> serialize'
    | CancelJobCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.cancelJobForProject (Some(ProjectPaths.ProjectTarget.projectId validated)) cmd |> serialize'
        | None -> rt |> JobManager.cancelJob cmd |> serialize'
    | MessageCommand cmd ->
        if String.IsNullOrWhiteSpace cmd.Message then
            Error(Client.ValidationError "Message cannot be empty.") |> serialize'
        elif cmd.Message.Length > 1000 then
            Error(Client.ValidationError "Message cannot exceed 1000 characters.") |> serialize'
        else
            rt.Tui.Message(cmd.ProjectName, cmd.Message)
            Ok() |> serialize'
    | GetClientActivityCommand cmd ->
        rt.Tui.GetActivitySnapshot(cmd.ProjectName, cmd.Limit, includeObservers = defaultArg cmd.IncludeObservers false)
        |> Ok
        |> serialize'

let private receiveCommandForTarget (rt: Runtime) (target: ProjectPaths.ProjectTarget option)
                                    (resolved: ProjectPaths.ResolvedProject option)
                                    (preparedTask: ProjectTaskDefinition option) (command: AgentCommand) =
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
                (match preparedTask with
                 | Some task -> Ok task
                 | None ->
                     (sourceOperation resolved
                         (fun project -> ClientShell.resolveProjectTaskIn project cmd)
                         (fun () -> ClientShell.resolveProjectTask cmd)) rt)
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
                        let! result = (sourceOperation resolved
                            (fun project -> ClientShell.runProjectTaskIn project task)
                            (fun () -> ClientShell.runProjectTask cmd.ProjectName task)) rt |> serialize'
                        return result
                    | Ok(), None ->
                        let! result = dispatch rt target resolved command
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

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    receiveCommandForTarget rt None None None command

let private receiveCommandWithTypedInstructions (rt: Runtime)
                                                (target: ProjectPaths.ProjectTarget option) command =
    task {
        match AgentInstructions.prepareOperationWithTarget rt target command with
        | Error error ->
            return Error error, [], AgentInstructions.RootBootstrapRequired
        | Ok prepared ->
            let! response =
                receiveCommandForTarget rt target prepared.Source prepared.ProjectTask command
            let provenance =
                if prepared.Source.IsSome then AgentInstructions.HierarchyLoaded
                else AgentInstructions.RootBootstrapRequired
            return response,
                   prepared.Instructions
                   @ AgentInstructions.writeWarningsForContext rt prepared.Source command response,
                   provenance
    }

let receiveCommandWithInstructions (rt: Runtime) command =
    task {
        match classifyProject command with
        | Error error -> return Error error, []
        | Ok scope ->
            let! response, instructions, _ =
                receiveCommandWithTypedInstructions rt (projectTarget scope) command
            return response, instructions
    }

// Shared by SignalR receive and tests. Identity is supplied by the MCP call,
// not inferred from provider conversation metadata.
let processIncomingCommand (rt: Runtime) (commandJson: string) (agentId: string) =
    task {
        let mutable parsedCommand = None
        let mutable instructions: InstructionDocument list = []
        let! response =
            task {
                try
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    parsedCommand <- Some command
                    let project = classifyProject command
                    match project with
                    | Error error -> return Error error
                    | Ok scope ->
                        let target = projectTarget scope
                        target |> Option.iter (fun selected ->
                            rt.Tui.RecordAgentSeen(AgentView.activityProject selected, agentId))
                        match activateReadyOnActivity rt agentId target command with
                        | Error error -> return Error error
                        | Ok () ->
                            let! result, applicable, provenance =
                                receiveCommandWithTypedInstructions rt target command
                            instructions <-
                                match command with
                                | GetAgentInstructionsCommand _ ->
                                    // The explicit result includes the complete hierarchy.
                                    // Do not double-inject automatic AGENTS.md payloads.
                                    checkWorkingGuidance rt agentId target command
                                | _ ->
                                    incrementalInstructions rt agentId target provenance applicable
                                    @ checkWorkingGuidance rt agentId target command
                            return result
                with ex ->
                    return Error(ExceptionError ex)
            }
        let agentResponse =
            match parsedCommand with
            | Some command -> toAgentResponse command instructions response
            | None -> toAgentResponse ListCommandsCommand [] response
        return parsedCommand, response, agentResponse
    }

let receiveCommandAndReply (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) (agentId: string) : Task =
    task {
        let! parsedCommand, response, agentResponse = processIncomingCommand rt commandJson agentId
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

        match sendResult, parsedCommand, response with
        | Ok(), Some UpdateClientCommand, Ok _ ->
            rt.Tui.Log "Update verified and installed; restarting Jarvis in this terminal."
            match SelfUpdate.activatePending () with
            | Ok _ -> ()
            | Error error -> rt.Tui.Log $"Self-update activation failed: {EffectError.toString error}"
        | _ -> ()
    }
