module Client.SignalR.Client

open System
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
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
let invalidateAgentStates (rt: Runtime) (projectName: string) (agentIds: string array) =
    // Server notifications only carry the logical project identity. Worktree
    // selectors share the canonical ledger/assignment cache by ProjectId.
    match ProjectPaths.ProjectTarget.parse projectName with
    | Ok target ->
        let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
            WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
        if not (isNull agentIds) then
            let projectId = ProjectPaths.ProjectTarget.projectId target
            for agentId in agentIds |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.distinct do
                cache.Invalidate(projectId, agentId)
    | Error _ -> ()

let invalidateAllAgentStates (rt: Runtime) =
    // Reconnection can follow missed updates from other devices.
    let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
        WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
    cache.InvalidateAll()
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


let private loadAgentState (rt: Runtime) (project: string) (agentId: string) =
    match OrchestrationApi.LedgerBindingConfig.loadWithSnapshot project rt with
    | Error _ -> None
    | Ok (binding, snapshot) ->
        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurnAt binding snapshot agentId with
        | Ok agent -> Some agent
        | Error _ -> None

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
            let agent =
                cache.Get(now, projectId, agentId, fun () ->
                    loadAgentState rt project agentId)
            match agent |> Option.bind (WorkingTurnGuidance.instructionAt now) with
            | Some content ->
                [ { Scope = "working-turn"; Path = "jarvis/working-turn"
                    Sha256 = ""; Content = content } ]
            | None -> []

// A cross-project notification is sent using the *source* agent identity,
// which is not an identity of an agent in the destination project. Routing
// health probes likewise do not represent agent activity. Keep both out of
// destination last-seen, turn-guidance and implicit-activation handling.
let private isLocalAgentActivity = function
    | TaskWriteCommand cmd when cmd.Action = "NotifyAgent"
                                && not (String.IsNullOrWhiteSpace cmd.SourceRef)
                                && cmd.SourceRef <> cmd.ProjectName -> false
    | LedgerReadCommand cmd when cmd.Action = "Health" -> false
    | ObserveAgentCommand _ -> false // Neither observer nor target is acting.
    | _ -> true

// An agent's first ordinary tool call starts its Ready assignment.
// No new session identifier or explicit resume tool is needed. Never
// auto-activate a different agent merely because an administrative operation
// names that agent as its target.
let private activatesAgent (agentId: string) command =
    match command with
    | _ when not (isLocalAgentActivity command) -> false
    | TaskWriteCommand cmd ->
        cmd.ActorId = agentId
        && List.contains cmd.Action
            [ "CheckpointAttempt"; "YieldAgent"; "CompleteAgentWork"
              "AcknowledgeFeedback"; "AcknowledgeNotification"; "NotifyAgent"
              "BindAttemptSource"; "RebindAttemptSource"; "BindReviewBase"
              "SetAttemptSource"; "SubmitIntegration"; "CompleteIntegration" ]
    | TaskReadCommand cmd ->
        String.IsNullOrWhiteSpace cmd.AgentId || cmd.AgentId = agentId
    | LedgerWriteCommand _ | ReviewWriteCommand _
    | RetryPendingIntentCommand _ | SupersedePendingIntentCommand _
    | AbandonPendingIntentCommand _ -> false
    | _ -> true

let private activateReadyOnActivity (rt: Runtime) (agentId: string)
                                (target: ProjectPaths.ProjectTarget option) command =
    if String.IsNullOrWhiteSpace agentId || not (activatesAgent agentId command) then Ok ([], None)
    else
        match target with
        | None -> Ok ([], None)
        | Some target ->
            let projectId = ProjectPaths.ProjectTarget.projectId target
            let project = ProjectPaths.ProjectId.value projectId
            let cache = workingGuidanceCaches.GetValue(rt, fun _ -> WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
            // The server broadcasts invalidation after successful ledger writes
            // on any connected device; the inbox also forces authoritative refresh.
            // Keep the 2-minute fallback for changes outside Jarvis and missed
            // notifications. Do not fetch from GitHub for every ordinary source call.
            match command with
            | TaskReadCommand cmd when cmd.Action = "AgentInbox" -> cache.Invalidate(projectId, agentId)
            | _ -> ()
            let current =
                cache.Get(DateTimeOffset.UtcNow, projectId, agentId, fun () ->
                    loadAgentState rt project agentId)
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
                    | Ok write when write.AlreadyApplied -> Ok ([], None)
                    | Ok write ->
                        // Only a successfully committed Ready-to-Working write
                        // provides evidence of a transition performed here.
                        let committed : AgentInboxActivation =
                            { ProjectName = project
                              AgentId = agentId
                              ActivatedByThisCall = true
                              BeforeState = Some "Ready"
                              AfterState = Some "Working"
                              CommittedHead = Some write.CurrentHead
                              OperationId = Some write.Operation.OperationId }
                        Ok (Client.LedgerStore.LedgerStore.changedAgentIds write, Some committed)
                    | Error(Client.LedgerStore.StaleEntityVersion _) ->
                        // Another concurrent tool may already have started this
                        // assignment. Do not reset its Working clock.
                        match ReviewWorkflow.ReviewWorkflow.getAgentWorkingTurn binding agentId with
                        | Ok latest when latest.State = "Working" -> Ok ([], None)
                        | _ -> Error(Client.ValidationError "Agent assignment changed before automatic activation.")
                    | Error error -> Error(Client.ValidationError($"Automatic agent activation failed: {error}"))
            | _ -> Ok ([], None)

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
                    loadAgentState rt project agentId)
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

type private DispatchedResult =
    { Payload: string
      ChangedAgentIds: string list }

let private serialize'<'a> (result: Result<'a, Client.EffectError>) =
    result
    |> Result.bind (fun value ->
        let changedAgentIds =
            match box value with
            | :? OrchestrationApi.OperationResponse.Presented as operation ->
                operation.changedAgentIds
            | _ -> []
        serialize value
        |> Result.map (fun payload ->
            { Payload = payload; ChangedAgentIds = changedAgentIds }))
    |> ValueTask<_>

let private toAgentResponse command instructions stateChanges inboxActivation response =
    match response with
    | Error error ->
        { Result = None
          Error = Some(EffectError.toAgentError error)
          Instructions = instructions
          AgentStateChanges = stateChanges
          InboxActivation = inboxActivation
          PendingJob = None }
    | Ok (dispatched: DispatchedResult) ->
        let payload = dispatched.Payload
        let bytes = Encoding.UTF8.GetByteCount payload +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize instructions) +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize stateChanges) +
                    Encoding.UTF8.GetByteCount(JsonSerializer.Serialize inboxActivation)
        let maxBytes =
            match command with
            | ReadImageCommand _ -> (AgentProtocol.maxImageBytes * 4 / 3) + (64 * 1024)
            | ExportFileCommand _ -> (AgentProtocol.maxExportBytes * 4 / 3) + (64 * 1024)
            // Tool responses are embedded as serialized strings inside
            // JobResult. Keep the original media/export allowance plus safe
            // JSON escaping overhead for this one typed polling envelope.
            | GetJobResultCommand _ -> (AgentProtocol.maxImageBytes * 4 / 3 * 2) + (128 * 1024)
            | _ -> AgentProtocol.maxResponseBytes

        if bytes <= maxBytes then
            { Result = Some payload
              Error = None
              Instructions = instructions
              AgentStateChanges = stateChanges
              InboxActivation = inboxActivation
              PendingJob = None }
        else
            { Result = None
              Error = Some(OutputTruncated $"Command response was {bytes} bytes and exceeds the safe response size of {maxBytes} bytes.")
              Instructions = instructions
              AgentStateChanges = stateChanges
              InboxActivation = inboxActivation
              PendingJob = None }

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
    | ScheduleBrowserExecutionCommand cmd -> Some("ScheduleBrowserExecution", Some cmd.ProjectName, [ ProcessExecution; NetworkAccess ], [cmd.ScriptPath], None, [])
    | CancelScheduledBrowserExecutionCommand _ -> Some("CancelScheduledBrowserExecution", None, [ ProcessExecution ], [], None, [])
    | BrowserOpenTabCommand _ -> Some("BrowserOpenTab", None, [ ProcessExecution; NetworkAccess ], [], None, [])
    | BrowserRunInNewTabCommand cmd -> Some("BrowserRunInNewTab", Some cmd.ProjectName, [ ProcessExecution; NetworkAccess ], [cmd.ScriptPath], None, [])
    | BrowserCloseTabCommand _ -> Some("BrowserCloseTab", None, [ ProcessExecution ], [], None, [])
    | BrowserExecuteFileCommand cmd -> Some("BrowserExecuteFile", Some cmd.ProjectName, [ ProcessExecution; NetworkAccess ], [cmd.ScriptPath], None, [])
    | BrowserExecuteJsCommand cmd -> Some("BrowserExecuteJs", None, [ ProcessExecution; NetworkAccess ], [], None, [])
    | BrowserCaptureDomCommand _ -> Some("BrowserCaptureDom", None, [ ProcessExecution; NetworkAccess ], [], None, [])
    | BrowserExportDomCommand cmd -> Some("BrowserExportDom", Some cmd.ProjectName, [ ProcessExecution; NetworkAccess; WorkspaceWrite ], [cmd.FilePath], None, [])
    | WriteFileCommand cmd -> Some("WriteFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | PatchFileCommand cmd -> Some("PatchFile", Some cmd.ProjectName, [ WorkspaceWrite ], [ cmd.FilePath ], None, [])
    | RunCommandCommand cmd -> Some("RunCommand", Some cmd.ProjectName, [ ProcessExecution ], [], Some cmd.Executable, cmd.Args)
    | RunProjectTaskCommand cmd -> Some("RunProjectTask", Some cmd.ProjectName, [ ProcessExecution ], [], None, [ cmd.TaskName ])
    | CreateSkillCommand cmd -> Some("CreateSkill", Some cmd.ProjectName, [ WorkspaceWrite ], [ $".agents/skills/{cmd.SkillName}/SKILL.md" ], None, [])
    | RemoveWorktreeCommand cmd -> Some("RemoveWorktree", Some cmd.ProjectName, [ VersionControlWrite; Destructive ], [], Some "git", [ cmd.WorktreePath ])
    | TaskWriteCommand cmd -> Some("Task" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.TaskId; cmd.AttemptId; cmd.OperationId ])
    | LedgerWriteCommand cmd -> Some("Ledger" + cmd.Action, Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ], [], Some "git", [ cmd.RemoteName; cmd.ExpectedTrustedHead; cmd.OperationId ])
    | RetryPendingIntentCommand cmd ->
        Some("RetryPendingIntent", Some cmd.ProjectName, [ VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.ExpectedVerifiedHead ])
    | SupersedePendingIntentCommand cmd ->
        Some("SupersedePendingIntent", Some cmd.ProjectName,
             [ WorkspaceWrite; VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.RelatedOperationId; cmd.Reason ])
    | AbandonPendingIntentCommand cmd ->
        Some("AbandonPendingIntent", Some cmd.ProjectName,
             [ WorkspaceWrite; VersionControlWrite; NetworkAccess ],
             [], Some "git", [ cmd.OperationId; cmd.ExpectedCommandSha256; cmd.Reason ])
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
        match ImportNetwork.parseUrl cmd.DownloadUrl with
        | Error failure -> Error failure
        | Ok _ ->
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

                let result =
                    try
                        if File.Exists destinationPath && not cmd.Overwrite then
                            Error(Client.ValidationError $"Destination file already exists: {cmd.FilePath}")
                        else
                            if cmd.CreateParents then
                                Directory.CreateDirectory(parentPath) |> ignore
                            elif not (Directory.Exists parentPath) then
                                raise (DirectoryNotFoundException $"Parent directory does not exist: {Path.GetDirectoryName(cmd.FilePath)}")

                            // Automatic trust under PartialTrust is restricted to
                            // provider file hosts throughout the redirect chain.
                            // Generic HTTPS imports require explicit local approval,
                            // or the operator's existing FullTrust setting.
                            let permission = rt :> PermissionIO
                            let redirectTrust =
                                if permission.TrustLevel = PartialTrust
                                    && ImportNetwork.isKnownAttachment cmd.DownloadUrl then
                                    ImportNetwork.ProviderOriginsOnly
                                else ImportNetwork.AnyPublicHttps

                            let result =
                                ImportNetwork.downloadTo
                                    (ImportNetwork.sharedClient())
                                    redirectTrust cmd.DownloadUrl tempPath effectiveLimit
                                    Threading.CancellationToken.None
                                |> fun task -> task.GetAwaiter().GetResult()

                            match result with
                            | Error failure -> Error failure
                            | Ok receipt ->
                                // The checkout can change during a remote download.
                                // Revalidate source containment before publication.
                                match destination rt with
                                | Error error -> Error error
                                | Ok(FilePath revalidated) when revalidated <> destinationPath ->
                                    Error(Client.ValidationError "Import destination changed during download.")
                                | Ok _ ->
                                    File.Move(tempPath, destinationPath, cmd.Overwrite)
                                    Ok
                                        { FilePath = cmd.FilePath
                                          FileId = cmd.FileId
                                          BytesWritten = receipt.BytesWritten
                                          MimeType = cmd.MimeType
                                          Sha256 = receipt.Sha256 }
                    with
                    | :? IOException ->
                        Error(Client.ValidationError "Unable to publish imported file.")
                    | ex -> Error(Client.ExceptionError ex)
                if File.Exists tempPath then
                    try File.Delete tempPath with _ -> ()
                result

let private sourceOperation resolved withContext legacy =
    match resolved with
    | Some project -> withContext project
    | None -> legacy()

// Browser actions return bounded serialized JSON; adapt their transport result
// to the current command-dispatch contract without inventing agent state changes.
let private fromBrowser (operation: ValueTask<Result<string, EffectError>>) =
    ValueTask<Result<DispatchedResult, EffectError>>(task {
        let! result = operation
        return result |> Result.map (fun payload ->
            { Payload = payload; ChangedAgentIds = [] })
    })

let private dispatch (rt: Runtime) (target: ProjectPaths.ProjectTarget option)
                     (resolved: ProjectPaths.ResolvedProject option) (agentId: string) (command: AgentCommand) =
    match command with
    | ListCommandsCommand -> rt |> ProjectBrowser.listCommands |> serialize'
    | BrowserListWindowsCommand ->
        fromBrowser (
            match rt.Browser with
            | None -> ValueTask<Result<string,EffectError>>(Error(Client.ValidationError "BridgeDisabled"))
            | Some bridge ->
                ValueTask<Result<string,EffectError>>(bridge.ListWindows() |> Result.mapError Client.ValidationError)
        )
    | BrowserListTabsCommand ->
        fromBrowser (
            match rt.Browser with
            | None -> ValueTask<Result<string,EffectError>>(Error(Client.ValidationError "BridgeDisabled"))
            | Some bridge ->
                let result = bridge.ListTabs() |> Result.mapError Client.ValidationError
                ValueTask<Result<string,EffectError>>(result)
        )
    | ListScheduledBrowserExecutionsCommand ->
        fromBrowser (
            match rt.BrowserScheduler with
            | None -> ValueTask<Result<string,EffectError>>(Error(Client.ValidationError "BridgeDisabled"))
            | Some scheduler -> ValueTask<Result<string,EffectError>>(Ok(scheduler.List()))
        )
    | CancelScheduledBrowserExecutionCommand cmd ->
        fromBrowser (
            match rt.BrowserScheduler with
            | None -> ValueTask<Result<string,EffectError>>(Error(Client.ValidationError "BridgeDisabled"))
            | Some scheduler ->
                let result =
                    scheduler.Cancel(cmd.ScheduleId)
                    |> Result.map (fun () -> """{"canceled":true}""")
                    |> Result.mapError Client.ValidationError
                ValueTask<Result<string,EffectError>>(result)
        )
    | ScheduleBrowserExecutionCommand cmd ->
        fromBrowser (
            let result =
                match rt.BrowserScheduler with
                | None -> Error(Client.ValidationError "BridgeDisabled")
                | Some scheduler ->
                    match ProjectPaths.validateProjectRelativePath cmd.ScriptPath with
                    | Error error -> Error error
                    | Ok _ when not (cmd.ScriptPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) ->
                        Error(Client.ValidationError "Browser script must be a .js file.")
                    | Ok _ ->
                        match ProjectPaths.resolveProjectFile cmd.ProjectName cmd.ScriptPath rt with
                        | Error error -> Error error
                        | Ok(FilePath filename) ->
                            try
                                if FileInfo(filename).Length > int64 BrowserBridge.MaxScriptBytes then
                                    Error(Client.ValidationError "InputTooLarge: script exceeds 64 KiB.")
                                else
                                    let date = scheduler.Schedule(cmd.ScheduleId, cmd.RunAfterSeconds, cmd.Url,
                                                    cmd.ProjectName, cmd.ScriptPath, defaultArg cmd.Alias "",
                                                    defaultArg cmd.ArgsJson "{}", defaultArg cmd.Active false,
                                                    ?windowRegistration = cmd.WindowRegistration,
                                                    ?closeAfter = cmd.CloseTabAfterExecution,
                                                    ?repeatEverySeconds = cmd.RepeatEverySeconds)
                                    date
                                    |> Result.map (fun due -> JsonSerializer.Serialize({| scheduleId = cmd.ScheduleId; dueAtUtc = due; status = "pending" |}))
                                    |> Result.mapError Client.ValidationError
                            with ex -> Error(Client.ExceptionError ex)
            ValueTask<Result<string,EffectError>>(result)
        )
    | BrowserOpenTabCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    let! result = bridge.OpenTab(cmd.WindowRegistration, cmd.Url, defaultArg cmd.Alias "", defaultArg cmd.Active false)
                    return result |> Result.mapError Client.ValidationError
            })
        )
    | BrowserCloseTabCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    let! result = bridge.CloseTab(cmd.TabRegistration)
                    return result |> Result.mapError Client.ValidationError
            })
        )
    | BrowserRunInNewTabCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    match ProjectPaths.validateProjectRelativePath cmd.ScriptPath with
                    | Error e -> return Error e
                    | Ok _ when not (cmd.ScriptPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) ->
                        return Error(Client.ValidationError "Browser script must be a .js file.")
                    | Ok _ ->
                        match ProjectPaths.resolveProjectFile cmd.ProjectName cmd.ScriptPath rt with
                        | Error e -> return Error e
                        | Ok(FilePath path) ->
                            try
                                if FileInfo(path).Length > int64 BrowserBridge.MaxScriptBytes then
                                    return Error(Client.ValidationError "InputTooLarge: script exceeds 64 KiB.")
                                else
                                    let source = File.ReadAllText(path, UTF8Encoding(false, true))
                                    let args = defaultArg cmd.ArgsJson "{}"
                                    use validatedArgs = JsonDocument.Parse(args)
                                    let! result = BrowserBridge.runInNewTab bridge cmd.WindowRegistration cmd.Url "" (defaultArg cmd.Active false) source args (defaultArg cmd.TimeoutMs 30000) (defaultArg cmd.CloseTabAfterExecution false)
                                    return result
                                           |> Result.mapError (fun (registrationId,error) ->
                                               let detail =
                                                   if String.IsNullOrWhiteSpace(registrationId) then error
                                                   else error + " [tabRegistration=" + registrationId + "]"
                                               Client.ValidationError detail)
                            with
                            | :? DecoderFallbackException -> return Error(Client.ValidationError "InvalidUtf8")
                            | :? JsonException -> return Error(Client.ValidationError "InvalidArgumentsJson")
                            | :? FileNotFoundException -> return Error(Client.NotFoundError "ScriptNotFound")
                            | ex -> return Error(Client.ExceptionError ex)
            })
        )
    | BrowserExecuteFileCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    match ProjectPaths.validateProjectRelativePath cmd.ScriptPath with
                    | Error error -> return Error error
                    | Ok _ when not (cmd.ScriptPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) ->
                        return Error(Client.ValidationError "Browser script must be a .js file.")
                    | Ok _ ->
                        match ProjectPaths.resolveProjectFile cmd.ProjectName cmd.ScriptPath rt with
                        | Error error -> return Error error
                        | Ok(FilePath path) ->
                            try
                                let info = FileInfo(path)
                                if info.Length > int64 BrowserBridge.MaxScriptBytes then
                                    return Error(Client.ValidationError "InputTooLarge: script exceeds 64 KiB.")
                                else
                                    let source = File.ReadAllText(path, UTF8Encoding(false, true))
                                    let! outcome = bridge.Execute(cmd.TabRegistration, source, defaultArg cmd.ArgsJson "{}", defaultArg cmd.TimeoutMs 15000, false)
                                    return outcome |> Result.mapError Client.ValidationError
                            with
                            | :? DecoderFallbackException ->
                                return Error(Client.ValidationError "InvalidUtf8")
                            | :? FileNotFoundException ->
                                return Error(Client.NotFoundError "ScriptNotFound")
                            | ex -> return Error(Client.ExceptionError ex)
            })
        )
    | BrowserCaptureDomCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    let! result = bridge.CaptureDom(cmd.TabRegistration,
                        defaultArg cmd.WaitJson """{"kind":"immediate"}""",
                        defaultArg cmd.CaptureSelector "", defaultArg cmd.TimeoutMs 30000, None)
                    return result |> Result.mapError Client.ValidationError
            })
        )
    | BrowserExportDomCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    match ProjectPaths.resolveWritableProjectFile cmd.ProjectName cmd.FilePath rt with
                    | Error e -> return Error e
                    | Ok(FilePath destination) ->
                        let! result = bridge.CaptureDom(cmd.TabRegistration,
                            defaultArg cmd.WaitJson """{"kind":"immediate"}""",
                            defaultArg cmd.CaptureSelector "", defaultArg cmd.TimeoutMs 30000, Some destination)
                        match result with
                        | Error reason -> return Error(Client.ValidationError reason)
                        | Ok json ->
                            use snapshot = JsonDocument.Parse(json)
                            return Ok(JsonSerializer.Serialize(
                                {| projectName = cmd.ProjectName; filePath = cmd.FilePath
                                   snapshot = snapshot.RootElement.Clone() |}))
            })
        )
    | BrowserExecuteJsCommand cmd ->
        fromBrowser (
            ValueTask<Result<string,EffectError>>(task {
                match rt.Browser with
                | None -> return Error(Client.ValidationError "BridgeDisabled")
                | Some bridge ->
                    let! outcome = bridge.Execute(cmd.TabRegistration, cmd.Code, defaultArg cmd.ArgsJson "{}", defaultArg cmd.TimeoutMs 15000, true)
                    return outcome |> Result.mapError Client.ValidationError
            })
        )
    | ListWorktreesCommand cmd -> rt |> Worktrees.listWorktrees cmd |> serialize'
    | GetWorktreeCommand cmd -> rt |> Worktrees.getWorktree cmd |> serialize'
    | CreateWorktreeCommand cmd -> rt |> Worktrees.createWorktree cmd |> serialize'
    | InspectWorktreeRemovalCommand cmd -> rt |> Worktrees.inspectRemoval cmd |> serialize'
    | RemoveWorktreeCommand cmd -> rt |> Worktrees.removeWorktree cmd |> serialize'
    | TaskReadCommand cmd -> rt |> OrchestrationApi.TaskApi.read cmd |> serialize'
    | ObserveAgentCommand cmd ->
        rt |> OrchestrationApi.AgentObservationApi.observe cmd
            (fun project agent -> rt.Tui.GetAgentLastSeen(project, agent)) |> serialize'
    | TaskWriteCommand cmd when cmd.Action = "ExportTaskSpec" || cmd.Action = "ImportTaskSpec" ->
        match resolved with
        | Some context -> rt |> OrchestrationApi.TaskApi.writeForContext context cmd |> serialize'
        | None -> Error(Client.ValidationError "Task-spec source checkout was not resolved.") |> serialize'
    | TaskWriteCommand cmd -> rt |> OrchestrationApi.TaskApi.write cmd |> serialize'
    | CrossProjectNotificationCommand _ ->
        Error(Client.ValidationError "Forwarded notification must be normalized before client dispatch.") |> serialize'
    | LedgerReadCommand cmd -> rt |> OrchestrationApi.LedgerApi.read cmd |> serialize'
    | LedgerWriteCommand cmd -> rt |> OrchestrationApi.LedgerApi.write cmd |> serialize'
    | ReviewReadCommand cmd -> rt |> OrchestrationApi.ReviewApi.read cmd |> serialize'
    | ReviewWriteCommand cmd -> rt |> OrchestrationApi.ReviewApi.write cmd |> serialize'
    | RetryPendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.retry cmd |> serialize'
    | SupersedePendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.supersede cmd |> serialize'
    | AbandonPendingIntentCommand cmd -> rt |> OrchestrationApi.PendingRecoveryApi.abandon cmd |> serialize'
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
    | ReadFilePageCommand cmd ->
        rt
        |> sourceOperation resolved
            (fun project -> ProjectBrowser.readFilePageIn project cmd)
            (fun () -> ProjectBrowser.readFilePage cmd)
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
            rt |> JobManager.listJobsForProjectAsAgent (Some(ProjectPaths.ProjectTarget.projectId validated)) (Some agentId) cmd |> serialize'
        | None -> rt |> JobManager.listJobsForProjectAsAgent None (Some agentId) cmd |> serialize'
    | GetJobResultCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.getJobResultForProjectAsAgent (Some(ProjectPaths.ProjectTarget.projectId validated)) (Some agentId) cmd |> serialize'
        | None -> rt |> JobManager.getJobResultForProjectAsAgent None (Some agentId) cmd |> serialize'
    | CancelJobCommand cmd ->
        match target with
        | Some validated ->
            rt |> JobManager.cancelJobForProjectAsAgent (Some(ProjectPaths.ProjectTarget.projectId validated)) (Some agentId) cmd |> serialize'
        | None -> rt |> JobManager.cancelJobForProjectAsAgent None (Some agentId) cmd |> serialize'
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
                                    (preparedTask: ProjectTaskDefinition option) (agentId: string) (command: AgentCommand) =
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
                        let! result = dispatch rt target resolved agentId command
                        return result
            }

        stopwatch.Stop()

        match response with
        | Ok payload ->
            let outcome = ActivityPresentation.tryResultSummary command payload.Payload
            activityId
            |> Option.iter (fun id ->
                rt.Tui.CompleteActivity(id, stopwatch.ElapsedMilliseconds, outcome))
        | Error err ->
            activityId
            |> Option.iter (fun id ->
                rt.Tui.FailActivity(id, stopwatch.ElapsedMilliseconds, ActivityPresentation.shortError err, ActivityPresentation.fullError err))

        audit command (response |> Result.map (fun outcome -> outcome.Payload))

        return response
    }

let receiveCommand (rt: Runtime) (command: AgentCommand) =
    task {
        let! result = receiveCommandForTarget rt None None None "" command
        return result |> Result.map (fun outcome -> outcome.Payload)
    }

let private receiveCommandWithTypedInstructions (rt: Runtime)
                                                (target: ProjectPaths.ProjectTarget option) (agentId: string) command =
    task {
        match command with
        | GetJobResultCommand _ | ListJobsCommand _ | CancelJobCommand _ ->
            // Cheap controls must bypass source instruction preparation and
            // remain responsive even while source/ledger commands are blocked.
            let! response = receiveCommandForTarget rt target None None agentId command
            return response, [], AgentInstructions.RootBootstrapRequired
        | _ ->
            match AgentInstructions.prepareOperationWithTarget rt target command with
            | Error error ->
                return Error error, [], AgentInstructions.RootBootstrapRequired
            | Ok prepared ->
                let! response =
                    receiveCommandForTarget rt target prepared.Source prepared.ProjectTask agentId command
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
                receiveCommandWithTypedInstructions rt (projectTarget scope) "" command
            return response |> Result.map (fun outcome -> outcome.Payload), instructions
    }

// Shared by SignalR receive and tests. Identity is supplied by the MCP call,
// not inferred from provider conversation metadata.
let processIncomingCommand (rt: Runtime) (commandJson: string) (agentId: string) =
    task {
        let mutable parsedCommand = None
        let mutable instructions: InstructionDocument list = []
        let mutable implicitChanges: string list = []
        let mutable inboxActivation: AgentInboxActivation option = None
        let! response =
            task {
                try
                    let command = JsonSerializer.Deserialize<AgentCommand>(commandJson)
                    parsedCommand <- Some command
                    // A self-inbox response must explicitly distinguish a
                    // repeated read from a newly committed activation, even
                    // when the subsequent inbox read fails.
                    match command with
                    | TaskReadCommand inbox when inbox.Action = "AgentInbox" ->
                        inboxActivation <-
                            Some { ProjectName = inbox.ProjectName; AgentId = inbox.AgentId
                                   ActivatedByThisCall = false
                                   BeforeState = None; AfterState = None
                                   CommittedHead = None; OperationId = None }
                    | _ -> ()
                    // A target field is never proof of acting-agent identity.
                    // Reject forged observer identity before source resolution,
                    // any activity attribution, or the canonical read.
                    let project =
                        match command with
                        | ObserveAgentCommand observation
                            when String.IsNullOrWhiteSpace agentId
                                 || not (String.Equals(observation.ObserverAgentId, agentId, StringComparison.Ordinal)) ->
                            Error(Client.ValidationError "Observation actor label does not match the initiating tool-call actor.")
                        | _ -> classifyProject command
                    match project with
                    | Error error -> return Error error
                    | Ok scope ->
                        let target = projectTarget scope
                        // The caller's agentId belongs to the source project
                        // of a cross-project notification, not the destination.
                        let localAgentId =
                            // Job status, listing and cancellation are controls,
                            // not evidence that the agent resumed its task.
                            // Preserve the transport actor separately for job
                            // authorization, including unknown/expired IDs.
                            match command with
                            | GetJobResultCommand _ | ListJobsCommand _ | CancelJobCommand _ -> ""
                            | _ when isLocalAgentActivity command -> agentId
                            | _ -> ""
                        if not (String.IsNullOrWhiteSpace localAgentId) then
                            target |> Option.iter (fun selected ->
                                rt.Tui.RecordAgentSeen(AgentView.activityProject selected, localAgentId))
                        match activateReadyOnActivity rt localAgentId target command with
                        | Error error -> return Error error
                        | Ok (changes, committedActivation) ->
                            implicitChanges <- changes
                            match command, committedActivation with
                            | TaskReadCommand inbox, Some receipt when inbox.Action = "AgentInbox" ->
                                inboxActivation <- Some receipt
                            | _ -> ()
                            let! result, applicable, provenance =
                                // Polling does not activate or record local
                                // agent activity, but access checks still need
                                // the initiating actor label from the transport.
                                receiveCommandWithTypedInstructions rt target agentId command
                            // A write may alter another agent's state, or none
                            // at all. Invalidate precisely the affected local
                            // cache entries before preparing turn guidance.
                            match target, result with
                            | Some selected, Ok outcome when not outcome.ChangedAgentIds.IsEmpty ->
                                let cache = workingGuidanceCaches.GetValue(rt, fun _ ->
                                    WorkingTurnGuidance.AgentCache(TimeSpan.FromMinutes 2.))
                                let projectId = ProjectPaths.ProjectTarget.projectId selected
                                for changed in outcome.ChangedAgentIds do
                                    cache.Invalidate(projectId, changed)
                            | _ -> ()
                            instructions <-
                                match command with
                                | GetAgentInstructionsCommand _ ->
                                    // The explicit result includes the complete hierarchy.
                                    // Do not double-inject automatic AGENTS.md payloads.
                                    checkWorkingGuidance rt localAgentId target command
                                | _ ->
                                    incrementalInstructions rt localAgentId target provenance applicable
                                    @ checkWorkingGuidance rt localAgentId target command
                            return result
                with ex ->
                    return Error(ExceptionError ex)
            }
        // The metadata is independent of the requested command outcome. A
        // Ready->Working transition stays committed if the following file read
        // fails, so other devices must still expire that agent's cache.
        let changes =
            match parsedCommand |> Option.bind AgentCommandInfo.projectName with
            | Some project when not (String.IsNullOrWhiteSpace project) ->
                let executed =
                    match response with
                    | Ok outcome -> outcome.ChangedAgentIds
                    | Error _ -> []
                implicitChanges @ executed
                |> List.distinct
                |> List.map (fun id -> { ProjectName = project; AgentId = id })
            | _ -> []
        let agentResponse =
            match parsedCommand with
            | Some command -> toAgentResponse command instructions changes inboxActivation response
            | None -> toAgentResponse ListCommandsCommand [] changes inboxActivation response
        return parsedCommand, (response |> Result.map (fun outcome -> outcome.Payload)), agentResponse
    }

// Bounded, client-owned execution begins before the deadline. An HTTP
// request and SignalR delivery may end without cancelling this operation.
let private promotionThreshold () =
    match Int32.TryParse(Environment.GetEnvironmentVariable "JARVIS_TOOL_JOB_THRESHOLD_MS") with
    | true, milliseconds when milliseconds >= 250 && milliseconds <= 60000 ->
        TimeSpan.FromMilliseconds(float milliseconds)
    | _ -> TimeSpan.FromSeconds 3.

let private shouldAutoPromote = function
    // Cheap controls must never wait for the command worker slots. They can
    // poll or inspect a slow task while every worker is occupied.
    | StartJobCommand _ // Already returns its own OS process-job handle.
    | ListJobsCommand _
    | GetJobResultCommand _
    | CancelJobCommand _
    | UpdateClientCommand
    | MessageCommand _
    | GetClientActivityCommand _
    | ListCommandsCommand -> false
    | _ -> true

/// Source-side evidence is attached to the ORIGINAL destination client's
/// terminal response before completion is recorded in its job registry. Lost
/// pending handles can then be recovered via list_jobs/get_job_result without
/// reconstructing state evidence in a server-side cache.
let combineCrossProjectNotification (source: CrossProjectNotificationCommand)
                                    (destination: AgentCommandResponse) =
    let knownChanges =
        if isNull (box destination.AgentStateChanges) then
            Unchecked.defaultof<AgentStateChange list>
        else
            (source.SourceStateChanges @ destination.AgentStateChanges) |> List.distinct
    { destination with
        AgentStateChanges = knownChanges
        InboxActivation =
            match destination.InboxActivation with
            | Some receipt -> Some receipt
            | None ->
                source.SourceInboxActivation
                |> Option.filter (fun receipt -> receipt.ActivatedByThisCall) }

let private executeOrPromote (rt: Runtime) (commandJson: string) (agentId: string) =
    task {
        let command =
            try Some(JsonSerializer.Deserialize<AgentCommand>(commandJson))
            with _ -> None
        let _, normalizedJson, merge =
            match command with
            | Some (CrossProjectNotificationCommand envelope) ->
                let original = TaskWriteCommand envelope.Notification
                Some original, JsonSerializer.Serialize<AgentCommand>(original),
                (combineCrossProjectNotification envelope)
            | _ -> command, commandJson, id
        match command with
        | Some command when shouldAutoPromote command ->
            match classifyProject command with
            | Ok scope ->
                let project = AgentCommandInfo.projectName command |> Option.filter (String.IsNullOrWhiteSpace >> not)
                let canonical =
                    match scope with
                    | Checkout target -> Some(ProjectPaths.ProjectTarget.projectId target)
                    | ActivityLabel _ | Unscoped -> None
                let registered =
                    JobManager.startToolOperationWithCancelEvidence canonical project agentId
                        (AgentCommandInfo.protocolName command)
                        (AgentCommandInfo.operationId command
                         |> Option.filter (String.IsNullOrWhiteSpace >> not))
                        (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson))))
                        (fun () -> task {
                            let! _, _, outcome = processIncomingCommand rt normalizedJson agentId
                            return merge outcome
                        }) merge
                match registered with
                | Error error ->
                    return
                        { Result = None
                          Error = Some(EffectError.toAgentError error)
                          Instructions = []
                          AgentStateChanges = []
                          InboxActivation = None
                          PendingJob = None }
                | Ok operation ->
                    let! first = JobManager.awaitToolOperation operation (promotionThreshold ())
                    match first with
                    | Choice1Of2 response -> return response
                    | Choice2Of2 handle ->
                        // Do not invent state-change evidence: activation and
                        // canonical writes may still be running at this moment.
                        return
                            { Result = Some(JsonSerializer.Serialize handle)
                              Error = None
                              Instructions = []
                              // Unknown until the original command finishes.
                              // Null on this one pending wire envelope means
                              // "not yet known", never "there were no writes".
                              AgentStateChanges = Unchecked.defaultof<_>
                              InboxActivation = None
                              PendingJob = Some handle }
            | Error _ ->
                let! _, _, outcome = processIncomingCommand rt normalizedJson agentId
                return merge outcome
        | _ ->
            let! _, _, outcome = processIncomingCommand rt normalizedJson agentId
            return merge outcome
    }

let private receiveCommandAndReplyCore (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) (agentId: string) : Task =
    task {
        let! agentResponse = executeOrPromote rt commandJson agentId
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

        let updatedSuccessfully =
            match agentResponse.Result, agentResponse.Error with
            | Some _, None -> true
            | _ -> false
        let isUpdate =
            try JsonSerializer.Deserialize<AgentCommand>(commandJson) = UpdateClientCommand
            with _ -> false
        match sendResult, isUpdate, updatedSuccessfully with
        | Ok(), true, true ->
            rt.Tui.Log "Update verified and installed; restarting Jarvis in this terminal."
            match SelfUpdate.activatePending () with
            | Ok _ -> ()
            | Error error -> rt.Tui.Log $"Self-update activation failed: {EffectError.toString error}"
        | _ -> ()
    }

let private commandIngress = ConditionalWeakTable<Runtime, BoundedResponseIngress>()

/// Return to SignalR ingress immediately after bounded admission. The
/// response task remains tracked until it is sent (or delivery fails), so
/// concurrent cheap polling is not serialized behind a 3-second deadline.
let receiveCommandAndReply (connection: HubConnection) (rt: Runtime) (correlationId: string) (commandJson: string) (agentId: string) : Task =
    let ingress =
        commandIngress.GetValue(rt, fun _ ->
            BoundedResponseIngress(96, fun ex ->
                rt.Tui.Log $"Client command response failed: {ex.Message}"))
    match ingress.TryDispatch(correlationId, fun () ->
        receiveCommandAndReplyCore connection rt correlationId commandJson agentId) with
    | Ok _ -> Task.CompletedTask
    | Error reason -> Task.FromException(InvalidOperationException reason)
